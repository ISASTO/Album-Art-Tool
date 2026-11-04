using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace AlbumArtTool.Management
{
    // Measures a song's real average audio bitrate: the total size of its audio packets divided by their total duration.
    // Header bitrates (what tag readers report) can be wrong, especially for VBR MP3s without a Xing/VBRI header,
    // where the first frame's bitrate is taken as the whole file's.
    internal static class BitrateProbe
    {
        private static readonly ConcurrentDictionary<string, (long Length, long Modified, int Kbps)> cache =
            new ConcurrentDictionary<string, (long, long, int)>(StringComparer.OrdinalIgnoreCase);

        internal static void ClearCache() => cache.Clear();

        internal static string ProbeName => OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe";

        /// <summary>ffprobe beside the given FFmpeg, in the app folder or on PATH; null when there is none (FFmpeg itself is used then).</summary>
        internal static string FindProbe(string ffmpeg)
        {
            var folders = new[] { ffmpeg == null ? null : Path.GetDirectoryName(ffmpeg), AppContext.BaseDirectory, Encoder.Cache }
                .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator));
            foreach (string folder in folders)
                try
                {
                    if (string.IsNullOrWhiteSpace(folder)) continue;
                    string file = Path.Combine(folder.Trim('"'), ProbeName);
                    if (File.Exists(file)) return Path.GetFullPath(file);
                }
                catch { }
            return null;
        }

        /// <summary>Measured kbps (rounded), cached while the file's size and modification time are unchanged.</summary>
        internal static int Measure(string ffmpeg, string probe, string path, CancellationToken token)
        {
            var info = new FileInfo(path);
            long length = info.Length, modified = info.LastWriteTimeUtc.Ticks;
            if (cache.TryGetValue(path, out var known) && known.Length == length && known.Modified == modified) return known.Kbps;
            (long bytes, double seconds) = probe != null ? WithProbe(probe, path, token) : WithFfmpeg(ffmpeg, path, token);
            if (bytes <= 0 || seconds <= 0.05) throw new IOException("No audio packets could be measured.");
            int kbps = (int)Math.Round(bytes * 8 / seconds / 1000);
            cache[path] = (length, modified, kbps);
            return kbps;
        }

        // ffprobe lists every audio packet as "size,duration_time".
        private static (long, double) WithProbe(string probe, string path, CancellationToken token)
        {
            long bytes = 0; double seconds = 0;
            Encoder.Run(probe, new[] { "-v", "error", "-select_streams", "a:0", "-show_entries", "packet=size,duration_time", "-of", "csv=p=0", path }, token, line =>
            {
                var parts = line.Split(',');
                if (parts.Length < 2) return;
                if (long.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long size)) bytes += size;
                if (double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double duration)) seconds += duration;
            }, timeoutSeconds: 600);
            return (bytes, seconds);
        }

        // FFmpeg's framecrc output lists the same packets: "stream, dts, pts, duration, size, crc" after a "#tb 0: 1/44100" time base line.
        private static (long, double) WithFfmpeg(string ffmpeg, string path, CancellationToken token)
        {
            if (ffmpeg == null) throw new IOException("FFmpeg is not set up.");
            long bytes = 0, ticks = 0; double timebase = 0;
            Encoder.Run(ffmpeg, new[] { "-v", "error", "-nostdin", "-i", path, "-map", "0:a:0", "-c", "copy", "-f", "framecrc", "-" }, token, line =>
            {
                if (line.StartsWith("#tb 0:", StringComparison.Ordinal))
                {
                    var ratio = line.Substring(6).Trim().Split('/');
                    if (ratio.Length == 2 && double.TryParse(ratio[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double num) &&
                        double.TryParse(ratio[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double den) && den > 0) timebase = num / den;
                    return;
                }
                if (line.StartsWith("#", StringComparison.Ordinal)) return;
                var parts = line.Split(',');
                if (parts.Length < 5 || parts[0].Trim() != "0") return;
                if (long.TryParse(parts[3].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long duration)) ticks += duration;
                if (long.TryParse(parts[4].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long size)) bytes += size;
            }, timeoutSeconds: 600);
            return (bytes, ticks * timebase);
        }
    }
}
