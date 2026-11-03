using System;
using System.IO;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace AlbumArtTool.Ui
{
    // Small factories so windows read like their layout. Sizes are in device-independent
    // pixels: 13.33 px = the 10 pt Segoe UI used across Windows.
    internal static class Kit
    {
        internal const double Body = 13.33, Small = 12, Tiny = 10.67;

        internal static Button Button(string text, params string[] classes)
        {
            var button = new Button { Content = text };
            foreach (string name in classes) button.Classes.Add(name);
            return button;
        }

        internal static string Text(this Button button) => button.Content as string ?? "";

        internal static TextBlock Label(string text, double size = Body, bool bold = false, string cls = null)
        {
            var label = new TextBlock { Text = text, FontSize = size, FontWeight = bold ? FontWeight.Bold : FontWeight.Normal,
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            if (cls != null) label.Classes.Add(cls);
            return label;
        }

        internal static CheckBox Check(string text, bool value) => new CheckBox { Content = text, IsChecked = value, VerticalAlignment = VerticalAlignment.Center };
        internal static bool Checked(this CheckBox box) => box.IsChecked == true;

        internal static Grid Grid(string columns, string rows)
        {
            var grid = new Grid();
            if (!string.IsNullOrEmpty(columns)) grid.ColumnDefinitions = new ColumnDefinitions(columns);
            if (!string.IsNullOrEmpty(rows)) grid.RowDefinitions = new RowDefinitions(rows);
            return grid;
        }

        internal static T Cell<T>(this T control, int row, int column, int columnSpan = 1) where T : Control
        {
            Avalonia.Controls.Grid.SetRow(control, row); Avalonia.Controls.Grid.SetColumn(control, column);
            if (columnSpan > 1) Avalonia.Controls.Grid.SetColumnSpan(control, columnSpan);
            return control;
        }

        internal static Grid Add(this Grid grid, params Control[] children) { foreach (var child in children) grid.Children.Add(child); return grid; }

        internal static T Named<T>(this T control, string name) where T : Control { AutomationProperties.SetName(control, name); return control; }
        internal static string AccessibleName(this Control control) => AutomationProperties.GetName(control);
        internal static T Tip<T>(this T control, string text) where T : Control { ToolTip.SetTip(control, string.IsNullOrEmpty(text) ? null : text); return control; }

        internal static T Brush<T>(this T control, AvaloniaProperty property, string key) where T : Control
        { control.Bind(property, control.GetResourceObservable(key)); return control; }

        internal static Border Frost(Control child, Thickness padding, double radius = 18) =>
            new Border { Child = child, Padding = padding, CornerRadius = new CornerRadius(radius), Classes = { "frost" } };

        internal static Border Inset(Control child) => new Border { Child = child, Classes = { "inset" } };

        private static WindowIcon icon;
        private static Bitmap mark;
        internal static WindowIcon Icon => icon ??= new WindowIcon(AssetLoader.Open(new Uri("avares://AlbumArtTool/Assets/AppIcon.ico")));
        internal static Bitmap Mark => mark ??= new Bitmap(AssetLoader.Open(new Uri("avares://AlbumArtTool/Assets/AppIcon.ico")));

        internal static Bitmap Decode(byte[] bytes)
        {
            if (bytes == null) return null;
            using (var stream = new MemoryStream(bytes, false)) return new Bitmap(stream);
        }

        /// <summary>Applies the shared window chrome: icon, canvas glow, native title bar colors.</summary>
        internal static void Prepare(Window window, Control content)
        {
            window.Icon = Icon;
            var host = new Panel();
            host.Children.Add(new Border { IsHitTestVisible = false }.Brush(Border.BackgroundProperty, "GlowA"));
            host.Children.Add(new Border { IsHitTestVisible = false }.Brush(Border.BackgroundProperty, "GlowB"));
            host.Children.Add(content);
            window.Content = host;
            window.Opened += (s, e) => Theme.ApplyNativeFrame(window);
            Theme.Track(window);
        }
    }
}
