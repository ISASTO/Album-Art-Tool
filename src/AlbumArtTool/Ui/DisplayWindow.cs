using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace AlbumArtTool.Ui
{
    // The display window: recessed amber glass across the top of a page. Readouts glow in dot matrix on the left
    // (the page and up to three counts); what is running shows on the right as a legend, a segmented meter and a
    // one-line message. Sentences that need reading stay printed on the panels.
    internal sealed class DisplayWindow : Border
    {
        private readonly StackPanel readouts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 30, VerticalAlignment = VerticalAlignment.Center };
        private readonly List<(TextBlock Label, TextBlock Value, StackPanel Box)> slots = new List<(TextBlock, TextBlock, StackPanel)>();
        private readonly TextBlock activity = Label(""), message = Kit.Readout("", 15);
        private readonly StackPanel right = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 4 };
        private readonly Panel meterHost = new Panel();
        private readonly TextBlock bootText = Kit.Readout("", 30);
        private readonly Canvas bootLane = new Canvas { ClipToBounds = true, IsVisible = false, IsHitTestVisible = false };
        private bool booting;

        internal DisplayWindow()
        {
            this.Brush(BackgroundProperty, "Glass");
            this.Brush(BoxShadowProperty, "GlassDepth");
            CornerRadius = new CornerRadius(6); Padding = new Thickness(20, 9, 20, 10); MinHeight = 76; ClipToBounds = true;
            var dots = new Border { IsHitTestVisible = false, Margin = new Thickness(-20, -9, -20, -10) }.Brush(Border.BackgroundProperty, "DisplayDots");
            message.TextTrimming = TextTrimming.CharacterEllipsis; message.HorizontalAlignment = HorizontalAlignment.Right; message.MaxWidth = 640;
            message.Named("Display message");
            activity.HorizontalAlignment = HorizontalAlignment.Right;
            meterHost.HorizontalAlignment = HorizontalAlignment.Right;
            right.Children.Add(activity); right.Children.Add(meterHost); right.Children.Add(message);
            // The readouts keep their width; the message takes what is left and trims with an ellipsis.
            right.Margin = new Thickness(28, 0, 0, 0);
            var grid = Kit.Grid("Auto,*", "*").Add(readouts.Cell(0, 0), right.Cell(0, 1));
            bootLane.Children.Add(bootText);
            var host = new Panel(); host.Children.Add(dots); host.Children.Add(grid); host.Children.Add(bootLane);
            Child = host;
            PointerPressed += (s, e) => { if (booting) FinishBoot(); };
        }

        private static TextBlock Label(string text)
        {
            var label = new TextBlock { Text = text, FontFamily = Skin.Legend, FontSize = 8, LetterSpacing = 1.6, Foreground = new SolidColorBrush(Skin.Amber), Opacity = 0.72,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(1, 0, 0, 3) };
            return label;
        }

        /// <summary>The first readout is the page (MODE), then up to three labelled counts. Numbers count to their new value.</summary>
        internal void SetReadouts(params (string Label, string Value)[] items)
        {
            while (slots.Count < items.Length)
            {
                var label = Label(""); var value = Kit.Readout("", 28);
                var box = new StackPanel(); box.Children.Add(label); box.Children.Add(value);
                slots.Add((label, value, box)); readouts.Children.Add(box);
            }
            for (int i = 0; i < slots.Count; i++)
            {
                var slot = slots[i];
                slot.Box.IsVisible = i < items.Length;
                if (i >= items.Length) continue;
                slot.Label.Text = items[i].Label.ToUpperInvariant();
                string value = (items[i].Value ?? "").ToUpperInvariant();
                if (i == 0) slot.Value.Text = value; else Motion.Count(slot.Value, value);
            }
        }

        internal IReadOnlyList<string> Readouts => slots.Where(s => s.Box.IsVisible).Select(s => s.Value.Text).ToList();

        /// <summary>The meter shown on the right (a ProgressBar styled as amber segments).</summary>
        internal void SetMeter(Control meter, Control percent = null)
        {
            meterHost.Children.Clear();
            if (meter == null) return;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
            row.Children.Add(meter); if (percent != null) row.Children.Add(percent);
            meterHost.Children.Add(row);
        }

        /// <summary>What is running: a legend above the meter, and a one-line message below it.</summary>
        internal void SetActivity(string legend, string text)
        {
            activity.Text = (legend ?? "").ToUpperInvariant(); activity.IsVisible = activity.Text.Length > 0;
            message.Text = (text ?? "").ToUpperInvariant(); message.IsVisible = message.Text.Length > 0;
            ToolTip.SetTip(message, string.IsNullOrEmpty(text) ? null : text);
        }

        internal string Message => message.Text;
        internal TextBlock MessageText => message;

        /// <summary>The start-up message: the name scrolls past in dot matrix once, then the readouts light up. Click to skip.</summary>
        internal void Boot(string text)
        {
            if (!Motion.Allowed) return;
            booting = true; bootText.Text = text.ToUpperInvariant(); bootLane.IsVisible = true;
            readouts.Opacity = 0; right.Opacity = 0;
            void Start()
            {
                double width = Math.Max(Bounds.Width, 600);
                bootText.Measure(Size.Infinity);
                double travel = width + bootText.DesiredSize.Width;
                Canvas.SetTop(bootText, Math.Max(0, (Bounds.Height - Padding.Top - Padding.Bottom - bootText.DesiredSize.Height) / 2));
                Motion.Run(bootText, 1100, t => t, t => Canvas.SetLeft(bootText, width - travel * t), FinishBoot);
            }
            if (Bounds.Width > 0) Start(); else LayoutUpdated += OnLayout;
            void OnLayout(object s, EventArgs e) { if (Bounds.Width <= 0) return; LayoutUpdated -= OnLayout; Start(); }
        }

        private void FinishBoot()
        {
            if (!booting) return;
            booting = false; Motion.Stop(bootText); bootLane.IsVisible = false;
            Motion.Fade(readouts, 1, 220); Motion.Fade(right, 1, 220);
        }
    }

    // A faint wash of the selected cover's two main colors behind its details. Two layers crossfade when the album changes.
    internal sealed class CoverWash : Panel
    {
        private readonly Border[] layers = { new Border { IsHitTestVisible = false }, new Border { IsHitTestVisible = false } };
        private int front;
        private string shown;
        private static readonly Dictionary<string, (Color, Color)?> cache = new Dictionary<string, (Color, Color)?>();

        internal CoverWash()
        {
            IsHitTestVisible = false;
            foreach (var layer in layers) { layer.Opacity = 0; Children.Add(layer); }
            ClipToBounds = true;
        }

        /// <summary>Shows the wash for an album's cover (a null thumbnail clears it). Colors are read once per album.</summary>
        internal void Show(string key, byte[] thumbnail)
        {
            if (key == shown) return;
            shown = key;
            (Color, Color)? colors = null;
            if (key != null && thumbnail != null && !cache.TryGetValue(key, out colors))
            {
                colors = CoverColors.Read(thumbnail);
                if (cache.Count > 500) cache.Clear();
                cache[key] = colors;
            }
            var old = layers[front]; front = 1 - front; var next = layers[front];
            if (colors is (Color a, Color b))
            {
                bool dark = Appearance.IsDark;
                byte strong = (byte)(dark ? 70 : 52), soft = (byte)(dark ? 92 : 58);
                var brush1 = new RadialGradientBrush { Center = new RelativePoint(0.12, 0, RelativeUnit.Relative), GradientOrigin = new RelativePoint(0.12, 0, RelativeUnit.Relative), Radius = 0.75 };
                brush1.GradientStops.Add(new GradientStop(Color.FromArgb(strong, a.R, a.G, a.B), 0)); brush1.GradientStops.Add(new GradientStop(Color.FromArgb(0, a.R, a.G, a.B), 1));
                var brush2 = new RadialGradientBrush { Center = new RelativePoint(0.95, 0.08, RelativeUnit.Relative), GradientOrigin = new RelativePoint(0.95, 0.08, RelativeUnit.Relative), Radius = 0.6 };
                brush2.GradientStops.Add(new GradientStop(Color.FromArgb(soft, b.R, b.G, b.B), 0)); brush2.GradientStops.Add(new GradientStop(Color.FromArgb(0, b.R, b.G, b.B), 1));
                next.Background = brush1;
                next.Child = new Border { Background = brush2, IsHitTestVisible = false };
                Motion.Fade(next, 1);
            }
            Motion.Fade(old, 0);
        }

        internal void Clear() => Show(null, null);
    }

    internal static class CoverColors
    {
        /// <summary>The two most prominent colors of a small image: the most common saturated color, and the common color least like it.</summary>
        internal static (Color, Color)? Read(byte[] image)
        {
            try
            {
                using (var bitmap = SkiaSharp.SKBitmap.Decode(image))
                {
                    if (bitmap == null) return null;
                    using (var small = bitmap.Resize(new SkiaSharp.SKImageInfo(24, 24), SkiaSharp.SKFilterQuality.Low))
                    {
                        if (small == null) return null;
                        var buckets = new Dictionary<int, (double Weight, double R, double G, double B)>();
                        for (int y = 0; y < small.Height; y++)
                            for (int x = 0; x < small.Width; x++)
                            {
                                var c = small.GetPixel(x, y);
                                int max = Math.Max(c.Red, Math.Max(c.Green, c.Blue)), min = Math.Min(c.Red, Math.Min(c.Green, c.Blue));
                                double saturation = max == 0 ? 0 : (max - min) / (double)max;
                                double weight = 0.25 + saturation;
                                int key = (c.Red >> 5) << 6 | (c.Green >> 5) << 3 | (c.Blue >> 5);
                                buckets.TryGetValue(key, out var b);
                                buckets[key] = (b.Weight + weight, b.R + c.Red * weight, b.G + c.Green * weight, b.B + c.Blue * weight);
                            }
                        var ranked = buckets.Values.OrderByDescending(b => b.Weight).Select(b => Color.FromRgb((byte)(b.R / b.Weight), (byte)(b.G / b.Weight), (byte)(b.B / b.Weight))).ToList();
                        if (ranked.Count == 0) return null;
                        var first = ranked[0];
                        var second = ranked.Skip(1).Take(6).OrderByDescending(c => Distance(c, first)).FirstOrDefault();
                        if (ranked.Count == 1) second = first;
                        return (first, second);
                    }
                }
            }
            catch (Exception) { return null; }
        }

        private static double Distance(Color a, Color b) => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
    }
}
