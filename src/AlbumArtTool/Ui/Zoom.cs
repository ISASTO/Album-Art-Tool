using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using AlbumArtTool.Core;

namespace AlbumArtTool.Ui
{
    // Zoom, as in a browser or Spotify: Ctrl + mouse wheel, Ctrl + plus / minus, and Ctrl + 0 to reset (on a Mac ⌘ + plus /
    // minus / 0, and pinching on the trackpad). Every window's content scales (drop-down lists and menus follow it). The
    // main window keeps its size and its content reflows; dialogs grow or shrink with their content. The level is
    // remembered on this computer.
    internal static class Zoom
    {
        internal static readonly double[] Levels = { 0.75, 0.8, 0.9, 1.0, 1.1, 1.25, 1.4, 1.5 };
        internal static double Level { get; private set; } = 1;
        /// <summary>Raised after the level changes (the main window shows it on its display).</summary>
        internal static event Action Changed;
        /// <summary>Where the level is saved; null (tests) keeps it in memory only.</summary>
        internal static string SettingsFile = Path.Combine(BackupSettings.AppData, "zoom.txt");

        private sealed class Scaled
        {
            internal Window Window;
            internal LayoutTransformControl Host;
            internal bool KeepsSize;
            internal double Width, Height, MinWidth, MinHeight;
            internal double Wheel;
        }

        private static readonly List<Scaled> windows = new List<Scaled>();
        private static bool loaded;

        internal static void Load()
        {
            if (loaded) return;
            loaded = true;
            if (SettingsFile != null && double.TryParse(AppSettings.Read(SettingsFile), NumberStyles.Float, CultureInfo.InvariantCulture, out double saved))
                Level = Nearest(saved);
        }

        private static double Nearest(double value) => Levels.OrderBy(l => Math.Abs(l - value)).First();

        /// <summary>Wraps a window's content so it scales, and handles the zoom keys and wheel in that window.</summary>
        /// <param name="keepsSize">True for the main window: zooming reflows its content instead of resizing it.</param>
        internal static Control Attach(Window window, Control content, bool keepsSize = false)
        {
            Load();
            var entry = new Scaled
            {
                Window = window, Host = new LayoutTransformControl { Child = content }, KeepsSize = keepsSize,
                Width = window.Width, Height = window.Height, MinWidth = window.MinWidth, MinHeight = window.MinHeight
            };
            windows.Add(entry);
            window.Closed += (s, e) => windows.Remove(entry);
            Apply(entry);
            window.AddHandler(InputElement.KeyDownEvent, (s, e) => OnKey(e), RoutingStrategies.Tunnel);
            window.AddHandler(InputElement.PointerWheelChangedEvent, (s, e) => OnWheel(entry, e), RoutingStrategies.Tunnel);
            window.AddHandler(Gestures.PointerTouchPadGestureMagnifyEvent, (s, e) => { Pinch(e.Delta.X); e.Handled = true; }, RoutingStrategies.Bubble, handledEventsToo: true);
            return entry.Host;
        }

        private static void Apply(Scaled entry)
        {
            double level = Level;
            entry.Host.LayoutTransform = level == 1 ? null : new ScaleTransform(level, level);
            var window = entry.Window;
            // Minimum sizes grow with the zoom, but never beyond the screen.
            var area = Area(window);
            if (entry.MinWidth > 0) window.MinWidth = Math.Min(entry.MinWidth * level, area?.Width ?? double.MaxValue);
            if (entry.MinHeight > 0) window.MinHeight = Math.Min(entry.MinHeight * level, area?.Height ?? double.MaxValue);
            if (entry.KeepsSize)
            {
                // A larger zoom may need a larger window; it never shrinks one the user sized.
                if (window.Width < window.MinWidth) window.Width = window.MinWidth;
                if (window.Height < window.MinHeight) window.Height = window.MinHeight;
                return;
            }
            if (!double.IsNaN(entry.Width) && (window.SizeToContent & SizeToContent.Width) == 0) window.Width = entry.Width * level;
            if (!double.IsNaN(entry.Height) && (window.SizeToContent & SizeToContent.Height) == 0) window.Height = entry.Height * level;
        }

        /// <summary>Sets the zoom level (snapped to the nearest step). Returns false if it did not change.</summary>
        internal static bool Set(double level)
        {
            level = Nearest(level);
            if (level == Level || !Fits(level)) return false;
            Level = level;
            foreach (var entry in windows.ToArray()) Apply(entry);
            if (SettingsFile != null) AppSettings.Write(SettingsFile, level.ToString(CultureInfo.InvariantCulture));
            Changed?.Invoke();
            return true;
        }

        /// <summary>One step larger (+1) or smaller (-1).</summary>
        internal static bool Step(int direction)
        {
            int index = Array.IndexOf(Levels, Level) + Math.Sign(direction);
            return index >= 0 && index < Levels.Length && Set(Levels[index]);
        }

        internal static bool Reset() => Set(1);

        internal static string Percent => Math.Round(Level * 100).ToString(CultureInfo.InvariantCulture) + "%";

        // A zoom the screen cannot hold is refused: the main window's layout would be squeezed well below its minimum size.
        private static bool Fits(double level)
        {
            var main = windows.FirstOrDefault(w => w.KeepsSize);
            var area = main == null ? null : Area(main.Window);
            if (area == null || level <= 1) return true;
            return main.MinWidth * level <= area.Value.Width * 1.12 && main.MinHeight * level <= area.Value.Height * 1.12;
        }

        private static Size? Area(Window window)
        {
            try
            {
                var screen = window.Screens?.ScreenFromWindow(window) ?? window.Screens?.Primary;
                return screen == null ? (Size?)null : screen.WorkingArea.Size.ToSize(screen.Scaling);
            }
            catch (Exception) { return null; }
        }

        private static void OnKey(KeyEventArgs e)
        {
            if (!Shortcut.Pressed(e.KeyModifiers) || (e.KeyModifiers & KeyModifiers.Alt) != 0) return;
            if (e.Key == Key.OemPlus || e.Key == Key.Add) { Step(1); e.Handled = true; }
            else if (e.Key == Key.OemMinus || e.Key == Key.Subtract) { Step(-1); e.Handled = true; }
            else if (e.Key == Key.D0 || e.Key == Key.NumPad0) { Reset(); e.Handled = true; }
        }

        private static double pinched;

        /// <summary>A trackpad pinch (a Mac reports it as a change in magnification): about a quarter more or less is one step.</summary>
        internal static void Pinch(double magnification)
        {
            if (double.IsNaN(magnification)) return;
            if (Math.Sign(magnification) != Math.Sign(pinched)) pinched = 0;
            pinched += magnification;
            if (Math.Abs(pinched) < 0.2) return;
            Step(Math.Sign(pinched));
            pinched = 0;
        }

        // Each notch of a mouse wheel is one step; a touchpad's small scrolls add up to one.
        private static void OnWheel(Scaled entry, PointerWheelEventArgs e)
        {
            if (!Shortcut.Pressed(e.KeyModifiers)) return;
            e.Handled = true;
            entry.Wheel += e.Delta.Y;
            if (Math.Abs(entry.Wheel) < 1) return;
            Step(Math.Sign(entry.Wheel));
            entry.Wheel = 0;
        }
    }
}
