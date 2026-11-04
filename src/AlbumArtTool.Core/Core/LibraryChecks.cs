using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace AlbumArtTool.Core
{
    public sealed class LibraryCheckResult
    {
        /// <summary>Songs without a Title tag (the app shows their filename instead).</summary>
        public List<Track> MissingTitles { get; } = new List<Track>();
        /// <summary>Songs with two or more embedded pictures; those with several covers come first.</summary>
        public List<Track> MultiplePictures { get; } = new List<Track>();
        /// <summary>Lossy songs below the chosen bitrate, lowest first.</summary>
        public List<Track> LowBitrate { get; } = new List<Track>();
        public int LowBitrateLimit { get; set; }
        public int TracksChecked { get; set; }
        /// <summary>Measured kbps by path, for low-bitrate candidates that were verified.</summary>
        public Dictionary<string, int> Measured { get; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Measured lengths by path (seconds), alongside Measured.</summary>
        public Dictionary<string, double> MeasuredSeconds { get; } = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Songs whose header bitrate looked low but whose measured bitrate is at or above the limit.</summary>
        public List<Track> HeaderWrong { get; } = new List<Track>();
        /// <summary>Songs that could not be measured (kept in LowBitrate with their header bitrate).</summary>
        public List<string> MeasureIssues { get; } = new List<string>();
        public bool Verified { get; set; }

        /// <summary>The measured bitrate when verified, otherwise the header bitrate.</summary>
        public int Bitrate(Track track) => Measured.TryGetValue(track.Path, out int kbps) ? kbps : track.Bitrate;
        /// <summary>The measured length when known, otherwise the header's.</summary>
        public TimeSpan Duration(Track track) => MeasuredSeconds.TryGetValue(track.Path, out double seconds) ? TimeSpan.FromSeconds(seconds) : track.Duration;

        /// <summary>Low-bitrate songs grouped by album (folder + album tag), lowest bitrate first.</summary>
        public List<LowBitrateAlbum> LowBitrateAlbums() =>
            LowBitrate.GroupBy(t => System.IO.Path.GetDirectoryName(t.Path) + "\u001f" + (t.AlbumTitle ?? ""), StringComparer.OrdinalIgnoreCase)
                .Select(g => new LowBitrateAlbum(g.First().AlbumTitle, g.Select(t => t.ArtistText).FirstOrDefault(a => !string.IsNullOrWhiteSpace(a)) ?? "",
                    System.IO.Path.GetDirectoryName(g.First().Path), g.OrderBy(t => t.Number).ThenBy(t => t.Path, StringComparer.CurrentCultureIgnoreCase).ToList(), this))
                .OrderBy(a => a.MinKbps).ThenBy(a => a.Artist, StringComparer.CurrentCultureIgnoreCase).ThenBy(a => a.Album, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public sealed class LowBitrateAlbum
    {
        public string Album { get; }
        public string Artist { get; }
        public string Folder { get; }
        public List<Track> Tracks { get; }
        public int MinKbps { get; }
        public int MaxKbps { get; }
        internal LowBitrateAlbum(string album, string artist, string folder, List<Track> tracks, LibraryCheckResult result)
        {
            Album = string.IsNullOrWhiteSpace(album) ? System.IO.Path.GetFileName(folder) : album; Artist = artist; Folder = folder; Tracks = tracks;
            MinKbps = tracks.Min(result.Bitrate); MaxKbps = tracks.Max(result.Bitrate);
        }
        public string Range => MinKbps == MaxKbps ? MinKbps + " kbps" : MinKbps + "–" + MaxKbps + " kbps";
    }

    // Read-only checks over a library scan. Everything comes from tags the scan already read,
    // so the checks are instant and never open or change a music file.
    public static class LibraryChecks
    {
        /// <summary>"MP3", "AAC" etc. from the file extension (more recognizable than codec descriptions).</summary>
        public static string Format(Track track)
        {
            string extension = System.IO.Path.GetExtension(track.Path).TrimStart('.').ToUpperInvariant();
            return extension == "M4A" || extension == "M4B" || extension == "MP4" ? "AAC/M4A" : extension;
        }

        // A leading track number such as "01 - ", "1. " or "07 -" is not part of the song's name.
        private static readonly System.Text.RegularExpressions.Regex LeadingNumber =
            new System.Text.RegularExpressions.Regex(@"^\s*\d{1,3}(?:\s*-\s+|\s+-\s*|\.\s+)(?=\S)");

        /// <summary>The Title a song without one would get from its filename: the name without extension or a leading track number.</summary>
        public static string SuggestedTitle(string path)
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(path) ?? "";
            return LeadingNumber.Replace(name, "", 1).Trim();
        }

        public const int DefaultLowBitrate = 192;
        public static readonly int[] BitrateChoices = { 96, 128, 160, 192, 256, 320 };

        // Lossless files are never "low bitrate": their bitrate reflects compression, not quality.
        // Unknown bitrates (0) are not reported.
        public static bool IsLowBitrate(Track track, int limit) => !track.Lossless && track.Bitrate > 0 && track.Bitrate < limit;

        public static LibraryCheckResult Run(IEnumerable<Album> albums, int lowBitrate = DefaultLowBitrate)
        {
            var result = new LibraryCheckResult { LowBitrateLimit = lowBitrate };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var track in albums.SelectMany(a => a.Tracks))
            {
                if (!seen.Add(track.Path)) continue;
                result.TracksChecked++;
                if (!track.HasTitleTag) result.MissingTitles.Add(track);
                if (track.PictureCount > 1) result.MultiplePictures.Add(track);
                if (IsLowBitrate(track, lowBitrate)) result.LowBitrate.Add(track);
            }
            result.MissingTitles.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Path, b.Path));
            var ordered = result.MultiplePictures.OrderByDescending(t => t.CoverPictures > 1).ThenByDescending(t => t.PictureCount)
                .ThenBy(t => t.Path, StringComparer.CurrentCultureIgnoreCase).ToList();
            result.MultiplePictures.Clear(); result.MultiplePictures.AddRange(ordered);
            ordered = result.LowBitrate.OrderBy(t => t.Bitrate).ThenBy(t => t.Path, StringComparer.CurrentCultureIgnoreCase).ToList();
            result.LowBitrate.Clear(); result.LowBitrate.AddRange(ordered);
            return result;
        }

        public sealed class Measurements
        {
            public bool Available;
            public Dictionary<string, int> Kbps = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, double> Seconds = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            public List<string> Issues = new List<string>();
        }

        /// <summary>
        /// Measures the real bitrate of each song with ffprobe (or FFmpeg, which lists the same audio packets).
        /// Safe to run in the background: it only reads the given tracks. Available is false when neither tool is set up.
        /// </summary>
        public static Measurements Measure(IReadOnlyList<Track> candidates, CancellationToken token, IProgress<ProgressInfo> progress = null)
        {
            var result = new Measurements();
            string ffmpeg = Management.Encoder.Find(), probe = Management.BitrateProbe.FindProbe(ffmpeg);
            if (ffmpeg == null && probe == null) return result;
            result.Available = true;
            var measured = new System.Collections.Concurrent.ConcurrentDictionary<string, (int Kbps, double Seconds)>(StringComparer.OrdinalIgnoreCase);
            var issues = new System.Collections.Concurrent.ConcurrentBag<string>();
            int done = 0;
            System.Threading.Tasks.Parallel.ForEach(candidates, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Math.Min(4, Environment.ProcessorCount)), CancellationToken = token }, track =>
            {
                try { measured[track.Path] = Management.BitrateProbe.MeasureFull(ffmpeg, probe, track.Path, token); }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) when (e is System.IO.IOException || e is UnauthorizedAccessException || e is System.ComponentModel.Win32Exception) { issues.Add(track.Path + ": " + e.Message); }
                int n = Interlocked.Increment(ref done);
                progress?.Report(new ProgressInfo { Completed = n, Total = candidates.Count, Message = "Measuring real bitrates · " + n + "/" + candidates.Count });
            });
            token.ThrowIfCancellationRequested();
            foreach (var pair in measured) { result.Kbps[pair.Key] = pair.Value.Kbps; result.Seconds[pair.Key] = pair.Value.Seconds; }
            result.Issues.AddRange(issues.OrderBy(i => i, StringComparer.OrdinalIgnoreCase));
            return result;
        }

        /// <summary>Uses measured bitrates: songs that really are at or above the limit leave the low-bitrate list.</summary>
        public static void Apply(LibraryCheckResult result, Measurements measured)
        {
            if (!measured.Available) return;
            foreach (var pair in measured.Kbps) result.Measured[pair.Key] = pair.Value;
            foreach (var pair in measured.Seconds) result.MeasuredSeconds[pair.Key] = pair.Value;
            result.MeasureIssues.Clear(); result.MeasureIssues.AddRange(measured.Issues);
            var candidates = result.LowBitrate.ToArray();
            result.HeaderWrong.Clear();
            result.HeaderWrong.AddRange(candidates.Where(t => result.Measured.TryGetValue(t.Path, out int kbps) && kbps >= result.LowBitrateLimit));
            var keep = candidates.Where(t => !result.HeaderWrong.Contains(t)).OrderBy(result.Bitrate).ThenBy(t => t.Path, StringComparer.CurrentCultureIgnoreCase).ToList();
            result.LowBitrate.Clear(); result.LowBitrate.AddRange(keep);
            result.Verified = true;
        }

        /// <summary>Measure and Apply in one step.</summary>
        public static void Verify(LibraryCheckResult result, CancellationToken token, IProgress<ProgressInfo> progress = null) =>
            Apply(result, Measure(result.LowBitrate.ToArray(), token, progress));

        public static string Csv(LibraryCheckResult result)
        {
            var text = new StringBuilder("Check,Artist,Album,Title,Embedded pictures,Bitrate (kbps),Format,Path\r\n");
            string Escape(string value)
            {
                value = value ?? "";
                // Spreadsheet formula injection guard.
                if (value.Length > 0 && "=+-@\t\r".IndexOf(value[0]) >= 0) value = "'" + value;
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            }
            void Row(string check, Track t) => text.Append(string.Join(",", new[] { check, t.ArtistText, t.AlbumTitle, t.HasTitleTag ? t.Title : "", t.PictureSummary, result.Bitrate(t) > 0 ? result.Bitrate(t).ToString() : "", Format(t), t.Path }.Select(Escape))).Append("\r\n");
            foreach (var track in result.MissingTitles) Row("Missing Title tag", track);
            foreach (var track in result.MultiplePictures) Row(track.CoverPictures > 1 ? "Several embedded covers" : "Several embedded pictures", track);
            foreach (var track in result.LowBitrate) Row("Below " + result.LowBitrateLimit + " kbps" + (result.Measured.ContainsKey(track.Path) ? " (measured)" : " (header)"), track);
            return text.ToString();
        }
    }
}
