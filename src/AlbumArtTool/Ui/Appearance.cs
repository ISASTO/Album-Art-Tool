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
        internal static TimeSpan WipeDuration { get; } = TimeSpan.FromMilliseconds(180);
        internal static int TransitionsRunning { get; private set; }

        internal static void Initialize(Application application) =>
            application.RequestedThemeVariant = AppSettings.ReadDark() ? ThemeVariant.Dark : ThemeVariant.Light;

        /// <summary>Switches appearance. Returns false only if the preference could not be saved.</summary>
        internal static bool SetDark(bool dark, bool persist = true, bool animate = false)
        {
            var application = Application.Current;
            if (application != null && dark != IsDark)
            {
                var wipes = new List<Wipe>();
                if (animate && MotionAllowed)
                    foreach (var window in OpenWindows())
                        try { var wipe = Wipe.Capture(window); if (wipe != null) wipes.Add(wipe); }
                        catch (Exception) { /* No snapshot (e.g. no GPU surface yet): switch without the wipe. */ }
                application.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
                foreach (var window in OpenWindows()) ApplyNativeFrame(window);
                Changed?.Invoke();
                foreach (var wipe in wipes) wipe.Start();
            }
            return !persist || AppSettings.SaveDark(dark);
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

        // A short gradient wipe from a snapshot of the previous appearance. The snapshot is a
        // single GPU-composited image above the live window, which repaints underneath it once.
        private sealed class Wipe
        {
            private readonly Window window;
            private readonly Image overlay;
            private readonly RenderTargetBitmap snapshot;
            private readonly GradientStop clearEdge, solidEdge;
            private readonly Stopwatch clock = new Stopwatch();
            private DispatcherTimer timer;

            private Wipe(Window window, Image overlay, RenderTargetBitmap snapshot, GradientStop clearEdge, GradientStop solidEdge)
            { this.window = window; this.overlay = overlay; this.snapshot = snapshot; this.clearEdge = clearEdge; this.solidEdge = solidEdge; }

            internal static Wipe Capture(Window window)
            {
                var layer = OverlayLayer.GetOverlayLayer(window);
                if (layer == null || window.Bounds.Width < 2 || window.Bounds.Height < 2) return null;
                double scale = window.RenderScaling;
                var size = new PixelSize(Math.Max(1, (int)Math.Ceiling(window.Bounds.Width * scale)), Math.Max(1, (int)Math.Ceiling(window.Bounds.Height * scale)));
                var snapshot = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
                snapshot.Render(window);
                var clearEdge = new GradientStop(Colors.Transparent, 0);
                var solidEdge = new GradientStop(Colors.Black, 0.08);
                var mask = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0.35, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(Colors.Transparent, 0), clearEdge, solidEdge, new GradientStop(Colors.Black, 1) }
                };
                var overlay = new Image { Source = snapshot, Stretch = Stretch.Fill, Width = window.Bounds.Width, Height = window.Bounds.Height, IsHitTestVisible = false, OpacityMask = mask };
                layer.Children.Add(overlay);
                TransitionsRunning++;
                return new Wipe(window, overlay, snapshot, clearEdge, solidEdge);
            }

            internal void Start()
            {
                clock.Start();
                timer = new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Render, (s, e) => Frame());
                timer.Start();
            }

            private void Frame()
            {
                double progress = Math.Min(1, clock.Elapsed.TotalMilliseconds / WipeDuration.TotalMilliseconds);
                double eased = 1 - Math.Pow(1 - progress, 3), edge = -0.08 + eased * 1.16;
                clearEdge.Offset = Math.Clamp(edge, 0, 1); solidEdge.Offset = Math.Clamp(edge + 0.08, 0, 1);
                overlay.InvalidateVisual();
                if (progress < 1 && window.IsVisible) return;
                Finish();
            }

            private void Finish()
            {
                timer.Stop();
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
