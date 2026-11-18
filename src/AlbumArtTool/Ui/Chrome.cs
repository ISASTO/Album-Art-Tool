using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace AlbumArtTool.Ui
{
    // The window frame as part of the faceplate. On Windows the system title bar is replaced by the app's own window
    // keys (minimize, maximize, close) and drag areas. They are marked for Windows as the real caption, caption buttons
    // and close box, so dragging, double-click to maximize, Aero Snap, the Windows 11 snap layouts on the maximize key
    // and the window menu (Alt+Space, right-click) all behave natively. The resize borders and shadow stay the system's.
    internal static class Chrome
    {
        /// <summary>Whether windows use the app's own title bar (Windows; tests switch it on to check and show it).</summary>
        internal static bool Enabled = OperatingSystem.IsWindows();
        internal const double KeyWidth = 46, KeyHeight = 32;

        /// <summary>Extends a window's content into its title bar and keeps it clear of the screen edge when maximized.</summary>
        internal static void Extend(Window window)
        {
            window.ExtendClientAreaToDecorationsHint = true;
            window.ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome;
            window.ExtendClientAreaTitleBarHeightHint = -1;
            window.PropertyChanged += (s, e) =>
            {
                if (e.Property == Window.OffScreenMarginProperty || e.Property == Window.WindowStateProperty) window.Padding = window.OffScreenMargin;
            };
        }

        /// <summary>Marks a control (and everything in it that is not a key or field) as a place to drag the window from.</summary>
        internal static T Drag<T>(T control) where T : Control
        {
            Win32Properties.SetNonClientHitTestResult(control, Win32Properties.Win32HitTestValue.Caption);
            if (control is Border border && border.Background == null) border.Background = Brushes.Transparent;
            // Windows handles dragging itself; elsewhere (and in tests) the app does.
            if (!OperatingSystem.IsWindows())
                control.PointerPressed += (s, e) =>
                {
                    if (e.Source is Visual source && Interactive(source, control)) return;
                    var window = TopLevel.GetTopLevel(control) as Window;
                    if (window == null || !e.GetCurrentPoint(control).Properties.IsLeftButtonPressed) return;
                    if (e.ClickCount == 2 && window.CanResize) ToggleMaximize(window);
                    else window.BeginMoveDrag(e);
                };
            return control;
        }

        private static bool Interactive(Visual source, Visual stop)
        {
            for (var v = source; v != null && v != stop; v = v.GetVisualParent())
                if (v is Button || v is TextBox || v is ComboBox || v is CheckBox) return true;
            return false;
        }

        // Keys and fields inside a drag area stay clickable.
        internal static T Client<T>(T control) where T : Control
        {
            Win32Properties.SetNonClientHitTestResult(control, Win32Properties.Win32HitTestValue.Client);
            return control;
        }

        internal static void ToggleMaximize(Window window) =>
            window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        /// <summary>The window keys, flush with the window's top-right corner: minimize and maximize (when the window can resize) and close.</summary>
        internal static Control Keys(Window window)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
            if (window.CanResize)
            {
                var minimize = Key(Icons.Minimize, "Minimize", Win32Properties.Win32HitTestValue.MinButton);
                minimize.Click += (s, e) => window.WindowState = WindowState.Minimized;
                var maximize = Key(Icons.Maximize, "Maximize", Win32Properties.Win32HitTestValue.MaxButton);
                maximize.Click += (s, e) => ToggleMaximize(window);
                void Sync()
                {
                    bool maximized = window.WindowState == WindowState.Maximized;
                    Kit.SetIcon(maximize, maximized ? Icons.Restore : Icons.Maximize);
                    maximize.Named(maximized ? "Restore" : "Maximize").Tip(maximized ? "Restore down" : "Maximize");
                }
                window.PropertyChanged += (s, e) => { if (e.Property == Window.WindowStateProperty) Sync(); };
                row.Children.Add(minimize); row.Children.Add(maximize);
            }
            var close = Key(Icons.WindowClose, "Close", Win32Properties.Win32HitTestValue.Close);
            close.Classes.Add("close");
            close.Click += (s, e) => window.Close();
            row.Children.Add(close);
            return row;
        }

        private static Button Key(string icon, string name, Win32Properties.Win32HitTestValue role)
        {
            var key = new Button { Content = new StackPanel { Children = { Kit.Glyph(icon, 10) }, HorizontalAlignment = HorizontalAlignment.Center }, Classes = { "windowKey" }, Width = KeyWidth, Height = KeyHeight };
            key.Named("Window " + name.ToLowerInvariant()).Tip(name);
            key.Focusable = false;
            Win32Properties.SetNonClientHitTestResult(key, role);
            return key;
        }

        /// <summary>A dialog's title bar: its title printed as a legend, a drag area, and its window keys.</summary>
        internal static Control TitleBar(Window window)
        {
            var title = new CapsText { Source = window.Title ?? "", FontFamily = Skin.Legend, FontSize = 9, LetterSpacing = 1.6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 1, 12, 0) };
            title.Brush(TextBlock.ForegroundProperty, "Muted");
            window.PropertyChanged += (s, e) => { if (e.Property == Window.TitleProperty) title.Source = window.Title ?? ""; };
            var bar = Kit.Grid("*,Auto", "Auto").Add(Drag(new Border { Child = title, Height = KeyHeight }).Cell(0, 0), Keys(window).Cell(0, 1));
            return bar;
        }
    }
}
