// Attributes that describe the native/managed ABI. They are read by Tools/AbiGen through
// metadata only; at runtime they carry no behaviour.
namespace AudioPlugSharpBridge;

/// <summary>Global ABI settings for the contract assembly.</summary>
[AttributeUsage(AttributeTargets.Assembly)]
internal sealed class AbiConfigAttribute : Attribute
{
    /// <summary>ABI version. Bump it to make breaking (non-append) changes.</summary>
    public int Version { get; set; } = 1;
    /// <summary>Prefix for C identifiers (types become prefix_name, macros PREFIX_NAME).</summary>
    public string CPrefix { get; set; } = "pt";
    /// <summary>Namespace for the generated C++ wrappers.</summary>
    public string CppNamespace { get; set; } = "pt";
    /// <summary>Namespace for the generated C# interop code.</summary>
    public string CSharpNamespace { get; set; } = "";
}

/// <summary>A blittable struct shared as-is by both sides. Allowed fields: primitives (not bool), enums, pointers, other AbiStructs.</summary>
[AttributeUsage(AttributeTargets.Struct)]
internal sealed class AbiStructAttribute : Attribute { }

/// <summary>
/// A marshalled value type for main-thread calls. May contain [FixedString] strings in addition
/// to AbiStruct field types. The generator emits a native mirror and conversion code.
/// </summary>
[AttributeUsage(AttributeTargets.Struct)]
internal sealed class AbiRecordAttribute : Attribute { }

/// <summary>A string field of an AbiRecord, stored as a NUL-terminated UTF-8 char array of this size.</summary>
[AttributeUsage(AttributeTargets.Field)]
internal sealed class FixedStringAttribute(int length) : Attribute
{
    public int Length { get; } = length;
}

/// <summary>On a pointer field: every pointee level is const in C (e.g. const float* const*).</summary>
[AttributeUsage(AttributeTargets.Field)]
internal sealed class ConstAttribute : Attribute { }

/// <summary>An interface implemented in C# and called from native code.</summary>
[AttributeUsage(AttributeTargets.Interface)]
internal sealed class ManagedApiAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

/// <summary>An interface implemented in native code and called from C#.</summary>
[AttributeUsage(AttributeTargets.Interface)]
internal sealed class NativeApiAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

/// <summary>Called on the host's main thread. May allocate and block.</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class MainThreadAttribute : Attribute { }

/// <summary>Called on the realtime audio thread. The generator rejects types that would allocate.</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class AudioThreadAttribute : Attribute { }

/// <summary>The implementation may not provide this member (e.g. an older binary). Members appended after the first release must be optional.</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class OptionalMemberAttribute : Attribute { }
