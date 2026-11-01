using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using AlbumArtTool.Core;
using AlbumArtTool.Updates;

namespace AlbumArtTool.Management
{
    internal static class Encoder
    {
        internal static string Cache => Path.Combine(BackupSettings.AppData, "Encoder");
        internal static string Find()
        {
            string[] folders = new[] { AppDomain.CurrentDomain.BaseDirectory, Cache }.Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)).ToArray();
            string explicitPath = Environment.GetEnvironmentVariable("ALBUMART_FFMPEG");
            if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath)) return Path.GetFullPath(explicitPath);
            foreach (string folder in folders)
                try { string file = Path.Combine(folder.Trim('"'), "ffmpeg.exe"); if (File.Exists(file)) return Path.GetFullPath(file); } catch { }
            return null;
        }
        internal static string Run(string exe, string[] args, CancellationToken token, Action<string> line = null, int timeoutSeconds = 7200)
        {
            var output = new StringBuilder(); var error = new StringBuilder();
            var info = new ProcessStartInfo(exe, string.Join(" ", args.Select(UpdateInstaller.QuoteArgument)))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var process = new Process { StartInfo = info })
            {
                process.OutputDataReceived += (s, e) => { if (e.Data != null) { lock (output) if (output.Length < 65536) output.AppendLine(e.Data); line?.Invoke(e.Data); } };
                process.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (error) if (error.Length < 65536) error.AppendLine(e.Data); };
                process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine(); var clock = Stopwatch.StartNew();
                try
                {
                    while (!process.WaitForExit(100))
                    { token.ThrowIfCancellationRequested(); if (clock.Elapsed.TotalSeconds > timeoutSeconds) throw new IOException("FFmpeg timed out. The original file was kept."); }
                    process.WaitForExit(); token.ThrowIfCancellationRequested();
                    if (process.ExitCode != 0) throw new IOException("FFmpeg could not process this file. " + error.ToString().Trim());
                    return output.ToString();
                }
                catch { try { if (!process.HasExited) { process.Kill(); process.WaitForExit(5000); } } catch { } throw; }
            }
        }
        internal static void Validate(string exe, CancellationToken token)
        { if (!Run(exe, new[] { "-version" }, token, timeoutSeconds: 20).Contains("ffmpeg version")) throw new IOException("That executable did not identify itself as FFmpeg."); }
        internal static string AudioHash(string exe, Track track, CancellationToken token)
        {
            string output = Run(exe, new[] { "-v", "error", "-nostdin", "-i", track.Path, "-map", "0:a:0", "-c:a", "copy", "-f", "hash", "-hash", "sha256", "-" }, token);
            string hash = output.Split('\n').FirstOrDefault(l => l.StartsWith("SHA256=", StringComparison.OrdinalIgnoreCase));
            if (hash == null) throw new IOException("FFmpeg did not return an audio hash.");
            return (track.Codec ?? Path.GetExtension(track.Path)) + ":" + track.SampleRate + ":" + track.Channels + ":" + hash.Trim();
        }
        internal static void Convert(string exe, Track track, string temp, CancellationToken token, Action<double> progress)
        {
            var args = new System.Collections.Generic.List<string> { "-v", "error", "-nostdin", "-i", track.Path, "-map", "0:a:0", "-vn", "-map_metadata", "0", "-c:a", "libmp3lame", "-b:a", "320k", "-id3v2_version", "3", "-write_id3v1", "1" };
            if (track.SampleRate > 48000) args.AddRange(new[] { "-ar", "48000" });
            if (track.Channels > 2) args.AddRange(new[] { "-ac", "2" });
            args.AddRange(new[] { "-progress", "pipe:1", "-nostats", "-y", temp });
            Run(exe, args.ToArray(), token, line =>
            {
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal) && long.TryParse(line.Substring(12), out long microseconds) && track.Duration.TotalSeconds > 0)
                    progress?.Invoke(Math.Min(1, microseconds / 1000000.0 / track.Duration.TotalSeconds));
            });
            using (var original = TagLib.File.Create(track.Path, TagLib.ReadStyle.None))
            using (var result = TagLib.File.Create(temp))
            {
                original.Tag.CopyTo(result.Tag, true);
                result.Tag.Pictures = original.Tag.Pictures;
                result.Save();
            }
            using (var check = TagLib.File.Create(temp))
            {
                if (check.Properties.AudioBitrate < 318 || check.Properties.AudioBitrate > 322 ||
                    Math.Abs((check.Properties.Duration - track.Duration).TotalSeconds) > Math.Max(2, track.Duration.TotalSeconds * .01))
                    throw new IOException("The new MP3 did not pass bitrate/duration verification. Original kept.");
            }
            Run(exe, new[] { "-v", "error", "-xerror", "-nostdin", "-i", temp, "-map", "0:a:0", "-f", "null", "-" }, token);
        }
    }
}
