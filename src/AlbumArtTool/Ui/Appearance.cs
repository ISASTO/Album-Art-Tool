using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using AlbumArtTool.Core;

namespace AlbumArtTool.Ui
{
    // Light/dark appearance. Every brush is a theme resource, so switching only changes the
    // requested theme variant: no control is recolored by hand and nothing is repainted twice.
    internal static partial class Appearance
    {
        internal static event Action Changed;
        internal static bool IsDark => Application.Current?.ActualThemeVariant != ThemeVariant.Light;
        internal static TimeSpan WipeDuration { get; } = TimeSpan.FromMilliseconds(320);
        internal static int TransitionsRunning { get; private set; }

        internal static ThemeMode Mode { get; private set; } = ThemeMode.System;

        internal static void Initialize(Application application)
        {
            Mode = AppSettings.ReadTheme();
            application.RequestedThemeVariant = Variant(Mode);
            // "Match Windows" follows the system setting live, while the app is open.
            application.ActualThemeVariantChanged += (s, e) =>
            {
                foreach (var window in OpenWindows()) ApplyNativeFrame(window);
                Changed?.Invoke();
            };
        }

        private static ThemeVariant Variant(ThemeMode mode) => mode == ThemeMode.Dark ? ThemeVariant.Dark : mode == ThemeMode.Light ? ThemeVariant.Light : ThemeVariant.Default;

        /// <summary>Switches appearance. Returns false only if the preference could not be saved.</summary>
        internal static bool SetDark(bool dark, bool persist = true, bool animate = false) => SetMode(dark ? ThemeMode.Dark : ThemeMode.Light, persist, animate);

        /// <summary>Chooses Match Windows, Light or Dark. Returns false only if the preference could not be saved.</summary>
        internal static bool SetMode(ThemeMode mode, bool persist = true, bool animate = false)
        {
            var application = Application.Current;
            bool wasDark = IsDark;
            if (application != null && (mode != Mode || application.RequestedThemeVariant != Variant(mode)))
            {
                var wipes = new List<Wipe>();
                if (animate && MotionAllowed)
                    foreach (var window in OpenWindows())
                        try { var wipe = Wipe.Capture(window); if (wipe != null) wipes.Add(wipe); }
                        catch (Exception) { /* No snapshot (e.g. no GPU surface yet): switch without the wipe. */ }
                Mode = mode;
                application.RequestedThemeVariant = Variant(mode);
                bool changed = wasDark != IsDark;
                foreach (var window in OpenWindows()) ApplyNativeFrame(window);
                Changed?.Invoke();
                // Match Windows may resolve to the appearance already showing: then there is nothing to sweep.
                foreach (var wipe in wipes) { if (changed) wipe.Start(); else wipe.Cancel(); }
            }
            Mode = mode;
            return !persist || AppSettings.SaveTheme(mode);
        }

        private static readonly List<Window> windows = new List<Window>();
        internal static void Track(Window window)
        {
            windows.Add(window);
            window.Closed += (s, e) => windows.Remove(window);
        }
        internal static IEnumerable<Window> OpenWindows() => windows.Where(w => w.IsVisible).ToArray();

        internal static IBrush Brush(string key)
        {
            var application = Application.Current;
            if (application != null && application.TryGetResource(key, application.ActualThemeVariant, out object value) && value is IBrush brush) return brush;
            return Brushes.Transparent;
        }

        // A soft diagonal sweep from a snapshot of the previous appearance. The snapshot is a
        // single GPU-composited image above the live window, which repaints underneath it once.
        // The mask's gradient stops stay fixed (a smoothstep feather) and the gradient itself
        // moves across the window, so the edge stays equally soft from start to finish.
        private sealed class Wipe
        {
            private const int FeatherStops = 12;
            private static readonly double Angle = 18 * Math.PI / 180;
            private readonly Window window;
            private readonly Image overlay;
            private readonly RenderTargetBitmap snapshot;
            private readonly LinearGradientBrush mask;
            private readonly double width, height, feather, travel;
            private readonly Stopwatch clock = new Stopwatch();
            private DispatcherTimer timer;

            private Wipe(Window window, Image overlay, RenderTargetBitmap snapshot, LinearGradientBrush mask)
            {
                this.window = window; this.overlay = overlay; this.snapshot = snapshot; this.mask = mask;
                width = window.Bounds.Width; height = window.Bounds.Height;
                // Distance the edge covers along the sweep direction, corner to corner.
                travel = width * Math.Cos(Angle) + height * Math.Sin(Angle);
                feather = Math.Max(240, travel * 0.38);
                Place(-feather);
            }

            internal static Wipe Capture(Window window)
            {
                var layer = OverlayLayer.GetOverlayLayer(window);
                if (layer == null || window.Bounds.Width < 2 || window.Bounds.Height < 2) return null;
                double scale = window.RenderScaling;
                var size = new PixelSize(Math.Max(1, (int)Math.Ceiling(window.Bounds.Width * scale)), Math.Max(1, (int)Math.Ceiling(window.Bounds.Height * scale)));
                var snapshot = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
                snapshot.Render(window);
                var mask = new LinearGradientBrush { SpreadMethod = GradientSpreadMethod.Pad };
                for (int i = 0; i < FeatherStops; i++)
                {
                    double t = i / (double)(FeatherStops - 1), alpha = t * t * (3 - 2 * t);
                    mask.GradientStops.Add(new GradientStop(Color.FromArgb((byte)Math.Round(alpha * 255), 0, 0, 0), t));
                }
                var overlay = new Image { Source = snapshot, Stretch = Stretch.Fill, Width = window.Bounds.Width, Height = window.Bounds.Height, IsHitTestVisible = false, OpacityMask = mask };
                layer.Children.Add(overlay);
                TransitionsRunning++;
                return new Wipe(window, overlay, snapshot, mask);
            }

            internal void Start()
            {
                clock.Start();
                timer = new DispatcherTimer(TimeSpan.FromMilliseconds(8), DispatcherPriority.Render, (s, e) => Frame());
                timer.Start();
            }

            // The new appearance is revealed behind the feather's leading edge.
            private void Place(double edge)
            {
                double cos = Math.Cos(Angle), sin = Math.Sin(Angle);
                mask.StartPoint = new RelativePoint(edge * cos, edge * sin, RelativeUnit.Absolute);
                mask.EndPoint = new RelativePoint((edge + feather) * cos, (edge + feather) * sin, RelativeUnit.Absolute);
            }

            private void Frame()
            {
                double progress = Math.Min(1, clock.Elapsed.TotalMilliseconds / WipeDuration.TotalMilliseconds);
                double eased = progress < .5 ? 4 * progress * progress * progress : 1 - Math.Pow(-2 * progress + 2, 3) / 2;
                Place(-feather + eased * (travel + feather));
                // Fade whatever remains in the far corner so the sweep never ends on a visible line.
                double tail = Math.Clamp((progress - .7) / .3, 0, 1);
                overlay.Opacity = 1 - tail * tail * (3 - 2 * tail);
                overlay.InvalidateVisual();
                if (progress < 1 && window.IsVisible) return;
                Finish();
            }

            internal void Cancel() => Finish();

            private void Finish()
            {
                timer?.Stop();
                OverlayLayer.GetOverlayLayer(window)?.Children.Remove(overlay);
                overlay.Source = null; snapshot.Dispose();
                TransitionsRunning--;
            }
        }

        internal static void ApplyNativeFrame(Window window)
        {
            if (!OperatingSystem.IsWindows()) return;
            try
            {
                var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                if (handle == IntPtr.Zero) return;
                var canvas = ((ISolidColorBrush)Brush("Canvas")).Color; var ink = ((ISolidColorBrush)Brush("Ink")).Color;
                int dark = IsDark ? 1 : 0, rounded = 2, caption = canvas.R | canvas.G << 8 | canvas.B << 16, text = ink.R | ink.G << 8 | ink.B << 16;
                // Documented Windows 11 DWM attributes. Earlier Windows versions simply reject them.
                DwmSetWindowAttribute(handle, 20, ref dark, 4);
                DwmSetWindowAttribute(handle, 33, ref rounded, 4);
                DwmSetWindowAttribute(handle, 35, ref caption, 4);
                DwmSetWindowAttribute(handle, 36, ref text, 4);
            }
            catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException || e is InvalidCastException) { }
        }

        // Windows "Show animations in Windows" (reduced motion) is respected.
        internal static bool MotionAllowed
        {
            get
            {
                if (!OperatingSystem.IsWindows()) return true;
                try { int enabled = 1; return SystemParametersInfo(0x1042, 0, ref enabled, 0) == 0 || enabled != 0; }
                catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException) { return true; }
            }
        }

        [LibraryImport("dwmapi.dll")]
        private static partial int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
        private static partial int SystemParametersInfo(uint action, uint parameter, ref int value, uint winIni);
    }
}
