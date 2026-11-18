using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;

namespace AudioPlugSharpWPF
{
    /// <summary>
    /// A rotary knob. Drag vertically to change it, double-click for the default value. With a parameter, bind
    /// Value to NormalizedEditValue (Minimum and Maximum stay 0-1) and call BeginEdit()/EndEdit() from
    /// EditStarted/EditEnded, so a drag reaches the host as one gesture.
    /// </summary>
    public class Dial : RangeBase
    {
        /// <summary>Raised when a drag starts (mouse down).</summary>
        public event EventHandler EditStarted;

        /// <summary>Raised when a drag ends (mouse up or lost capture).</summary>
        public event EventHandler EditEnded;

        public static readonly DependencyProperty DefaultValueProperty =
            DependencyProperty.Register("DefaultValue", typeof(double),
              typeof(Dial), new PropertyMetadata(default(double)));

        public double DefaultValue
        {
            get { return (double)this.GetValue(DefaultValueProperty); }
            set { this.SetValue(DefaultValueProperty, value); }
        }
        private bool dragging = false;
        private Point dragStart;
        private System.Drawing.Point absDragStart;
        private double valAtStart;

        static Dial()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(Dial), new FrameworkPropertyMetadata(typeof(Dial)));
        }

        public Dial()
        {
            this.MouseLeftButtonDown += new MouseButtonEventHandler(Dial_MouseLeftButtonDown);
            this.MouseLeftButtonUp += new MouseButtonEventHandler(Dial_MouseLeftButtonUp);
            this.MouseMove += new MouseEventHandler(Dial_MouseMove);
            this.MouseDoubleClick += new MouseButtonEventHandler(Dial_MouseDoubleClick);
        }

        void Dial_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            dragging = true;
            dragStart = e.GetPosition(this);
            absDragStart = System.Windows.Forms.Cursor.Position;
            valAtStart = this.Value;

            EditStarted?.Invoke(this, EventArgs.Empty);

            this.CaptureMouse();
            this.Cursor = Cursors.None;

            e.Handled = true;
        }

        void Dial_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            this.ReleaseMouseCapture();

            e.Handled = true;
        }

        void Dial_MouseMove(object sender, MouseEventArgs e)
        {
            if (dragging)
            {
                Point currentPos = e.GetPosition(this);

                double delta = dragStart.Y - currentPos.Y;
                double pixelScale = (this.Maximum - this.Minimum) / 100;
                double newValue = valAtStart + delta * pixelScale;

                if (newValue > this.Maximum)
                {
                    this.Value = this.Maximum;
                }
                else if (newValue < this.Minimum)
                {
                    this.Value = this.Minimum;
                }
                else
                {
                    this.Value = newValue;
                }

                e.Handled = true;
            }
        }

        protected override void OnLostMouseCapture(MouseEventArgs e)
        {
            if (dragging)
            {
                dragging = false;

                EditEnded?.Invoke(this, EventArgs.Empty);
            }

            System.Windows.Forms.Cursor.Position = absDragStart;
            this.ClearValue(FrameworkElement.CursorProperty);

            base.OnLostMouseCapture(e);
        }

        void Dial_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            this.Value = DefaultValue;
        }
    }

    public sealed class DialToAngleConverter : IMultiValueConverter
    {
        public object Convert(object[] value, Type targetType, object parameter, CultureInfo culture)
        {
            Dial dial = (Dial)(value[0]);

            double val = (dial.Value - dial.Minimum) / (dial.Maximum - dial.Minimum);

            double angle = 143;

            return -angle + (val * angle * 2);
        }

        public object[] ConvertBack(object value, Type[] targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException("DialToAngleConverter.ConvertBack is not supported.");
        }
    }
}
