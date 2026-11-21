using System.Text;

namespace AbiGen;

public static class Naming
{
    private static readonly HashSet<string> CKeywords =
    [
        "auto", "break", "case", "char", "class", "const", "continue", "default", "delete", "do", "double", "else",
        "enum", "explicit", "export", "extern", "float", "for", "friend", "goto", "if", "inline", "int", "long",
        "mutable", "namespace", "new", "operator", "private", "protected", "public", "register", "restrict",
        "return", "short", "signed", "sizeof", "static", "struct", "switch", "template", "this", "throw", "try",
        "typedef", "typename", "union", "unsigned", "using", "virtual", "void", "volatile", "while", "bool", "true",
        "false", "requires", "concept", "co_await", "co_return", "co_yield", "and", "or", "not", "xor",
    ];

    /// <summary>PascalCase / camelCase to snake_case ("GetParamInfo" -> "get_param_info", "GCPause" -> "gc_pause").</summary>
    public static string Snake(string name)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (char.IsUpper(c))
            {
                bool prevLowerOrDigit = i > 0 && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]));
                bool acronymEnd = i > 0 && char.IsUpper(name[i - 1]) && i + 1 < name.Length && char.IsLower(name[i + 1]);
                if (prevLowerOrDigit || acronymEnd) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    public static string UpperSnake(string name) => Snake(name).ToUpperInvariant();

    public static string Camel(string name) => name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];

    /// <summary>"plugin" / "audio_host" -> "Plugin" / "AudioHost".</summary>
    public static string Pascal(string name) =>
        string.Concat(name.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));

    /// <summary>Makes an identifier safe in C and C++.</summary>
    public static string SafeC(string name) => CKeywords.Contains(name) ? name + "_" : name;
}
