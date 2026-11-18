using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;

namespace AlbumArtTool.Ui
{
    // The parts of the Silver and Black finishes that XAML cannot express: the bundled fonts, shadow tokens,
    // tiled textures (brushed metal, display dots, meter segments) and the capitals printed on keys.
    internal static class Skin
    {
        private static readonly string Assets = "avares://" + typeof(Skin).Assembly.GetName().Name + "/Assets/Fonts#";
        /// <summary>Everything you read: lists, descriptions, fields.</summary>
        internal static readonly FontFamily Text = new FontFamily(Assets + "Archivo");
        /// <summary>Keys, switches, navigation and status tags (drawn in capitals).</summary>
        internal static readonly FontFamily Key = new FontFamily(Assets + "ArchivoSemiExpanded");
        /// <summary>Album and artist names in details panes, dialog titles.</summary>
        internal static readonly FontFamily Display = new FontFamily(Assets + "ArchivoExpanded");
        /// <summary>Printed legends: section labels, units, the wordmark.</summary>
        internal static readonly FontFamily Legend = new FontFamily(Assets + "Michroma");
        /// <summary>Dot-matrix readouts inside display windows only.</summary>
        internal static readonly FontFamily Readout = new FontFamily(Assets + "Doto");

        internal static readonly Color Orange = Color.Parse("#FF5B14"), Amber = Color.Parse("#FFB347"), Red = Color.Parse("#FF3B30"), Green = Color.Parse("#34D17A");

        private static bool installed;

        internal static void Install(Application application)
        {
            if (installed || application == null) return;
            installed = true;
            var resources = application.Resources;
            resources["TextFont"] = Text; resources["KeyFont"] = Key; resources["DisplayFont"] = Display;
            resources["LegendFont"] = Legend; resources["ReadoutFont"] = Readout;
            resources["DisplayMeterFill"] = Segments(Amber, 255, 6, 2, 9);
            resources["DisplayMeterTrack"] = Segments(Amber, 34, 6, 2, 9);
            resources["DisplayDots"] = Dots();
            if (resources.ThemeDictionaries.TryGetValue(ThemeVariant.Light, out var lightProvider) && lightProvider is ResourceDictionary light)
            {
                light["PanelEdge"] = BoxShadows.Parse("0 1 0 0 #FAFAF7");
                light["KeyLip"] = BoxShadows.Parse("0 1 0 0 #FAFAF7");
                light["PressedIn"] = BoxShadows.Parse("inset 0 2 3 0 #2E000000");
                light["PressedEdge"] = BoxShadows.Parse("inset 3 0 0 0 #FF5B14, inset 0 2 4 0 #2A000000");
                light["Well"] = BoxShadows.Parse("inset 0 1 3 0 #22000000");
                light["GlassDepth"] = BoxShadows.Parse("inset 0 2 10 0 #CC000000, 0 1 0 0 #FAFAF7");
                light["MeterFill"] = Segments(Orange, 255, 6, 2, 8);
                light["MeterTrack"] = Segments(Color.Parse("#B6B5AF"), 140, 6, 2, 8);
                light["FaceTexture"] = Brushed();
            }
            if (resources.ThemeDictionaries.TryGetValue(ThemeVariant.Dark, out var darkProvider) && darkProvider is ResourceDictionary dark)
            {
                dark["PanelEdge"] = BoxShadows.Parse("0 0 0 0 #00000000");
                dark["KeyLip"] = BoxShadows.Parse("0 1 0 0 #0A0A0A");
                dark["PressedIn"] = BoxShadows.Parse("inset 0 2 4 0 #99000000");
                dark["PressedEdge"] = BoxShadows.Parse("inset 3 0 0 0 #FF5B14, inset 0 2 5 0 #99000000");
                dark["Well"] = BoxShadows.Parse("inset 0 1 3 0 #99000000");
                dark["GlassDepth"] = BoxShadows.Parse("inset 0 2 10 0 #FF000000");
                dark["MeterFill"] = Segments(Orange, 255, 6, 2, 8);
                dark["MeterTrack"] = Segments(Color.Parse("#3A3A38"), 255, 6, 2, 8);
                dark["FaceTexture"] = Brushes.Transparent;
            }
            // Switches, steps and list keys latch when pressed: they sink, then spring back (sidebar keys latch in AddNav).
            Button.ClickEvent.AddClassHandler<Button>((button, e) =>
            {
                if (button.Classes.Contains("chip") || button.Classes.Contains("step") || button.Classes.Contains("item")) Motion.Latch(button);
            });
            // Drop-down lists, menus and flyouts scale with the zoom of the window they open from.
            application.Styles.Add(new Style(x => x.OfType<Avalonia.Controls.Primitives.Popup>()) { Setters = { new Setter(Avalonia.Controls.Primitives.Popup.InheritsTransformProperty, true) } });
            // Keys print their labels in capitals; the label itself stays as written, for tests, tooltips and screen readers.
            application.Styles.Add(new Style(x => x.OfType<Button>()) { Setters = { new Setter(ContentControl.ContentTemplateProperty, Caps) } });
            application.Styles.Add(new Style(x => x.OfType<Button>().Class("chip")) { Setters = { new Setter(ContentControl.ContentTemplateProperty, Switch) } });
        }

        /// <summary>A string label drawn in tracked capitals.</summary>
        // A key whose content is already a control (an icon and label, a sidebar key) shows that control unchanged.
        internal static readonly IDataTemplate Caps = new FuncDataTemplate<object>((value, scope) => value as Control ?? CapsText.Create(value?.ToString() ?? ""), false);

        /// <summary>A switch: an LED (lit when the switch is chosen) and its label in capitals.</summary>
        internal static readonly IDataTemplate Switch = new FuncDataTemplate<object>((value, scope) =>
        {
            if (value is Control control) return control;
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(Kit.Led()); row.Children.Add(CapsText.Create(value?.ToString() ?? ""));
            return row;
        }, false);

        // Repeating meter segments: a lit block and a gap, tiled along the bar.
        private static IBrush Segments(Color color, byte alpha, int segment, int gap, int height)
        {
            int width = segment + gap;
            var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            Fill(bitmap, (x, y) => x < segment ? Color.FromArgb(alpha, color.R, color.G, color.B) : Colors.Transparent);
            return Tile(bitmap, width, height);
        }

        // The faint grid of unlit dots behind a display's readouts.
        private static IBrush Dots()
        {
            var bitmap = new WriteableBitmap(new PixelSize(4, 4), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            Fill(bitmap, (x, y) => x == 1 && y == 1 ? Color.FromArgb(20, Amber.R, Amber.G, Amber.B) : Colors.Transparent);
            return Tile(bitmap, 4, 4);
        }

        // Brushed aluminium: alternating light and dark hairlines, two pixels apart.
        private static IBrush Brushed()
        {
            var bitmap = new WriteableBitmap(new PixelSize(2, 2), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            Fill(bitmap, (x, y) => y == 0 ? Color.FromArgb(9, 255, 255, 255) : Color.FromArgb(7, 0, 0, 0));
            return Tile(bitmap, 2, 2);
        }

        private static IBrush Tile(Bitmap bitmap, int width, int height) => new ImageBrush(bitmap)
        {
            TileMode = TileMode.Tile, Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top,
            DestinationRect = new RelativeRect(0, 0, width, height, RelativeUnit.Absolute),
            SourceRect = new RelativeRect(0, 0, width, height, RelativeUnit.Absolute)
        };

        private static unsafe void Fill(WriteableBitmap bitmap, Func<int, int, Color> pixel)
        {
            using (var buffer = bitmap.Lock())
            {
                for (int y = 0; y < buffer.Size.Height; y++)
                {
                    var row = (byte*)buffer.Address + y * buffer.RowBytes;
                    for (int x = 0; x < buffer.Size.Width; x++)
                    {
                        var c = pixel(x, y); double a = c.A / 255.0;
                        row[x * 4 + 0] = (byte)Math.Round(c.B * a); row[x * 4 + 1] = (byte)Math.Round(c.G * a);
                        row[x * 4 + 2] = (byte)Math.Round(c.R * a); row[x * 4 + 3] = c.A;
                    }
                }
            }
        }

        /// <summary>The amber halo around display text (a static shadow; only its opacity ever changes).</summary>
        internal static IEffect Glow(Color color, double blur = 10, double opacity = 0.6) =>
            new DropShadowEffect { OffsetX = 0, OffsetY = 0, BlurRadius = blur, Color = color, Opacity = opacity };
    }

    /// <summary>A label printed in capitals. <see cref="Source"/> keeps the words as written.</summary>
    internal sealed class CapsText : TextBlock
    {
        protected override Type StyleKeyOverride => typeof(TextBlock);
        private string source = "";

        internal string Source
        {
            get => source;
            set { source = value ?? ""; Text = source.ToUpperInvariant(); }
        }

        internal static CapsText Create(string text) => new CapsText
        {
            Source = text, LetterSpacing = 0.7, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
        };
    }
}
