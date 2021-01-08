using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Runtime.InteropServices;
using AudioPlugSharp;

namespace AudioPlugSharpWPF
{
    public class EditorWindow : Window
    {
        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hwnd, ref Rectangle rectangle);

        public IAudioPluginEditor Editor { get; private set; }
        public UserControl EditorView { get; private set; }

        public Rectangle DisplayRectangle
        {
            get
            {
                Rectangle displayRectangle = new Rectangle();

                GetWindowRect(parentWindow, ref displayRectangle);

                return displayRectangle;
            }
        }

        IntPtr parentWindow;

        public EditorWindow(IAudioPluginEditor editor, UserControl editorView)
        {
            this.Editor = editor;
            this.EditorView = editorView;

            DataContext = Editor.Processor;
        }

        public void Show(IntPtr parentWindow)
        {
            this.parentWindow = parentWindow;

            Content = EditorView;

            Top = 0;
            Left = 0;
            ShowInTaskbar = false;
            WindowStyle = System.Windows.WindowStyle.None;
            ResizeMode = System.Windows.ResizeMode.NoResize;

            Show();

            var windowHwnd = new System.Windows.Interop.WindowInteropHelper(this);
            IntPtr hWnd = windowHwnd.Handle;
            SetParent(hWnd, parentWindow);
        }
    }
}
