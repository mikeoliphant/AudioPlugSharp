namespace AbiGen;

public enum Prim { Void, Bool, I8, U8, I16, U16, I32, U32, I64, U64, F32, F64, IntPtr, UIntPtr }

public abstract record TypeRef;
public sealed record PrimType(Prim Kind) : TypeRef;
public sealed record EnumType(EnumDef Def) : TypeRef;
public sealed record StructType(StructDef Def) : TypeRef;
public sealed record PointerType(TypeRef Element) : TypeRef;
public sealed record StringType : TypeRef;
public sealed record SpanType(TypeRef Element, bool ReadOnly) : TypeRef;
public sealed record ArrayType(TypeRef Element) : TypeRef;

public sealed class EnumDef
{
    public required string Name { get; init; }
    public required string FullName { get; init; }
    public Prim Underlying { get; set; }
    public bool IsFlags { get; set; }
    public List<EnumMember> Members { get; } = new();
    public string? Doc { get; set; }
}

public sealed record EnumMember(string Name, long Value, string? Doc);

public sealed class StructDef
{
    public required string Name { get; init; }
    public required string FullName { get; init; }
    /// <summary>True for [AbiRecord] (marshalled), false for [AbiStruct] (blittable, shared as-is).</summary>
    public bool IsRecord { get; init; }
    public List<FieldDef> Fields { get; } = new();
    public string? Doc { get; set; }
}

public sealed class FieldDef
{
    public required string Name { get; init; }
    public required TypeRef Type { get; init; }
    public int? FixedString { get; init; }
    public bool Const { get; init; }
    public string? Doc { get; init; }
}

public enum ApiDirection
{
    /// <summary>Implemented in C#, called from native code.</summary>
    Managed,
    /// <summary>Implemented natively, called from C#.</summary>
    Native,
}

public enum ThreadKind { Main, Audio }

public enum ParamMod { None, In, Ref, Out }

public sealed record ParamDef(string Name, TypeRef Type, ParamMod Mod);

public sealed class MethodDef
{
    public required string Name { get; init; }
    public ThreadKind Thread { get; init; }
    public bool Optional { get; init; }
    public List<ParamDef> Params { get; } = new();
    public required TypeRef Return { get; init; }
    public string? Doc { get; init; }
}

public sealed class InterfaceDef
{
    public required string ClrName { get; init; }
    public required string FullName { get; init; }
    /// <summary>Short name from the attribute, e.g. "plugin" -> pt_plugin_api.</summary>
    public required string ApiName { get; init; }
    public ApiDirection Direction { get; init; }
    public List<MethodDef> Methods { get; } = new();
    public string? Doc { get; init; }
}

public sealed class AbiModel
{
    public int Version { get; set; } = 1;
    public string CPrefix { get; set; } = "pt";
    public string CppNamespace { get; set; } = "pt";
    public string CSharpNamespace { get; set; } = "";
    public string ContractAssembly { get; set; } = "";
    public List<EnumDef> Enums { get; } = new();
    /// <summary>Structs and records, in dependency order (dependencies first).</summary>
    public List<StructDef> Structs { get; } = new();
    public List<InterfaceDef> Interfaces { get; } = new();
}

public sealed class AbiGenException(IEnumerable<string> errors) : Exception(string.Join(Environment.NewLine, errors))
{
    public IReadOnlyList<string> Errors { get; } = errors.ToList();
}
