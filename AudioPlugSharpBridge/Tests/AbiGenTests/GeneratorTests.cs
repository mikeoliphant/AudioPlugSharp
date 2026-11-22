using Xunit;
using static AbiGen.Tests.ContractCompiler;

namespace AbiGen.Tests;

public class ContractTests
{
    private const string Header = "using AudioPlugSharpBridge; namespace T;";

    private static string Api(string members, string extra = "") => $$"""
        {{Header}}
        {{extra}}
        [ManagedApi("thing")]
        internal unsafe interface IThing
        {
            {{members}}
        }
        """;

    [Fact]
    public void RealContract_Loads()
    {
        var model = Load(Resource("Contract.cs"));
        Assert.Equal(2, model.Interfaces.Count);
        var plugin = model.Interfaces.Single(i => i.ApiName == "plugin");
        Assert.Equal(ApiDirection.Managed, plugin.Direction);
        Assert.Equal(ThreadKind.Audio, plugin.Methods.Single(m => m.Name == "Process").Thread);
    }

    [Fact]
    public void RealContract_LayoutMatchesKnownSizes()
    {
        var model = Load(Resource("Contract.cs"));
        int Size(string name) => Layout.Compute(model.Structs.Single(s => s.Name == name)).Size;
        Assert.Equal(24, Size("AudioEvent"));
        Assert.Equal(16, Size("AudioPortBuffers"));
        Assert.Equal(80, Size("ProcessData"));
        Assert.Equal(728, Size("PluginInfo"));
        Assert.Equal(152, Size("ParamInfo"));
    }

    [Fact]
    public void Layout_UsesNaturalAlignment()
    {
        var model = Load($$"""
            {{Header}}
            [AbiStruct] internal struct S { public byte A; public double B; public int C; }
            [ManagedApi("thing")] internal interface IThing { [MainThread] void Set(S s); }
            """);
        var layout = Layout.Compute(model.Structs.Single());
        Assert.Equal([0, 8, 16], layout.Fields.Select(f => f.Offset));
        Assert.Equal(24, layout.Size);
    }

    [Fact]
    public void AudioThread_RejectsAllocatingTypes()
    {
        var errors = Errors(Api("[AudioThread] void Log(string message);"));
        Assert.Contains(errors, e => e.Contains("would allocate"));

        errors = Errors(Api("[AudioThread] byte[] Dump();"));
        Assert.Contains(errors, e => e.Contains("would allocate"));
    }

    [Fact]
    public void AudioThread_AllowsBlittableTypes()
    {
        var errors = Errors(Api("[AudioThread] void Run(in S data, ReadOnlySpan<float> samples, float* scratch, uint count);",
            "[AbiStruct] internal struct S { public int X; }"));
        Assert.Empty(errors);
    }

    [Fact]
    public void Methods_NeedExactlyOneThreadAttribute()
    {
        Assert.Contains(Errors(Api("void Run();")), e => e.Contains("exactly one of"));
        Assert.Contains(Errors(Api("[MainThread, AudioThread] void Run();")), e => e.Contains("exactly one of"));
    }

    [Fact]
    public void Overloads_AreRejected()
    {
        var errors = Errors(Api("[MainThread] void Run(); [MainThread] void Run(int x);"));
        Assert.Contains(errors, e => e.Contains("overloads"));
    }

    [Fact]
    public void Strings_NeedFixedStringInRecords()
    {
        var errors = Errors(Api("[MainThread] R Get();", "[AbiRecord] internal struct R { public string Name; }"));
        Assert.Contains(errors, e => e.Contains("[FixedString"));

        errors = Errors(Api("[MainThread] void Set(S s);", "[AbiStruct] internal struct S { [FixedString(8)] public string Name; }"));
        Assert.Contains(errors, e => e.Contains("only allowed in [AbiRecord]"));
    }

    [Fact]
    public void UnmarkedStruct_IsRejected()
    {
        var errors = Errors(Api("[MainThread] void Set(S s);", "internal struct S { public int X; }"));
        Assert.Contains(errors, e => e.Contains("unsupported type"));
    }

    [Theory]
    [InlineData("GetParamInfo", "get_param_info")]
    [InlineData("GCPauseMs", "gc_pause_ms")]
    [InlineData("NumInputs", "num_inputs")]
    [InlineData("Log", "log")]
    public void Snake_Case(string input, string expected) => Assert.Equal(expected, Naming.Snake(input));
}

public class ManifestTests
{
    private const string Header = "using AudioPlugSharpBridge; namespace T;";

    private static Manifest Build(string members, int version = 1, string extra = "") =>
        Manifest.Build(Load($$"""
            using AudioPlugSharpBridge;
            [assembly: AbiConfig(Version = {{version}})]
            namespace T;
            {{extra}}
            [ManagedApi("thing")]
            internal interface IThing
            {
                {{members}}
            }
            """));

    private const string V1 = "[MainThread] void A(); [MainThread] int B(uint x);";

    [Fact]
    public void Unchanged_IsCompatible() => Assert.Empty(Build(V1).CheckCompatibleWith(Build(V1)));

    [Fact]
    public void AppendedOptionalMember_IsCompatible() =>
        Assert.Empty(Build(V1 + " [MainThread, OptionalMember] void C();").CheckCompatibleWith(Build(V1)));

    [Fact]
    public void AppendedRequiredMember_IsRejected()
    {
        var errors = Build(V1 + " [MainThread] void C();").CheckCompatibleWith(Build(V1));
        Assert.Contains(errors, e => e.Contains("must be [OptionalMember]"));
    }

    [Fact]
    public void ReorderedMembers_AreRejected()
    {
        var errors = Build("[MainThread] int B(uint x); [MainThread] void A();").CheckCompatibleWith(Build(V1));
        Assert.Contains(errors, e => e.Contains("may only be appended"));
    }

    [Fact]
    public void ChangedSignature_IsRejected()
    {
        var errors = Build("[MainThread] void A(); [MainThread] int B(ulong x);").CheckCompatibleWith(Build(V1));
        Assert.Contains(errors, e => e.Contains("may only be appended"));
    }

    [Fact]
    public void ChangedThread_IsRejected()
    {
        var errors = Build("[MainThread] void A(); [AudioThread] int B(uint x);").CheckCompatibleWith(Build(V1));
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void ChangedStruct_IsRejected_AddedEnumMember_IsNot()
    {
        const string members = "[MainThread] void Set(S s, E e);";
        var old = Build(members, extra: "[AbiStruct] internal struct S { public int X; } internal enum E : int { A = 0 }");
        var changed = Build(members, extra: "[AbiStruct] internal struct S { public int X; public int Y; } internal enum E : int { A = 0 }");
        var enumAdded = Build(members, extra: "[AbiStruct] internal struct S { public int X; } internal enum E : int { A = 0, B = 1 }");

        Assert.Contains(changed.CheckCompatibleWith(old), e => e.Contains("fields changed"));
        Assert.Empty(enumAdded.CheckCompatibleWith(old));
    }

    [Fact]
    public void VersionBump_AllowsBreakingChanges() =>
        Assert.Empty(Build("[MainThread] void Z();", version: 2).CheckCompatibleWith(Build(V1)));

    [Fact]
    public void VersionDecrease_IsRejected() =>
        Assert.NotEmpty(Build(V1, version: 1).CheckCompatibleWith(Build(V1, version: 2)));

    [Fact]
    public void Serialize_RoundTrips()
    {
        var m = Build(V1);
        var path = Path.Combine(Path.GetTempPath(), "abigen-tests", Guid.NewGuid() + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, m.Serialize());
        Assert.Empty(m.CheckCompatibleWith(Manifest.Load(path)!));
        Assert.Equal(m.Serialize(), Manifest.Load(path)!.Serialize());
    }
}
