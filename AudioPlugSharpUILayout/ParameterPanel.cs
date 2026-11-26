using System;
using System.Collections.Generic;
using AudioPlugSharp;
using UILayout;

namespace AudioPlugSharpUILayout;

/// <summary>
/// An editor generated from parameters: a grid of <see cref="ParameterControl.For"/> cells (a knob, switch or
/// drop-down per parameter, as its metadata suits) on the theme's background. Cells are a fixed size, so the panel's
/// size follows from the number of parameters and columns alone (<see cref="GetSize"/>), before any layout exists. Given
/// a width other than that (a resized window), the cells flow into as many columns as fit it.
/// <see cref="AudioPluginUILayout"/> shows one when a plugin doesn't build its own editor. Call <see cref="Update"/>
/// each frame (the plugin base does for its generated editor).
/// </summary>
public class ParameterPanel : Dock
{
    /// <summary>A cell's size and the space between cells and around the grid, in layout units.</summary>
    public const float CellWidth = 80;
    public const float CellHeight = 72;
    public const float CellSpacing = 8;
    public const float PanelPadding = 16;

    /// <summary>The most columns a panel gets by default; more parameters make more rows.</summary>
    public const int MaxDefaultColumns = 6;

    ParameterControl[] controls;
    Dock[] cells;
    VerticalStack grid;

    /// <param name="parameters">The parameters, in order (left to right, then down).</param>
    /// <param name="columns">The number of columns, or 0 for <see cref="DefaultColumns"/>.</param>
    public ParameterPanel(IReadOnlyList<AudioPluginParameter> parameters, int columns = 0)
    {
        UITheme theme = Layout.Current.Theme;

        HorizontalAlignment = EHorizontalAlignment.Stretch;
        VerticalAlignment = EVerticalAlignment.Stretch;
        BackgroundColor = theme.Background;
        Padding = new LayoutPadding(PanelPadding);

        Columns = (columns > 0) ? columns : DefaultColumns(parameters.Count);

        controls = new ParameterControl[parameters.Count];

        if (parameters.Count == 0)
        {
            Children.Add(new TextBlock("No parameters")
            {
                TextColor = theme.MutedText,
                HorizontalAlignment = EHorizontalAlignment.Center,
                VerticalAlignment = EVerticalAlignment.Center
            });

            return;
        }

        // The grid at the top, centered across a panel wider than it (a window of another size)
        grid = new VerticalStack
        {
            HorizontalAlignment = EHorizontalAlignment.Center,
            VerticalAlignment = EVerticalAlignment.Top,
            ChildSpacing = CellSpacing
        };
        Children.Add(grid);

        cells = new Dock[parameters.Count];

        for (int i = 0; i < parameters.Count; i++)
        {
            ParameterControl control = ParameterControl.For(parameters[i]);

            // The cell's width, so long names and choices are cut short instead of widening the column
            control.HorizontalAlignment = EHorizontalAlignment.Stretch;
            control.VerticalAlignment = EVerticalAlignment.Top;

            Dock cell = new Dock
            {
                DesiredWidth = CellWidth,
                DesiredHeight = CellHeight
            };
            cell.Children.Add(control);

            cells[i] = cell;
            controls[i] = control;
        }

        ArrangeCells();
    }

    // The cells in rows of Columns
    void ArrangeCells()
    {
        foreach (UIElement oldRow in grid.Children)
            ((HorizontalStack)oldRow).Children.Clear();

        grid.Children.Clear();

        HorizontalStack row = null;

        for (int i = 0; i < cells.Length; i++)
        {
            if ((i % Columns) == 0)
            {
                row = new HorizontalStack
                {
                    HorizontalAlignment = EHorizontalAlignment.Left,
                    ChildSpacing = CellSpacing
                };

                grid.Children.Add(row);
            }

            row.Children.Add(cells[i]);
        }
    }

    /// <summary>Lays out in the bounds given, first flowing the cells into as many columns as fit their width.</summary>
    public override void SetBounds(in RectF bounds, UIElement parent)
    {
        if (cells?.Length > 0)
        {
            int fitting = ColumnsFitting(bounds.Width, cells.Length);

            if (fitting != Columns)
            {
                Columns = fitting;

                ArrangeCells();
            }
        }

        base.SetBounds(bounds, parent);
    }

    /// <summary>The most columns of cells a width (in layout units) has room for, from 1 to the number of cells.</summary>
    public static int ColumnsFitting(float width, int cellCount)
    {
        int fitting = (int)MathF.Floor((width - (PanelPadding * 2) + CellSpacing + 0.01f) / (CellWidth + CellSpacing));

        return Math.Clamp(fitting, 1, Math.Max(cellCount, 1));
    }

    /// <summary>The number of columns.</summary>
    public int Columns { get; private set; }

    /// <summary>The parameters' controls, in the parameters' order.</summary>
    public IReadOnlyList<ParameterControl> Controls
    {
        get { return controls; }
    }

    /// <summary>
    /// Follows the parameters' values (host automation), shows hover and fades value popups. Call each frame. Doesn't
    /// allocate.
    /// </summary>
    public void Update()
    {
        for (int i = 0; i < controls.Length; i++)
            controls[i].Update();
    }

    /// <summary>
    /// The columns for a number of parameters: one row up to <see cref="MaxDefaultColumns"/>, then as few rows as
    /// that allows, filled evenly (8 parameters: two rows of 4).
    /// </summary>
    public static int DefaultColumns(int parameterCount)
    {
        if (parameterCount <= 0)
            return 1;

        int rows = (parameterCount + MaxDefaultColumns - 1) / MaxDefaultColumns;

        return (parameterCount + rows - 1) / rows;
    }

    /// <summary>
    /// The panel's size in layout units for a number of parameters and columns (0: <see cref="DefaultColumns"/>), as
    /// an editor's EditorWidth and EditorHeight.
    /// </summary>
    public static void GetSize(int parameterCount, int columns, out uint width, out uint height)
    {
        if (parameterCount <= 0)
        {
            width = 240;
            height = 80;

            return;
        }

        if (columns <= 0)
            columns = DefaultColumns(parameterCount);

        columns = Math.Min(columns, parameterCount);

        int rows = (parameterCount + columns - 1) / columns;

        width = (uint)MathF.Ceiling((PanelPadding * 2) + (columns * CellWidth) + ((columns - 1) * CellSpacing));
        height = (uint)MathF.Ceiling((PanelPadding * 2) + (rows * CellHeight) + ((rows - 1) * CellSpacing));
    }
}
