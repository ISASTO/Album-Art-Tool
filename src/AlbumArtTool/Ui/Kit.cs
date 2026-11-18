using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Media.Immutable;

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

        /// <summary>The button's label, also for icon buttons (icon + text).</summary>
        internal static string Text(this Button button) => button.Content switch
        {
            string text => text,
            CapsText caps => caps.Source,
            Panel panel => panel.Children.OfType<TextBlock>().LastOrDefault() is TextBlock label ? (label is CapsText c ? c.Source : label.Text ?? "") : "",
            _ => ""
        };

        /// <summary>Changes a button's label without removing its icon.</summary>
        internal static void SetText(this Button button, string text)
        {
            if (button.Content is Panel panel && panel.Children.OfType<TextBlock>().LastOrDefault() is TextBlock label) { if (label is CapsText caps) caps.Source = text; else label.Text = text; }
            else button.Content = text;
        }

        /// <summary>A button with a vector icon and an optional label (icon-only buttons need a Named() and a Tip()).</summary>
        internal static Button IconButton(string icon, string text, params string[] classes)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(Glyph(icon));
            if (!string.IsNullOrEmpty(text)) row.Children.Add(CapsText.Create(text));
            var button = new Button { Content = row };
            foreach (string name in classes) button.Classes.Add(name);
            if (string.IsNullOrEmpty(text)) button.Classes.Add("square");
            return button;
        }

        internal static void SetIcon(Button button, string data)
        {
            if (button.Content is Panel panel && panel.Children.OfType<PathIcon>().FirstOrDefault() is PathIcon icon) SetIcon(icon, data);
        }

        internal static void SetIcon(PathIcon icon, string data)
        {
            try { icon.Data = StreamGeometry.Parse(data); } catch (Exception) { }
        }

        internal static PathIcon Glyph(string data, double size = 14)
        {
            Geometry geometry = null;
            try { geometry = StreamGeometry.Parse(data); } catch (Exception) { /* A bad icon must never stop a window from opening. */ }
            return new PathIcon { Data = geometry, Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
        }

        /// <summary>Shortens a path in the middle, keeping the drive/first folder and the last folders: C:\Users\…\Music\Albums.</summary>
        internal static string MiddleEllipsis(string path, int max = 60)
        {
            if (string.IsNullOrEmpty(path) || path.Length <= max) return path ?? "";
            char separator = path.Contains('\\') ? '\\' : '/';
            var parts = path.Split(separator);
            string head = parts.Length > 1 ? parts[0] + separator : "";
            for (int keep = parts.Length - 1; keep >= 1; keep--)
            {
                string tail = string.Join(separator, parts.Skip(parts.Length - keep));
                string candidate = head + "…" + separator + tail;
                if (candidate.Length <= max && parts.Length - keep > 1) return candidate;
            }
            return "…" + path.Substring(path.Length - (max - 1));
        }

        internal static TextBlock Label(string text, double size = Body, bool bold = false, string cls = null)
        {
            var label = new TextBlock { Text = text, FontSize = size, FontWeight = bold ? FontWeight.Bold : FontWeight.Normal,
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            if (cls != null) label.Classes.Add(cls);
            return label;
        }

        internal static CheckBox Check(string text, bool value) => new CheckBox { Content = text, IsChecked = value, VerticalAlignment = VerticalAlignment.Center };
        internal static bool On(this CheckBox box) => box.IsChecked == true;

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

        /// <summary>A panel: flat, outlined, one depth above the faceplate (corners come from the theme).</summary>
        internal static Border Frost(Control child, Thickness padding, double radius = 6) =>
            new Border { Child = child, Padding = padding, Classes = { "frost" } };

        /// <summary>An indicator light. Classes: on (orange), red, green, amber; none = unlit.</summary>
        internal static Border Led(string tone = null)
        {
            var led = new Border { Classes = { "led" } };
            if (!string.IsNullOrEmpty(tone)) led.Classes.Add(tone);
            return led;
        }

        internal static void SetLed(Border led, string tone)
        {
            foreach (string t in new[] { "on", "red", "green", "amber" }) led.Classes.Set(t, t == tone);
        }

        /// <summary>A small printed legend in wide capitals (section labels, units).</summary>
        internal static TextBlock Legend(string text, double size = 9.5)
        {
            var legend = Caption(text); legend.FontSize = size;
            return legend;
        }

        /// <summary>Dot-matrix amber text with a soft glow, for display windows only.</summary>
        internal static TextBlock Readout(string text, double size = 28)
        {
            var readout = new TextBlock { Text = text, FontFamily = Theme.Readout, FontWeight = FontWeight.Bold, FontSize = size, VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Theme.Amber), Effect = Theme.Glow(Theme.Amber, size > 16 ? 12 : 7, size > 16 ? 0.65 : 0.5) };
            return readout;
        }

        internal static IEnumerable<Control> Descendants(Control root) => Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(root).OfType<Control>();

        /// <summary>A small uppercase section label.</summary>
        internal static TextBlock Caption(string text) => new TextBlock { Text = text.ToUpperInvariant(), Classes = { "caption" }, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };

        /// <summary>A number printed on a panel with its unit as a small legend: 320 KBPS.</summary>
        internal static Control Figure(string value, string unit, out TextBlock number)
        {
            number = new TextBlock { Text = value, FontFamily = Theme.Key, FontWeight = FontWeight.SemiBold, FontSize = 18, VerticalAlignment = VerticalAlignment.Bottom };
            var legend = Legend(unit, 8.5); legend.Margin = new Thickness(5, 0, 0, 3); legend.VerticalAlignment = VerticalAlignment.Bottom;
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(number); row.Children.Add(legend);
            return row;
        }

        internal static Border Inset(Control child) => new Border { Child = child, Classes = { "inset" } };

        private static WindowIcon icon;
        private static Bitmap mark;
        private static Uri IconUri => new Uri("avares://" + typeof(Kit).Assembly.GetName().Name + "/Assets/AppIcon.ico");
        internal static WindowIcon Icon => icon ??= new WindowIcon(AssetLoader.Open(IconUri));
        internal static Bitmap Mark => mark ??= new Bitmap(AssetLoader.Open(IconUri));

        internal static Bitmap Decode(byte[] bytes)
        {
            if (bytes == null) return null;
            using (var stream = new MemoryStream(bytes, false)) return new Bitmap(stream);
        }

        /// <summary>Applies the shared window chrome: icon, the faceplate's brushed texture, native title bar colors.</summary>
        internal static void Prepare(Window window, Control content)
        {
            window.Icon = Icon;
            Theme.Install(Application.Current);
            var host = new Panel();
            host.Children.Add(new Border { IsHitTestVisible = false }.Brush(Border.BackgroundProperty, "FaceTexture"));
            host.Children.Add(content);
            window.Content = host;
            window.Opened += (s, e) => Appearance.ApplyNativeFrame(window);
            Appearance.Track(window);
        }
    }
}
