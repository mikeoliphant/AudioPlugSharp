using System.Reflection;
using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace AbiGen;

/// <summary>Reads the contract assembly as metadata (MetadataLoadContext) and builds a validated AbiModel.</summary>
public static class ContractLoader
{
    private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static AbiModel Load(string contractPath)
    {
        contractPath = Path.GetFullPath(contractPath);
        var paths = Directory.GetFiles(RuntimeEnvironment.GetRuntimeDirectory(), "*.dll").ToList();
        paths.Add(contractPath);

        using var mlc = new MetadataLoadContext(new PathAssemblyResolver(paths));
        var assembly = mlc.LoadFromAssemblyPath(contractPath);
        var docs = Docs.Load(Path.ChangeExtension(contractPath, ".xml"));
        var errors = new List<string>();
        var model = new AbiModel { ContractAssembly = assembly.GetName().Name ?? "" };

        ReadConfig(assembly, model);

        var types = assembly.GetTypes().Where(t => t.DeclaringType == null).ToList();
        var enums = new Dictionary<string, EnumDef>();
        var structs = new Dictionary<string, StructDef>();
        var structTypes = new List<(Type Type, StructDef Def)>();

        // Pass 1: declare enums and structs so fields and signatures can reference them.
        foreach (var t in types)
        {
            if (t.IsEnum)
            {
                if (t.Namespace == null) continue;
                var def = new EnumDef { Name = t.Name, FullName = t.FullName!, Doc = docs.Get("T", t.FullName!) };
                def.Underlying = MapPrimitive(t.GetEnumUnderlyingType()) ?? Prim.I32;
                def.IsFlags = HasAttribute(t, "FlagsAttribute");
                foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Static))
                    def.Members.Add(new EnumMember(f.Name, Convert.ToInt64(f.GetRawConstantValue()), docs.Get("F", $"{t.FullName}.{f.Name}")));
                enums[def.FullName] = def;
            }
            else if (t.IsValueType && (HasAttribute(t, "AbiStructAttribute") || HasAttribute(t, "AbiRecordAttribute")))
            {
                var def = new StructDef
                {
                    Name = t.Name,
                    FullName = t.FullName!,
                    IsRecord = HasAttribute(t, "AbiRecordAttribute"),
                    Doc = docs.Get("T", t.FullName!),
                };
                structs[def.FullName] = def;
                structTypes.Add((t, def));
            }
        }

        // Only enums that are actually used end up in the model (the contract assembly may contain others).
        var usedEnums = new HashSet<EnumDef>();
        TypeRef Map(Type t, string where)
        {
            var r = MapType(t, enums, structs, where, errors);
            Collect(r, usedEnums);
            return r;
        }

        // Pass 2: struct fields.
        foreach (var (t, def) in structTypes)
        {
            foreach (var f in t.GetFields(InstanceFields))
            {
                var where = $"{def.Name}.{f.Name}";
                if (!f.IsPublic) errors.Add($"{where}: ABI struct fields must be public");
                var fixedLen = GetAttributeArg<int?>(f, "FixedStringAttribute");
                var type = Map(f.FieldType, where);
                def.Fields.Add(new FieldDef
                {
                    Name = f.Name,
                    Type = type,
                    FixedString = fixedLen,
                    Const = HasAttribute(f, "ConstAttribute"),
                    Doc = docs.Get("F", $"{t.FullName}.{f.Name}"),
                });
            }
            if (def.Fields.Count == 0) errors.Add($"{def.Name}: ABI structs must have at least one field");
        }

        // Pass 3: interfaces.
        foreach (var t in types.Where(t => t.IsInterface))
        {
            string? managed = GetAttributeArg<string>(t, "ManagedApiAttribute");
            string? native = GetAttributeArg<string>(t, "NativeApiAttribute");
            if (managed == null && native == null) continue;
            if (managed != null && native != null) { errors.Add($"{t.Name}: both [ManagedApi] and [NativeApi]"); continue; }

            var iface = new InterfaceDef
            {
                ClrName = t.Name,
                FullName = t.FullName!,
                ApiName = managed ?? native!,
                Direction = managed != null ? ApiDirection.Managed : ApiDirection.Native,
                Doc = docs.Get("T", t.FullName!),
            };
            if (t.GetProperties().Length > 0 || t.GetEvents().Length > 0)
                errors.Add($"{t.Name}: ABI interfaces may only declare methods (no properties or events)");

            var seen = new HashSet<string>();
            foreach (var m in t.GetMethods())
            {
                var where = $"{t.Name}.{m.Name}";
                if (!seen.Add(m.Name)) { errors.Add($"{where}: overloads are not supported"); continue; }
                if (m.IsGenericMethodDefinition) { errors.Add($"{where}: generic methods are not supported"); continue; }
                if (m.IsStatic) { errors.Add($"{where}: static members are not supported"); continue; }
                if (iface.Direction == ApiDirection.Managed && m.Name == "Destroy")
                    errors.Add($"{where}: 'Destroy' is reserved (generated; implement IDisposable instead)");

                bool main = HasAttribute(m, "MainThreadAttribute"), audio = HasAttribute(m, "AudioThreadAttribute");
                if (main == audio) errors.Add($"{where}: needs exactly one of [MainThread] or [AudioThread]");

                var method = new MethodDef
                {
                    Name = m.Name,
                    Thread = audio ? ThreadKind.Audio : ThreadKind.Main,
                    Optional = HasAttribute(m, "OptionalMemberAttribute"),
                    Return = Map(m.ReturnType, $"{where} return"),
                    Doc = docs.Get("M", $"{t.FullName}.{m.Name}"),
                };
                foreach (var p in m.GetParameters())
                {
                    var pt = p.ParameterType;
                    var mod = ParamMod.None;
                    if (pt.IsByRef)
                    {
                        mod = p.IsOut ? ParamMod.Out : p.IsIn ? ParamMod.In : ParamMod.Ref;
                        pt = pt.GetElementType()!;
                    }
                    method.Params.Add(new ParamDef(p.Name ?? $"arg{p.Position}", Map(pt, $"{where}({p.Name})"), mod));
                }
                iface.Methods.Add(method);
            }
            model.Interfaces.Add(iface);
        }

        model.Enums.AddRange(enums.Values.Where(usedEnums.Contains).OrderBy(e => e.Name, StringComparer.Ordinal));
        model.Structs.AddRange(TopologicalSort(structTypes.Select(s => s.Def).ToList(), errors));

        Validator.Validate(model, errors);
        if (errors.Count > 0) throw new AbiGenException(errors);
        return model;
    }

    private static void ReadConfig(Assembly assembly, AbiModel model)
    {
        var cfg = assembly.GetCustomAttributesData().FirstOrDefault(a => a.AttributeType.Name == "AbiConfigAttribute");
        if (cfg == null) return;
        foreach (var arg in cfg.NamedArguments)
        {
            switch (arg.MemberName)
            {
                case "Version": model.Version = (int)arg.TypedValue.Value!; break;
                case "CPrefix": model.CPrefix = (string)arg.TypedValue.Value!; break;
                case "CppNamespace": model.CppNamespace = (string)arg.TypedValue.Value!; break;
                case "CSharpNamespace": model.CSharpNamespace = (string)arg.TypedValue.Value!; break;
            }
        }
    }

    private static TypeRef MapType(Type t, Dictionary<string, EnumDef> enums, Dictionary<string, StructDef> structs,
        string where, List<string> errors)
    {
        if (t.IsByRef) { errors.Add($"{where}: by-ref types are only allowed as parameters"); return new PrimType(Prim.Void); }
        if (t.IsPointer) return new PointerType(MapType(t.GetElementType()!, enums, structs, where, errors));
        if (t.IsArray) return new ArrayType(MapType(t.GetElementType()!, enums, structs, where, errors));
        if (t.IsGenericType)
        {
            var def = t.GetGenericTypeDefinition().FullName;
            if (def is "System.ReadOnlySpan`1" or "System.Span`1")
                return new SpanType(MapType(t.GetGenericArguments()[0], enums, structs, where, errors), def == "System.ReadOnlySpan`1");
            errors.Add($"{where}: unsupported generic type {t}");
            return new PrimType(Prim.Void);
        }
        if (MapPrimitive(t) is { } prim) return new PrimType(prim);
        if (t.FullName == "System.String") return new StringType();
        if (t.FullName != null && enums.TryGetValue(t.FullName, out var e)) return new EnumType(e);
        if (t.FullName != null && structs.TryGetValue(t.FullName, out var s)) return new StructType(s);
        errors.Add($"{where}: unsupported type {t} (structs need [AbiStruct] or [AbiRecord]; enums must be in the contract)");
        return new PrimType(Prim.Void);
    }

    private static Prim? MapPrimitive(Type t) => t.FullName switch
    {
        "System.Void" => Prim.Void,
        "System.Boolean" => Prim.Bool,
        "System.SByte" => Prim.I8,
        "System.Byte" => Prim.U8,
        "System.Int16" => Prim.I16,
        "System.UInt16" => Prim.U16,
        "System.Int32" => Prim.I32,
        "System.UInt32" => Prim.U32,
        "System.Int64" => Prim.I64,
        "System.UInt64" => Prim.U64,
        "System.Single" => Prim.F32,
        "System.Double" => Prim.F64,
        "System.IntPtr" => Prim.IntPtr,
        "System.UIntPtr" => Prim.UIntPtr,
        _ => null,
    };

    private static void Collect(TypeRef t, HashSet<EnumDef> used)
    {
        switch (t)
        {
            case EnumType e: used.Add(e.Def); break;
            case PointerType p: Collect(p.Element, used); break;
            case SpanType s: Collect(s.Element, used); break;
            case ArrayType a: Collect(a.Element, used); break;
        }
    }

    /// <summary>Orders structs so every struct is declared after the structs it uses (by value or by pointer).</summary>
    private static List<StructDef> TopologicalSort(List<StructDef> defs, List<string> errors)
    {
        var result = new List<StructDef>();
        var state = new Dictionary<StructDef, int>(); // 1 = visiting, 2 = done
        void Visit(StructDef d)
        {
            if (state.TryGetValue(d, out var s))
            {
                if (s == 1) errors.Add($"{d.Name}: cyclic struct reference");
                return;
            }
            state[d] = 1;
            foreach (var f in d.Fields)
                foreach (var dep in Dependencies(f.Type))
                    Visit(dep);
            state[d] = 2;
            result.Add(d);
        }
        foreach (var d in defs.OrderBy(d => d.Name, StringComparer.Ordinal)) Visit(d);
        return result;
    }

    private static IEnumerable<StructDef> Dependencies(TypeRef t) => t switch
    {
        StructType s => [s.Def],
        PointerType p => Dependencies(p.Element),
        _ => [],
    };

    private static bool HasAttribute(MemberInfo m, string name) =>
        m.GetCustomAttributesData().Any(a => a.AttributeType.Name == name);

    private static T? GetAttributeArg<T>(MemberInfo m, string name)
    {
        var a = m.GetCustomAttributesData().FirstOrDefault(a => a.AttributeType.Name == name);
        if (a == null || a.ConstructorArguments.Count == 0) return default;
        return (T?)a.ConstructorArguments[0].Value;
    }
}

/// <summary>XML documentation summaries from the contract's doc file, keyed by "T:Full.Name" etc. (parameters stripped).</summary>
internal sealed class Docs
{
    private readonly Dictionary<string, string> _map = new();

    public static Docs Load(string path)
    {
        var docs = new Docs();
        if (!File.Exists(path)) return docs;
        foreach (var member in XDocument.Load(path).Descendants("member"))
        {
            var name = member.Attribute("name")?.Value;
            var summary = member.Element("summary") is { } element ? Text(element) : null;
            if (name == null || summary == null) continue;
            int paren = name.IndexOf('(');
            if (paren >= 0) name = name[..paren];
            docs._map[name] = string.Join(' ', summary.Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries));
        }
        return docs;
    }

    /// <summary>The text of a doc element, with &lt;paramref&gt;/&lt;see&gt; references (empty elements) rendered as names.</summary>
    private static string Text(XElement element) => string.Concat(element.Nodes().Select(node => node switch
    {
        XText text => text.Value,
        XElement { Name.LocalName: "paramref" or "typeparamref" } e => e.Attribute("name")?.Value ?? "",
        XElement { Name.LocalName: "see" or "seealso" } e when e.IsEmpty =>
            e.Attribute("langword")?.Value ?? ShortName(e.Attribute("cref")?.Value ?? e.Attribute("href")?.Value ?? ""),
        XElement e => Text(e),
        _ => "",
    }));

    /// <summary>"T:A.B.C" -> "C", "M:A.B.C(System.Int32)" -> "C".</summary>
    private static string ShortName(string cref)
    {
        int paren = cref.IndexOf('(');
        if (paren >= 0) cref = cref[..paren];
        if (cref.Length > 2 && cref[1] == ':') cref = cref[2..];
        return cref[(cref.LastIndexOf('.') + 1)..];
    }

    public string? Get(string kind, string fullName) => _map.GetValueOrDefault($"{kind}:{fullName}");
}
