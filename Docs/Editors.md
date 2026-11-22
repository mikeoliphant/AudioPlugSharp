# Plugin Editors

A plugin's editor (its user interface) is drawn into a window that the DAW provides. AudioPlugSharp has two ready-made kinds of editor:

- **UILayout** ([UILayout](https://github.com/mikeoliphant/UILayout)), through **AudioPluginUILayout**: works on Windows and Linux. Recommended.
- **WPF**, through **AudioPluginWPF**: Windows only.

Plugins without an editor can skip all of this: the DAW shows its own controls for their parameters. Or let AudioPlugSharp make an editor from your parameters, with no editor code at all (see [Automatic Editors](#automatic-editors)).

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

## Automatic Editors

If you don't override **CreateEditorUI**, your plugin gets an editor made from its parameters: a knob for each, a switch for an on/off parameter and a drop-down for a list of choices, in a grid. The editor's size fits the parameters, so you don't set **EditorWidth** and **EditorHeight** either:

```csharp
public class MyPlugin : AudioPluginUILayout
{
    // No CreateEditorUI: the editor is made from the parameters added in Initialize
}
```

Parameters appear in the order you add them, up to six in a row. Set **GeneratedEditorColumns** in your constructor for another number of columns. See the [ParameterEditorExample](../Examples/ParameterEditorExample/ParameterEditorExamplePlugin.cs).

## Parameter Types

The kind of parameter decides how editors and DAWs show it:

- **AudioPluginParameter**: a value from **MinValue** to **MaxValue**, shown with **ValueFormat**. Set **StepCount** for values in steps (7 steps from 1 to 8 gives the whole numbers 1 to 8).
- **DecibelParameter**: a gain in dB, down to -inf dB.
- **BoolParameter**: on or off. Read **IsOn** in your processing.
- **ChoiceParameter**: one of a list of **Choices**, by name. Read **ChoiceIndex** in your processing.
- **EnumParameter&lt;T&gt;**: the choices are the members of an enum, named after them (or after their **[Description]**). Read **Value** in your processing. Add new members at the end: projects save the choice's position.

DAWs show stepped parameters as steps and choices by name, in their own controls and automation.

## Parameter Controls

**ParameterControl** shows one parameter in your own editor: its name above a control, with the value popping up while the user changes it. It reports the user's changes to the DAW as gestures, so automation modes such as touch and latch work.

- **ParameterControl.For(parameter)**: the control that suits the parameter, as the automatic editor uses: a switch, a drop-down or a knob.
- **ParameterControl.Knob**, **Slider** (horizontal or vertical), **Switch** and **Choice**: a particular control.

Call each control's **Update()** in **EditorUpdate**, so it follows the DAW's changes to its parameter (automation). Knobs and sliders also take the mouse wheel, Shift for fine adjustment, and a double-click for the default value.

**ParameterPanel** is the automatic editor's grid of controls, which you can also put in an editor of your own. Call its **Update()** in **EditorUpdate** too.

## Your Own Controls

For controls of your own, setting a parameter's **EditValue** or **NormalizedEditValue** from your editor reports the change to the DAW.

For knobs and sliders, call the parameter's **BeginEdit()** when the user grabs the control and **EndEdit()** when they let go. The DAW then records the movement as one gesture, which automation modes such as touch and latch need.

Bind controls that work in a 0-1 range to **NormalizedEditValue**: it also works for parameters whose minimum is -inf dB.

## Audio Controls

**AudioPlugSharpUILayout** adds controls for audio alongside UILayout's own. **LevelMeter** shows a level on a decibel scale, in green, then orange from -6 dB and red when clipping. Set its **Level** (the peak amplitude, 1 is full scale) in **EditorUpdate**. The [UILayoutExample](../Examples/UILayoutExample/UILayoutExamplePlugin.cs) shows how to pass the peak from the audio thread and let the meter fall.

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

# Design Notes

This section explains why editors work the way they do. You don't need it to write an editor.

## Threads

By default, an editor's updating and drawing run on the DAW's user interface thread, each time the DAW's timer fires. Drawing doesn't wait for the screen's refresh, so several open editors don't hold each other up.

With **UseEditorRenderThread**, each editor gets a thread of its own, and all of its user interface work runs there: input, updating, layout and drawing. Mouse and keyboard input from the DAW is queued for that thread. In the other direction, parameter changes and window requests (fullscreen, cursor) are queued for the DAW's thread, because that's where the DAW expects them. Creating and destroying the window's graphics, and **CreateEditorUI**, **EditorOpened** and **EditorClosing**, stay on the DAW's thread.

We considered a simpler split: update on the DAW's thread, and only draw on the render thread. We rejected it because UILayout moves things in response to input while updating (scrolling, dragging a list). Those would then move only at the DAW's timer rate, about 30 times a second in Reaper, even though drawing kept up with the screen.

The DAW asks about each key right away, so **EditorWantsKey** is answered on the DAW's thread from the editor's input mappings, even in render-thread mode.

## Scaling

**EditorWidth**, **EditorHeight** and the editor sizes the DAW sees are logical units (pixels at 100% scale), as in earlier versions. The scale comes from the DAW when it provides one, otherwise from the system: the window's DPI on Windows, and 100% on Linux. When the scale changes (the DAW changes it, or the window moves to a screen with a different DPI), the window is resized and the editor redraws at the new scale.

WPF scales its content by the window's DPI itself, so a DAW scale that differs from the system's isn't applied to WPF editors.
