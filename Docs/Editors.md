# Plugin Editors

A plugin's editor (its user interface) is drawn into a window that the DAW provides. AudioPlugSharp has two ready-made kinds of editor:

- **UILayout** ([UILayout](https://github.com/mikeoliphant/UILayout)), through **AudioPluginUILayout**: works on Windows and Linux. Recommended.
- **WPF**, through **AudioPluginWPF**: Windows only.

Plugins without an editor can skip all of this: the DAW shows its own controls for their parameters.

# UILayout Editors

Derive your plugin from **AudioPluginUILayout** (in the **AudioPlugSharpUILayout** project), set the editor's size, and build its user interface in **CreateEditorUI**:

```csharp
public class MyPlugin : AudioPluginUILayout
{
    public MyPlugin()
    {
        EditorWidth = 320;
        EditorHeight = 160;
    }

    protected override UIElement CreateEditorUI()
    {
        // Build and return the editor's root element
    }
}
```

The editor is created when the DAW opens it and discarded when the DAW closes it. Some other things you can override:

- **EditorOpened** and **EditorClosing**: called after the editor opened and before it closes.
- **EditorUpdate**: called every frame, before the editor draws. A good place to bring your controls up to date with the parameter values, which the DAW may change (automation).
- **CreateEditorLayout**: to use your own layout class.

See the [UILayoutExample](../Examples/UILayoutExample/UILayoutExamplePlugin.cs) for a complete example.

## Parameters

Setting a parameter's **EditValue** or **NormalizedEditValue** from your editor reports the change to the DAW.

For knobs and sliders, call the parameter's **BeginEdit()** when the user grabs the control and **EndEdit()** when they let go. The DAW then records the movement as one gesture, which automation modes such as touch and latch need.

Bind controls that work in a 0-1 range to **NormalizedEditValue**: it also works for parameters whose minimum is -inf dB.

## Size and Scaling

**EditorWidth** and **EditorHeight** are in pixels at 100% display scale. On high-DPI screens the editor is scaled up automatically, and your user interface code doesn't need to change.

To let the DAW resize the editor, override **CanResizeEditor** to return true. The editor's layout then follows the new size.

The layout's **Scale** is yours to use, for example for a user zoom setting. It combines with the display scale.

## Smooth Drawing

By default the editor updates and draws on the DAW's user interface thread, about 30-60 times a second, depending on the DAW. That is fine for most editors.

For heavy or fast-moving editors (animation, scrolling displays), set **UseEditorRenderThread = true** in your plugin's constructor. The editor then draws on a thread of its own, in step with the screen's refresh rate. Your editor code (**EditorUpdate** and your user interface elements) then runs on that thread rather than the DAW's. Setting parameter values and using **Host** from it is fine. **CreateEditorUI**, **EditorOpened** and **EditorClosing** still run on the DAW's thread.

## Keyboard

DAWs use many keys as their own shortcuts (for example the space bar to start and stop playback). By default, your editor takes only the keys its input mappings use, and leaves the others to the DAW.

- Set **EditorClaimsAllKeys = true** for editors that should get every key while they have focus, such as an editor used like an application of its own.
- Override **EditorWantsKey** to decide key by key.

Some DAWs keep certain keys for themselves whatever the plugin says. Reaper, for example, keeps Alt+Enter and F11 (and Escape, for CLAP plugins).

## Fullscreen and the Mouse Cursor

Your editor can ask the DAW's window system for:

- **Host.SetEditorFullscreen(true)** (and **false**): show the editor fullscreen, and back. **Host.IsEditorFullscreen** tells whether it is. The editor must be resizable.
- **Host.SetEditorCursorVisible(false)** (and **true**): hide the mouse cursor while it is over the editor.

# WPF Editors

Derive your plugin from **AudioPluginWPF** and override **GetEditorView** to return your editor (a WPF **UserControl**). Its **DataContext** is your plugin, so controls can bind to its parameters. The **AudioPlugSharpWPF** library has ready-made controls for this. See the [WPFExample](../Examples/WPFExample/WPFExamplePlugin.cs).

WPF editors work on Windows only, and are heavier than UILayout editors.

# Other Editors

To use something else, implement the editor methods of **IAudioPluginEditor** yourself (or override them in **AudioPluginBase**): **OpenEditor** gives you the window to draw into, **EditorIdle** is called regularly, **EditorInput** brings mouse and keyboard input, and **CloseEditor** ends it.
