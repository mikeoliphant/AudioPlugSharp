namespace AbiGen;

/// <summary>Enforces the ABI type rules (see README.md). Anything outside them is a build error.</summary>
public static class Validator
{
    public static void Validate(AbiModel model, List<string> errors)
    {
        foreach (var s in model.Structs)
        {
            foreach (var f in s.Fields)
            {
                var where = $"{s.Name}.{f.Name}";
                if (f.Const && f.Type is not PointerType) errors.Add($"{where}: [Const] is only valid on pointer fields");
                if (f.FixedString is { } len)
                {
                    if (!s.IsRecord) errors.Add($"{where}: [FixedString] is only allowed in [AbiRecord] types");
                    if (f.Type is not StringType) errors.Add($"{where}: [FixedString] requires a string field");
                    if (len < 2) errors.Add($"{where}: [FixedString] length must be at least 2");
                    continue;
                }
                if (f.Type is StringType) { errors.Add($"{where}: string fields need [FixedString(n)] (and are only allowed in [AbiRecord] types)"); continue; }
                if (s.IsRecord && f.Type is PointerType) { errors.Add($"{where}: pointers are not allowed in [AbiRecord] types"); continue; }
                if (!IsBlittableField(f.Type)) errors.Add($"{where}: type not allowed in an ABI struct ({Describe(f.Type)})");
            }
        }

        foreach (var iface in model.Interfaces)
        {
            if (iface.Methods.Count == 0) errors.Add($"{iface.ClrName}: has no methods");
            foreach (var m in iface.Methods)
            {
                var where = $"{iface.ClrName}.{m.Name}";
                foreach (var p in m.Params) CheckParam($"{where}({p.Name})", p, m.Thread, errors);
                CheckReturn(where, m.Return, m.Thread, errors);
            }
        }
    }

    private static void CheckParam(string where, ParamDef p, ThreadKind thread, List<string> errors)
    {
        var t = p.Type;
        bool ok = p.Mod switch
        {
            ParamMod.None => t switch
            {
                PrimType { Kind: not Prim.Void } => true,
                EnumType or PointerType => IsValidPointerTarget(t),
                StructType => true, // AbiStruct by value, or record (passed as const pointer)
                StringType => true,
                SpanType s => IsSpanElement(s.Element),
                _ => false,
            },
            ParamMod.In => t is StructType or EnumType || t is PrimType { Kind: not Prim.Void and not Prim.Bool },
            ParamMod.Ref or ParamMod.Out => t is EnumType || t is StructType { Def.IsRecord: false } || t is PrimType { Kind: not Prim.Void and not Prim.Bool },
            _ => false,
        };
        if (!ok) { errors.Add($"{where}: unsupported parameter type {Modifier(p.Mod)}{Describe(t)}"); return; }

        if (thread == ThreadKind.Audio && !IsRealtimeSafe(t))
            errors.Add($"{where}: {Describe(t)} would allocate; not allowed on an [AudioThread] member");
    }

    private static void CheckReturn(string where, TypeRef t, ThreadKind thread, List<string> errors)
    {
        bool ok = t switch
        {
            PrimType => true,
            EnumType or StructType => true,
            PointerType => IsValidPointerTarget(t),
            ArrayType a => IsArrayElement(a.Element),
            _ => false,
        };
        if (!ok) { errors.Add($"{where}: unsupported return type {Describe(t)}"); return; }
        if (thread == ThreadKind.Audio && !IsRealtimeSafe(t))
            errors.Add($"{where}: returning {Describe(t)} would allocate; not allowed on an [AudioThread] member");
    }

    private static bool IsBlittableField(TypeRef t) => t switch
    {
        PrimType p => p.Kind is not (Prim.Void or Prim.Bool),
        EnumType => true,
        StructType s => !s.Def.IsRecord,
        PointerType => IsValidPointerTarget(t),
        _ => false,
    };

    private static bool IsValidPointerTarget(TypeRef t) => t switch
    {
        PointerType p => p.Element switch
        {
            PrimType => true, // includes void*
            EnumType => true,
            StructType s => !s.Def.IsRecord,
            PointerType => IsValidPointerTarget(p.Element),
            _ => false,
        },
        _ => true,
    };

    private static bool IsSpanElement(TypeRef t) => t switch
    {
        PrimType p => p.Kind is not (Prim.Void or Prim.Bool),
        EnumType => true,
        StructType s => !s.Def.IsRecord,
        PointerType => IsValidPointerTarget(t),
        _ => false,
    };

    private static bool IsArrayElement(TypeRef t) => t switch
    {
        PrimType p => p.Kind is not (Prim.Void or Prim.Bool),
        EnumType => true,
        StructType s => !s.Def.IsRecord,
        _ => false,
    };

    /// <summary>Types whose marshalling never allocates.</summary>
    public static bool IsRealtimeSafe(TypeRef t) => t switch
    {
        PrimType or EnumType or PointerType => true,
        StructType s => !s.Def.IsRecord,
        SpanType => true,
        _ => false, // strings, records, arrays
    };

    private static string Modifier(ParamMod m) => m switch { ParamMod.In => "in ", ParamMod.Ref => "ref ", ParamMod.Out => "out ", _ => "" };

    public static string Describe(TypeRef t) => t switch
    {
        PrimType p => p.Kind.ToString(),
        EnumType e => e.Def.Name,
        StructType s => s.Def.Name + (s.Def.IsRecord ? " (record)" : ""),
        PointerType p => Describe(p.Element) + "*",
        StringType => "string",
        SpanType s => (s.ReadOnly ? "ReadOnlySpan<" : "Span<") + Describe(s.Element) + ">",
        ArrayType a => Describe(a.Element) + "[]",
        _ => t.ToString(),
    };
}
