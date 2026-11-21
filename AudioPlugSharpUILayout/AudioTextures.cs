using System.IO;
using UILayout;

namespace AudioPlugSharpUILayout;

/// <summary>
/// The audio controls' own textures, loaded into the current layout by name, as UILayout loads its default textures:
/// a sheet and its manifest embedded from Textures/, rendered from the SVGs in SrcTextures/ by
/// AudioPlugSharpImageProcessor (names prefixed "Audio."). Audio-specific, so here rather than in UILayout.
/// <see cref="AudioPluginUILayout"/> loads them into its editor's layout before CreateEditorUI; a layout of another
/// kind (the standalone's status strip) calls <see cref="Load"/> once it is current, before making the controls.
/// </summary>
public static class AudioTextures
{
    /// <summary>A level meter's background and bar: small rounded nine-patches (white).</summary>
    public const string MeterBackground = "Audio.MeterBackground";
    public const string MeterBar = "Audio.MeterBar";

    /// <summary>
    /// A knob's shadow, body, highlight (translucent), ring (outline) and pointer: same-size images, layered (white).
    /// </summary>
    public const string KnobShadow = "Audio.KnobShadow";
    public const string KnobBody = "Audio.KnobBody";
    public const string KnobHighlight = "Audio.KnobHighlight";
    public const string KnobRing = "Audio.KnobRing";
    public const string KnobPointer = "Audio.KnobPointer";

    public static void Load()
    {
        var loader = new AssemblyResourceContentLoader(typeof(AudioTextures).Assembly);

        using (Stream manifest = loader.OpenContentStream("Textures/ImageManifest.xml"))
        {
            ImageManifest.Load(loader, manifest, Layout.Current);
        }
    }
}
