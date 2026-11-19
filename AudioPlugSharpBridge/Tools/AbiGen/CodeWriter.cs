using System.Text;

namespace AbiGen;

public sealed class CodeWriter
{
    private readonly StringBuilder _sb = new();
    private int _indent;

    public void Line(string text = "")
    {
        if (text.Length > 0) _sb.Append(' ', _indent * 4).Append(text);
        _sb.Append('\n');
    }

    public void Indent() => _indent++;
    public void Outdent() => _indent--;

    /// <summary>Writes "header {" ... "}" + suffix.</summary>
    public IDisposable Block(string header, string suffix = "")
    {
        if (header.Length > 0) Line(header);
        Line("{");
        _indent++;
        return new Closer(this, "}" + suffix);
    }

    /// <summary>Writes a doc comment in the given style, if text is non-empty.</summary>
    public void Doc(string? text, string style)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        switch (style)
        {
            case "c": Line($"/* {text} */"); break;
            case "cs":
                Line($"/// <summary>{System.Security.SecurityElement.Escape(text)}</summary>"); break;
            default: Line($"// {text}"); break;
        }
    }

    public override string ToString() => _sb.ToString();

    private sealed class Closer(CodeWriter w, string close) : IDisposable
    {
        public void Dispose()
        {
            w._indent--;
            w.Line(close);
        }
    }
}
