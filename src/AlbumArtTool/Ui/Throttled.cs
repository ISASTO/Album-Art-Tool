using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace AlbumArtTool.Ui
{
    // Progress reports from background work, delivered at most every 60 ms and below input priority.
    // A plain Progress<T> posts every report (thousands per scan) ahead of clicks and key presses,
    // which can make buttons feel like they need several clicks while work is running.
    internal sealed class Throttled<T> : IProgress<T>
    {
        private static readonly long Interval = Stopwatch.Frequency * 60 / 1000;
        private readonly Action<T> handler;
        private readonly object gate = new object();
        private T latest;
        private bool scheduled;
        private long last;

        internal Throttled(Action<T> handler) { this.handler = handler; }

        public void Report(T value)
        {
            long wait;
            lock (gate)
            {
                latest = value;
                if (scheduled) return;
                scheduled = true;
                wait = Math.Max(0, Interval - (Stopwatch.GetTimestamp() - last));
            }
            if (wait == 0) Dispatcher.UIThread.Post(Deliver, DispatcherPriority.Background);
            else Dispatcher.UIThread.Post(async () => { await Task.Delay(TimeSpan.FromTicks(wait * TimeSpan.TicksPerSecond / Stopwatch.Frequency)); Deliver(); }, DispatcherPriority.Background);
        }

        private void Deliver()
        {
            T value;
            lock (gate) { value = latest; scheduled = false; last = Stopwatch.GetTimestamp(); }
            handler(value);
        }
    }
}
