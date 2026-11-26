using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace AudioPlugSharpImageProcessor;

/// <summary>
/// The audio controls' textures: white SVGs (so they tint) rendered into one sheet. Image names get the "Audio."
/// prefix (see AudioTextures), so they don't clash with a plugin's own images.
/// </summary>
class AudioImageProcessor : ImageSheetProcessor.ImageSheetProcessor
{
    public void Render()
    {
        ImageNamePrefix = "Audio.";
        ForceWhite = true;

        BeginSpriteSheetGroup("AudioSheet");

        // Nine-patches: corners are (size / 2) - 1 pixels, with a straight pixel between each corner's curve and the
        // 2-pixel middle (see UILayout's NinePatchWrapper).
        AddSvg("MeterBackground");
        AddSvg("MeterBar");

        // Knobs: shadow, body, highlight, ring and pointer the same size, layered and centered (the pointer rotated
        // about its center).
        AddSvg("KnobShadow");
        AddSvg("KnobBody");
        AddSvg("KnobHighlight");
        AddSvg("KnobRing");
        AddSvg("KnobPointer");

        // Switches: lamps shaded in gray, so the shading is kept (the lamp color tints it).
        ForceWhite = false;

        AddSvg("SwitchOn", 32);
        AddSvg("SwitchOff", 32);

        ForceWhite = true;

        EndSpriteSheetGroup();
    }
}

static class Program
{
    static void Main()
    {
        string projectPath = Path.GetFullPath(Path.Combine(SourceDirectory(), "..", "AudioPlugSharpUILayout"));

        AudioImageProcessor processor = new AudioImageProcessor
        {
            SrcPath = Path.Combine(projectPath, "SrcTextures"),
            ForceRegen = true
        };

        processor.BeginRenderImages(Path.Combine(projectPath, "Textures"));

        processor.Render();

        processor.EndRenderImages();

        Console.WriteLine("Wrote " + processor.DestPath);
    }

    // This file's directory, from the compiler, so the tool finds the sources wherever it's built or run from.
    static string SourceDirectory([CallerFilePath] string path = "")
    {
        return Path.GetDirectoryName(path);
    }
}
