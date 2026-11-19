namespace AbiGen;

/// <summary>How one contract parameter or return value appears in the native signature.</summary>
public enum NKind
{
    Value,       // T (primitive, enum, AbiStruct by value, pointer)
    Bool,        // uint8_t
    String,      // const char*
    StructIn,    // const T*
    StructRef,   // T*
    StructOut,   // T*
    PrimRef,     // T* (primitive or enum)
    PrimOut,     // T* (primitive or enum)
    RecordIn,    // const T_native*
    SpanData,    // [const] T*
    SpanCount,   // uint32_t
    RetValue,    // T* out_value
    RetBool,     // uint8_t* out_value
    RetRecord,   // T_native* out_value
    ArrBuffer,   // T* buffer
    ArrCapacity, // uint32_t capacity
    ArrCount,    // uint32_t* out_count
}

/// <summary>A native parameter. <see cref="Type"/> is the value type (element type for spans and arrays).</summary>
public sealed record NParam(string CName, string CsName, NKind Kind, TypeRef Type, ParamDef? Source, bool ReadOnly = false);

public sealed class NMethod
{
    public required MethodDef Method { get; init; }
    public required string CName { get; init; }
    public List<NParam> Params { get; } = new();
    public bool IsDestroy { get; init; }
}

public static class Lowering
{
    public static List<NMethod> Lower(InterfaceDef iface)
    {
        var result = new List<NMethod>();
        if (iface.Direction == ApiDirection.Managed)
        {
            result.Add(new NMethod
            {
                Method = new MethodDef { Name = "Destroy", Thread = ThreadKind.Main, Return = new PrimType(Prim.Void), Doc = "Destroys the instance. self is invalid afterwards." },
                CName = "destroy",
                IsDestroy = true,
            });
        }
        foreach (var m in iface.Methods) result.Add(Lower(m));
        return result;
    }

    private static NMethod Lower(MethodDef m)
    {
        var nm = new NMethod { Method = m, CName = Naming.SafeC(Naming.Snake(m.Name)) };
        foreach (var p in m.Params)
        {
            var c = Naming.SafeC(Naming.Snake(p.Name));
            var cs = p.Name;
            switch (p.Mod, p.Type)
            {
                case (ParamMod.None, PrimType { Kind: Prim.Bool }):
                    nm.Params.Add(new NParam(c, cs, NKind.Bool, p.Type, p)); break;
                case (ParamMod.None or ParamMod.In, StructType { Def.IsRecord: true }):
                    nm.Params.Add(new NParam(c, cs, NKind.RecordIn, p.Type, p)); break;
                case (ParamMod.None, StringType):
                    nm.Params.Add(new NParam(c, cs, NKind.String, p.Type, p)); break;
                case (ParamMod.None, SpanType s):
                    nm.Params.Add(new NParam(c, cs, NKind.SpanData, s.Element, p, s.ReadOnly));
                    nm.Params.Add(new NParam(c + "_count", cs + "Count", NKind.SpanCount, new PrimType(Prim.U32), p));
                    break;
                case (ParamMod.In, StructType):
                    nm.Params.Add(new NParam(c, cs, NKind.StructIn, p.Type, p)); break;
                case (ParamMod.Ref, StructType):
                    nm.Params.Add(new NParam(c, cs, NKind.StructRef, p.Type, p)); break;
                case (ParamMod.Out, StructType):
                    nm.Params.Add(new NParam(c, cs, NKind.StructOut, p.Type, p)); break;
                case (ParamMod.Ref, _):
                    nm.Params.Add(new NParam(c, cs, NKind.PrimRef, p.Type, p)); break;
                case (ParamMod.Out, _):
                    nm.Params.Add(new NParam(c, cs, NKind.PrimOut, p.Type, p)); break;
                default: // None/In value types
                    nm.Params.Add(new NParam(c, cs, NKind.Value, p.Type, p)); break;
            }
        }

        switch (m.Return)
        {
            case PrimType { Kind: Prim.Void }: break;
            case PrimType { Kind: Prim.Bool }:
                nm.Params.Add(new NParam("out_value", "outValue", NKind.RetBool, m.Return, null)); break;
            case StructType { Def.IsRecord: true }:
                nm.Params.Add(new NParam("out_value", "outValue", NKind.RetRecord, m.Return, null)); break;
            case ArrayType a:
                nm.Params.Add(new NParam("buffer", "buffer", NKind.ArrBuffer, a.Element, null));
                nm.Params.Add(new NParam("capacity", "capacity", NKind.ArrCapacity, new PrimType(Prim.U32), null));
                nm.Params.Add(new NParam("out_count", "outCount", NKind.ArrCount, new PrimType(Prim.U32), null));
                break;
            default:
                nm.Params.Add(new NParam("out_value", "outValue", NKind.RetValue, m.Return, null)); break;
        }
        return nm;
    }

    /// <summary>Kinds whose native argument is a pointer that must not be null.</summary>
    public static bool IsRequiredPointer(NKind k) => k is NKind.StructIn or NKind.StructRef or NKind.StructOut or NKind.PrimRef
        or NKind.PrimOut or NKind.RecordIn or NKind.RetValue or NKind.RetBool or NKind.RetRecord or NKind.ArrCount;
}

/// <summary>C type names for the model.</summary>
public sealed class CTypes(AbiModel model)
{
    public string Prefix => model.CPrefix;
    public string Macro => model.CPrefix.ToUpperInvariant();

    public string Name(EnumDef e) => $"{Prefix}_{Naming.Snake(e.Name)}";
    public string Name(StructDef s) => $"{Prefix}_{Naming.Snake(s.Name)}";
    public string Name(InterfaceDef i) => $"{Prefix}_{i.ApiName}_api";
    public string EnumConst(EnumDef e, EnumMember m) => $"{Macro}_{Naming.UpperSnake(e.Name)}_{Naming.UpperSnake(m.Name)}";

    public static string Prim(Prim p) => p switch
    {
        AbiGen.Prim.Void => "void",
        AbiGen.Prim.Bool => "uint8_t",
        AbiGen.Prim.I8 => "int8_t",
        AbiGen.Prim.U8 => "uint8_t",
        AbiGen.Prim.I16 => "int16_t",
        AbiGen.Prim.U16 => "uint16_t",
        AbiGen.Prim.I32 => "int32_t",
        AbiGen.Prim.U32 => "uint32_t",
        AbiGen.Prim.I64 => "int64_t",
        AbiGen.Prim.U64 => "uint64_t",
        AbiGen.Prim.F32 => "float",
        AbiGen.Prim.F64 => "double",
        AbiGen.Prim.IntPtr => "intptr_t",
        AbiGen.Prim.UIntPtr => "uintptr_t",
        _ => throw new ArgumentOutOfRangeException(nameof(p)),
    };

    public string Type(TypeRef t) => t switch
    {
        PrimType p => Prim(p.Kind),
        EnumType e => Name(e.Def),
        StructType s => Name(s.Def),
        PointerType p => Type(p.Element) + "*",
        StringType => "const char*",
        _ => throw new InvalidOperationException($"no C type for {t}"),
    };

    /// <summary>A pointer type with every pointee level const: float** -> const float* const*.</summary>
    public string ConstPointer(PointerType p)
    {
        int levels = 0;
        TypeRef cur = p;
        while (cur is PointerType q) { levels++; cur = q.Element; }
        var s = "const " + Type(cur);
        for (int i = 0; i < levels - 1; i++) s += "* const";
        return s + "*";
    }

    public string FieldDecl(FieldDef f)
    {
        var name = Naming.SafeC(Naming.Snake(f.Name));
        if (f.FixedString is { } len) return $"char {name}[{len}]";
        var type = f.Const && f.Type is PointerType p ? ConstPointer(p) : Type(f.Type);
        return $"{type} {name}";
    }

    public string ParamType(NParam p) => p.Kind switch
    {
        NKind.Value => Type(p.Type),
        NKind.Bool => "uint8_t",
        NKind.String => "const char*",
        NKind.StructIn => $"const {Type(p.Type)}*",
        NKind.StructRef or NKind.StructOut or NKind.PrimRef or NKind.PrimOut => $"{Type(p.Type)}*",
        NKind.RecordIn => $"const {Type(p.Type)}*",
        NKind.SpanData => p.ReadOnly ? $"const {Type(p.Type)}*" : $"{Type(p.Type)}*",
        NKind.SpanCount or NKind.ArrCapacity => "uint32_t",
        NKind.RetValue or NKind.RetRecord => $"{Type(p.Type)}*",
        NKind.RetBool => "uint8_t*",
        NKind.ArrBuffer => $"{Type(p.Type)}*",
        NKind.ArrCount => "uint32_t*",
        _ => throw new ArgumentOutOfRangeException(),
    };

    public string SelfName(InterfaceDef i) => i.Direction == ApiDirection.Managed ? "self" : "ctx";

    /// <summary>Full C parameter list including self/ctx, e.g. "void* self, uint32_t index, pt_param_info* out_value".</summary>
    public string Signature(InterfaceDef i, NMethod m) =>
        string.Join(", ", new[] { $"void* {SelfName(i)}" }.Concat(m.Params.Select(p => $"{ParamType(p)} {p.CName}")));

    public string ReturnType(NMethod m) => m.IsDestroy ? "void" : $"{Prefix}_result";
}

/// <summary>Natural C layout (x64 / arm64: 8-byte pointers) for structs, records and vtables.</summary>
public static class Layout
{
    public sealed record FieldLayout(string CName, int Offset, int Size);
    public sealed record StructLayout(int Size, int Align, List<FieldLayout> Fields);

    public static (int Size, int Align) Of(TypeRef t) => t switch
    {
        PrimType p => p.Kind switch
        {
            Prim.Bool or Prim.I8 or Prim.U8 => (1, 1),
            Prim.I16 or Prim.U16 => (2, 2),
            Prim.I32 or Prim.U32 or Prim.F32 => (4, 4),
            Prim.I64 or Prim.U64 or Prim.F64 or Prim.IntPtr or Prim.UIntPtr => (8, 8),
            _ => throw new InvalidOperationException("void has no size"),
        },
        EnumType e => Of(new PrimType(e.Def.Underlying)),
        PointerType => (8, 8),
        StructType s => Compute(s.Def) is var l ? (l.Size, l.Align) : default,
        _ => throw new InvalidOperationException($"no layout for {t}"),
    };

    public static StructLayout Compute(StructDef def)
    {
        int offset = 0, align = 1;
        var fields = new List<FieldLayout>();
        foreach (var f in def.Fields)
        {
            var (size, a) = f.FixedString is { } len ? (len, 1) : Of(f.Type);
            offset = AlignUp(offset, a);
            fields.Add(new FieldLayout(Naming.SafeC(Naming.Snake(f.Name)), offset, size));
            offset += size;
            align = Math.Max(align, a);
        }
        return new StructLayout(AlignUp(offset, align), align, fields);
    }

    /// <summary>vtable: uint32 struct_size, uint32 abi_version, void* self/ctx, then one function pointer per member.</summary>
    public static StructLayout VTable(List<NMethod> methods, string selfName)
    {
        var fields = new List<FieldLayout> { new("struct_size", 0, 4), new("abi_version", 4, 4), new(selfName, 8, 8) };
        int offset = 16;
        foreach (var m in methods) { fields.Add(new FieldLayout(m.CName, offset, 8)); offset += 8; }
        return new StructLayout(offset, 8, fields);
    }

    private static int AlignUp(int v, int a) => (v + a - 1) / a * a;
}
