using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace AbiGen.Tests;

/// <summary>Compiles contract source text (plus the real ABI attributes) into a temporary assembly.</summary>
internal static class ContractCompiler
{
    private static readonly Lazy<MetadataReference[]> References = new(() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => Path.GetFileName(p).StartsWith("System.") || Path.GetFileName(p) is "netstandard.dll" or "mscorlib.dll")
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToArray());

    public static string Resource(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)!;
        return new StreamReader(s).ReadToEnd();
    }

    /// <summary>Compiles the sources; returns the path of the written assembly.</summary>
    public static string Compile(params string[] sources)
    {
        var trees = sources.Append(Resource("AbiAttributes.cs"))
            .Prepend("global using System;")
            .Select(s => CSharpSyntaxTree.ParseText(s, new CSharpParseOptions(LanguageVersion.Latest)));
        var compilation = CSharpCompilation.Create(
            "TestContract" + Guid.NewGuid().ToString("N"),
            trees,
            References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        var dir = Path.Combine(Path.GetTempPath(), "abigen-tests");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, compilation.AssemblyName + ".dll");
        var result = compilation.Emit(path);
        var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (errors.Count > 0) throw new InvalidOperationException(string.Join("\n", errors));
        return path;
    }

    public static AbiModel Load(params string[] sources) => ContractLoader.Load(Compile(sources));

    public static IReadOnlyList<string> Errors(params string[] sources)
    {
        try
        {
            Load(sources);
            return [];
        }
        catch (AbiGenException e)
        {
            return e.Errors;
        }
    }
}
