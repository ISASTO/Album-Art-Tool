using System;
using System.Diagnostics;

namespace AlbumArtTool.Design
{
    // Optional in-process timing hook used by the Windows performance checks.
    // No stopwatch or reporting work is done when diagnostics are disabled.
    internal static class PaintTrace
    {
        internal static Action<string, double> Recorded;
        internal static Measurement Measure(string name) => new Measurement(name);
        internal readonly struct Measurement : IDisposable
        {
            private readonly string name;
            private readonly Action<string, double> record;
            private readonly long start;
            internal Measurement(string name) { this.name = name; record = Recorded; start = record == null ? 0 : Stopwatch.GetTimestamp(); }
            public void Dispose() { if (record != null) record(name, (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency); }
        }
    }
}
