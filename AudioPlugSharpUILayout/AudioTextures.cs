using UILayout;

namespace AudioPlugSharpUILayout;

/// <summary>
/// The audio controls' own textures (embedded in Textures/), loaded into the current layout by name, as UILayout loads
/// its default textures. Audio-specific, so here rather than in UILayout. <see cref="AudioPluginUILayout"/> loads them
/// into its editor's layout before CreateEditorUI; a layout of another kind (the standalone's status strip) calls
/// <see cref="Load"/> once it is current, before making the controls.
/// </summary>
public static class AudioTextures
{
    /// <summary>A level meter's background and bar: small rounded nine-patches (white).</summary>
    public const string MeterBackground = "MeterBackground";
    public const string MeterBar = "MeterBar";

    public static void Load()
    {
        var loader = new AssemblyResourceContentLoader(typeof(AudioTextures).Assembly);

        Layout.Current.AddImage(loader, MeterBackground);
        Layout.Current.AddImage(loader, MeterBar);
    }
}
