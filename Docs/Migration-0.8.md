# Migrating From 0.7 to 0.8

Version 0.8 replaces the Windows-only C++/CLI VST3 bridge with CLAP and VST3 support on Windows and Linux. Your audio code (processing, ports, parameters, MIDI) mostly stays as it is. The changes are in project setup and in editor (user interface) code.

## Project Setup

- Replace the **AudioPlugSharpVst3** package with **AudioPlugSharpBridge**.
- Building now creates your plugin in the output folder: **clap/MyPlugin** and **vst3/MyPlugin.vst3**. See the [README](../README.md) for where to install them.
- The VST3 plugin is now the **MyPlugin.vst3** folder that the build creates, instead of a folder you named yourself with **MyPluginBridge.vst3** in it. Remove the old folder from your VST3 folder: both have the same plugin ID, so the DAW would see the plugin twice.
- Plugins need the .NET 10 runtime (on Windows, the **.NET Desktop Runtime**).

## Existing DAW Projects

Projects saved with 0.7 load unchanged in 0.8's VST3 plugin: the plugin ID, the parameter IDs and the saved state are the same. A project does not switch from the VST3 to the CLAP version of a plugin by itself.

## Editors

The DAW now provides the window, and your editor draws into it. The old editor methods are replaced:

| 0.7 | 0.8 |
|---|---|
| ShowEditor(parentWindow) | OpenEditor(window, windowType, scale) |
| HideEditor() | CloseEditor() |
| ResizeEditor(width, height) | SetEditorSize(width, height), and CanResizeEditor |
| GetDpiScale() | the scale passed to OpenEditor, and SetEditorScale when it changes |
| InitializeEditor() | (part of OpenEditor) |
| | EditorIdle (called regularly) and EditorInput (mouse and keys) |

Most plugins don't implement these themselves: the WPF and UILayout base classes do.

### WPF Editors

Keep deriving from **AudioPluginWPF** and overriding **GetEditorView**. Remove any use of the **EditorWindow** property and **GetDpiScale**, which are gone. WPF editors work on Windows only.

### UILayout Editors

Instead of creating and running your own editor window, derive from **AudioPluginUILayout** (the **AudioPlugSharpUILayout** project), set **EditorWidth** and **EditorHeight**, and override **CreateEditorUI** (or **CreateEditorLayout** to use your own layout class). Your editor then works on Windows and Linux. Some options:

- **CanResizeEditor**: let the DAW resize the editor.
- **UseEditorRenderThread**: draw on a thread of the editor's own, for heavy or fast-moving editors. Your editor code then runs on that thread.
- **EditorClaimsAllKeys**: take every key while the editor has focus. Otherwise the editor only takes the keys its input mappings use, and the others stay DAW shortcuts (such as the space bar).
- **Host.SetEditorFullscreen** and **Host.SetEditorCursorVisible**: fullscreen and hiding the mouse cursor.
- **Layout.Scale** is your own zoom setting. The display scale (high-DPI screens) is handled separately and automatically.

### Editor Size

**EditorWidth** and **EditorHeight** are in pixels at 100% display scale. On high-DPI screens the editor is scaled up automatically.

## Parameters

- Setting **EditValue** or **NormalizedEditValue** from your editor still reports the change to the DAW.
- For knobs and sliders, call **BeginEdit()** when the user grabs the control and **EndEdit()** when they let go. The DAW then records the movement as one gesture, which automation modes such as touch and latch need.
- Bind 0-1 controls to **NormalizedEditValue**: it also works for parameters whose minimum is -inf dB.

## Stand-Alone Applications

**AudioPlugSharpHost** and **AudioPlugSharpJack** are not part of 0.8 yet. Stay on 0.7 for stand-alone applications for now.
