using System.Text.Json;
using System.Text.Json.Serialization;

namespace AbiGen;

/// <summary>
/// A committed snapshot of the ABI. With an unchanged version, the generator only accepts compatible
/// evolution: new types, new enum members, and new [OptionalMember] interface members appended at the end.
/// Anything else requires bumping AbiConfig.Version.
/// </summary>
public sealed class Manifest
{
    public int AbiVersion { get; set; }
    public SortedDictionary<string, ManifestEnum> Enums { get; set; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, List<string>> Structs { get; set; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, List<ManifestMember>> Interfaces { get; set; } = new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    };

    public static Manifest Build(AbiModel model)
    {
        var c = new CTypes(model);
        var m = new Manifest { AbiVersion = model.Version };
        foreach (var e in model.Enums)
        {
            var me = new ManifestEnum { Underlying = CTypes.Prim(e.Underlying) };
            foreach (var member in e.Members) me.Members[member.Name] = member.Value;
            m.Enums[c.Name(e)] = me;
        }
        foreach (var s in model.Structs)
            m.Structs[c.Name(s)] = s.Fields.Select(c.FieldDecl).ToList();
        foreach (var iface in model.Interfaces)
        {
            m.Interfaces[c.Name(iface)] = Lowering.Lower(iface).Select(nm => new ManifestMember
            {
                Name = nm.CName,
                Signature = $"{c.ReturnType(nm)}({c.Signature(iface, nm)})",
                Thread = nm.Method.Thread == ThreadKind.Audio ? "audio" : "main",
                Optional = nm.Method.Optional,
            }).ToList();
        }
        return m;
    }

    public static Manifest? Load(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path), Json) : null;

    public string Serialize() => JsonSerializer.Serialize(this, Json) + "\n";

    /// <summary>Compatibility errors when evolving from <paramref name="old"/> to this manifest.</summary>
    public List<string> CheckCompatibleWith(Manifest old)
    {
        var errors = new List<string>();
        if (AbiVersion < old.AbiVersion)
        {
            errors.Add($"ABI version went down ({old.AbiVersion} -> {AbiVersion})");
            return errors;
        }
        if (AbiVersion > old.AbiVersion) return errors; // breaking changes allowed with a version bump

        foreach (var (name, oldEnum) in old.Enums)
        {
            if (!Enums.TryGetValue(name, out var e)) { errors.Add($"enum {name} was removed"); continue; }
            if (e.Underlying != oldEnum.Underlying) errors.Add($"enum {name}: underlying type changed");
            foreach (var (member, value) in oldEnum.Members)
                if (!e.Members.TryGetValue(member, out var v) || v != value)
                    errors.Add($"enum {name}: member {member} was removed or changed");
        }
        foreach (var (name, oldFields) in old.Structs)
        {
            if (!Structs.TryGetValue(name, out var fields)) { errors.Add($"struct {name} was removed"); continue; }
            if (!fields.SequenceEqual(oldFields)) errors.Add($"struct {name}: fields changed (structs are frozen within an ABI version)");
        }
        foreach (var (name, oldMembers) in old.Interfaces)
        {
            if (!Interfaces.TryGetValue(name, out var members)) { errors.Add($"interface {name} was removed"); continue; }
            for (int i = 0; i < oldMembers.Count; i++)
            {
                if (i >= members.Count) { errors.Add($"{name}: member {oldMembers[i].Name} was removed"); break; }
                if (members[i] != oldMembers[i])
                {
                    // Report only the first difference; everything after a moved member shifts too.
                    errors.Add($"{name}: member {i} changed ({oldMembers[i].Name}: {oldMembers[i].Signature} -> {members[i].Name}: {members[i].Signature}); members may only be appended");
                    break;
                }
            }
            foreach (var added in members.Skip(oldMembers.Count).Where(m => !m.Optional))
                errors.Add($"{name}: appended member {added.Name} must be [OptionalMember] (older binaries do not provide it)");
        }
        if (errors.Count > 0) errors.Add("To make breaking changes, bump AbiConfig.Version in the contract.");
        return errors;
    }
}

public sealed class ManifestEnum
{
    public string Underlying { get; set; } = "";
    public SortedDictionary<string, long> Members { get; set; } = new(StringComparer.Ordinal);
}

public sealed record ManifestMember
{
    public string Name { get; init; } = "";
    public string Signature { get; init; } = "";
    public string Thread { get; init; } = "";
    public bool Optional { get; init; }
}
