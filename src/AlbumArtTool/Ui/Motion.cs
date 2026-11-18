using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace AlbumArtTool.Ui
{
    // Motion behaves like a well-made switch: it answers at once, travels a short distance and settles.
    // Only opacity and transforms move; every tween can be interrupted by the next one on the same target;
    // with Windows "Show animations" off, movement becomes a short fade and the latch does nothing.
    internal static class Motion
    {
        /// <summary>Set false (tests, reduced motion) to finish every animation at once.</summary>
        internal static bool Enabled = true;
        internal static bool Allowed => Enabled && Appearance.MotionAllowed;

        // Timing tokens (ms) and curves (CSS cubic-bezier values).
        internal const double PressMs = 70, ReleaseMs = 160, SettleMs = 180, FadeMs = 300;
        internal static readonly Func<double, double> Press = Bezier(0.4, 0, 1, 1), Release = Bezier(0.34, 1.4, 0.64, 1), Settle = Bezier(0.2, 0.8, 0.2, 1);

        private sealed class Tween
        {
            internal double Duration;
            internal Func<double, double> Curve;
            internal Action<double> Apply;
            internal Action Done;
            internal Stopwatch Clock;
        }

        private static readonly Dictionary<object, Tween> running = new Dictionary<object, Tween>();
        private static DispatcherTimer ticker;

        /// <summary>Runs <paramref name="apply"/> from 0 to 1 over the duration; a new tween on the same key replaces the old one.</summary>
        internal static void Run(object key, double duration, Func<double, double> curve, Action<double> apply, Action done = null)
        {
            if (!Allowed || duration <= 0) { apply(1); done?.Invoke(); running.Remove(key); return; }
            running[key] = new Tween { Duration = duration, Curve = curve, Apply = apply, Done = done, Clock = Stopwatch.StartNew() };
            apply(curve(0));
            ticker ??= new DispatcherTimer(TimeSpan.FromMilliseconds(8), DispatcherPriority.Render, (s, e) => Tick());
            if (!ticker.IsEnabled) ticker.Start();
        }

        internal static void Stop(object key) { if (running.TryGetValue(key, out var t)) { running.Remove(key); t.Apply(1); t.Done?.Invoke(); } }

        private static void Tick()
        {
            if (running.Count == 0) { ticker.Stop(); return; }
            foreach (var pair in new List<KeyValuePair<object, Tween>>(running))
            {
                var t = pair.Value;
                double progress = Math.Min(1, t.Clock.Elapsed.TotalMilliseconds / t.Duration);
                t.Apply(t.Curve(progress));
                if (progress < 1) continue;
                if (running.TryGetValue(pair.Key, out var current) && ReferenceEquals(current, t)) running.Remove(pair.Key);
                t.Done?.Invoke();
            }
        }

        private static TranslateTransform Translate(Visual visual)
        {
            if (visual.RenderTransform is TranslateTransform existing) return existing;
            var transform = new TranslateTransform();
            visual.RenderTransform = transform;
            return transform;
        }

        /// <summary>The latch: the item sinks about 2 px (press), then springs back up with a hint of overshoot and rests pressed in.</summary>
        internal static void Latch(Visual visual, bool keyboard = false)
        {
            if (visual == null) return;
            var move = Translate(visual);
            if (!Allowed) { move.Y = 0; return; }
            if (keyboard) { Run(visual, ReleaseMs, Release, t => move.Y = 1.2 * (1 - t)); return; }
            Run(visual, PressMs, Press, t => move.Y = 2 * t, () => Run(visual, ReleaseMs, Release, t => move.Y = 2 * (1 - t)));
        }

        /// <summary>New content arriving: it rises a few pixels and fades in.</summary>
        internal static void Arrive(Visual visual, double rise = 6)
        {
            if (visual == null) return;
            var move = Translate(visual);
            if (!Allowed) { move.Y = 0; visual.Opacity = 1; return; }
            Run(visual, SettleMs, Settle, t => { move.Y = rise * (1 - t); visual.Opacity = Math.Min(1, t * 1.25); }, () => { move.Y = 0; visual.Opacity = 1; });
        }

        /// <summary>A staged cover dropping into its frame: from 104% to 100% with a fade.</summary>
        internal static void Drop(Visual visual)
        {
            if (visual == null) return;
            if (!Allowed) { visual.RenderTransform = null; visual.Opacity = 1; return; }
            var scale = new ScaleTransform(1.04, 1.04); visual.RenderTransform = scale; visual.RenderTransformOrigin = RelativePoint.Center;
            Run(visual, SettleMs, Settle, t => { double s = 1.04 - 0.04 * t; scale.ScaleX = scale.ScaleY = s; visual.Opacity = 0.35 + 0.65 * t; }, () => { visual.RenderTransform = null; visual.Opacity = 1; });
        }

        /// <summary>Fades a control's opacity to a value (glows lighting up, the album wash).</summary>
        internal static void Fade(Visual visual, double to, double duration = FadeMs)
        {
            if (visual == null) return;
            double from = visual.Opacity;
            Run(visual, duration, Settle, t => visual.Opacity = from + (to - from) * t);
        }

        /// <summary>Counts a number up or down to its new value (display readouts).</summary>
        internal static void Count(TextBlock text, string value, double duration = 300)
        {
            if (!int.TryParse(text.Text, out int from) || !int.TryParse(value, out int to) || from == to || !text.IsEffectivelyVisible)
            { Stop(text); text.Text = value; return; }
            Run(text, duration, Settle, t => text.Text = ((int)Math.Round(from + (to - from) * t)).ToString(), () => text.Text = value);
        }

        // CSS cubic-bezier(x1, y1, x2, y2): solve x(t) = progress for t, return y(t).
        internal static Func<double, double> Bezier(double x1, double y1, double x2, double y2)
        {
            double Coord(double t, double a, double b) => 3 * a * (1 - t) * (1 - t) * t + 3 * b * (1 - t) * t * t + t * t * t;
            return x =>
            {
                if (x <= 0) return 0; if (x >= 1) return 1;
                double lo = 0, hi = 1, t = x;
                for (int i = 0; i < 24; i++)
                {
                    double cx = Coord(t, x1, x2);
                    if (Math.Abs(cx - x) < 1e-5) break;
                    if (cx < x) lo = t; else hi = t;
                    t = (lo + hi) / 2;
                }
                return Coord(t, y1, y2);
            };
        }
    }
}
