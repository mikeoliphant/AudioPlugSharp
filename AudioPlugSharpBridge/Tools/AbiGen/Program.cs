using AbiGen;

// AbiGen: generates the native/managed ABI from the contract assembly. See README.md.
//
//   AbiGen --contract <Contract.dll> --manifest <abi-manifest.json> --c-header <abi.h>
//          --cpp-header <abi.hpp> --csharp-dir <dir> [--check]
//
// Normal mode writes changed files only. --check writes nothing and fails if any output is out of
// date (for CI). Both modes fail on incompatible ABI changes (see Manifest).

var opts = new Dictionary<string, string>();
bool check = false;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--check") { check = true; continue; }
    if (args[i].StartsWith("--") && i + 1 < args.Length) { opts[args[i][2..]] = args[++i]; continue; }
    return Fail($"unexpected argument '{args[i]}'");
}
foreach (var required in new[] { "contract", "manifest", "c-header", "cpp-header", "csharp-dir" })
    if (!opts.ContainsKey(required)) return Fail($"missing --{required}");

AbiModel model;
try
{
    model = ContractLoader.Load(opts["contract"]);
}
catch (AbiGenException e)
{
    foreach (var error in e.Errors) Console.Error.WriteLine($"AbiGen : error AG001 : {error}");
    return 1;
}

var manifest = Manifest.Build(model);
var oldManifest = Manifest.Load(opts["manifest"]);
if (oldManifest != null)
{
    var compat = manifest.CheckCompatibleWith(oldManifest);
    if (compat.Count > 0)
    {
        foreach (var error in compat) Console.Error.WriteLine($"AbiGen : error AG002 : {error}");
        return 1;
    }
}

var outputs = new Dictionary<string, string>
{
    [opts["c-header"]] = CEmitter.Emit(model),
    [opts["cpp-header"]] = CppEmitter.Emit(model),
    [opts["manifest"]] = manifest.Serialize(),
};
foreach (var (name, content) in CSharpEmitter.Emit(model))
    outputs[Path.Combine(opts["csharp-dir"], name)] = content;

int changed = 0;
foreach (var (path, content) in outputs)
{
    var existing = File.Exists(path) ? File.ReadAllText(path).ReplaceLineEndings("\n") : null;
    if (existing == content) continue;
    changed++;
    if (check)
    {
        Console.Error.WriteLine($"AbiGen : error AG003 : {path} is out of date; run the build (or AbiGen) and commit the result");
        continue;
    }
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    File.WriteAllText(path, content);
    Console.WriteLine($"AbiGen: wrote {path}");
}

if (check && changed > 0) return 1;
Console.WriteLine(check
    ? $"AbiGen: {outputs.Count} files up to date"
    : $"AbiGen: {changed} file(s) updated, {outputs.Count - changed} unchanged");
return 0;

static int Fail(string message)
{
    Console.Error.WriteLine($"AbiGen : error AG000 : {message}");
    return 2;
}
