// Hand-written support types used by the generated interop code (Generated/*.g.cs).
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Unicode;

namespace AudioPlugSharpBridge.Interop;

/// <summary>Implemented by [ManagedApi] implementations that want to hear about exceptions thrown into thunks.</summary>
internal interface IAbiErrorHandler
{
    /// <summary>May be called on any thread, including the audio thread. Must not throw.</summary>
    void OnAbiException(string member, Exception exception);
}

/// <summary>A native [NativeApi] call returned a failure code.</summary>
internal sealed class AbiCallException(string member, int code)
    : Exception($"Native call {member} failed with code {code}")
{
    public string Member { get; } = member;
    public int Code { get; } = code;
}

internal static unsafe class AbiUtf8
{
    /// <summary>Writes a NUL-terminated, truncated (on whole code points) UTF-8 string into a fixed buffer.</summary>
    public static void WriteFixed(string? value, byte* destination, int capacity)
    {
        var span = new Span<byte>(destination, capacity);
        span.Clear();
        if (!string.IsNullOrEmpty(value))
            Utf8.FromUtf16(value, span[..(capacity - 1)], out _, out _);
    }

    public static string ReadFixed(byte* source, int capacity)
    {
        var span = new ReadOnlySpan<byte>(source, capacity);
        int length = span.IndexOf((byte)0);
        return Encoding.UTF8.GetString(length < 0 ? span : span[..length]);
    }

    public static string FromNative(byte* value) => value == null ? "" : Marshal.PtrToStringUTF8((nint)value) ?? "";

    public static byte[] ToNullTerminated(string? value)
    {
        value ??= "";
        var bytes = new byte[Encoding.UTF8.GetByteCount(value) + 1];
        Encoding.UTF8.GetBytes(value, bytes);
        return bytes;
    }

    /// <summary>Last resort for exceptions with nowhere else to go (e.g. in Destroy).</summary>
    public static void ReportUnhandled(string member, Exception exception)
    {
        try { Console.Error.WriteLine($"AudioPlugSharpBridge: unhandled exception in {member}: {exception}"); }
        catch { /* ignore */ }
    }
}
