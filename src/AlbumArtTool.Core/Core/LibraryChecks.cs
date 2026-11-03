using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

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
            void Row(string check, Track t) => text.Append(string.Join(",", new[] { check, t.ArtistText, t.AlbumTitle, t.HasTitleTag ? t.Title : "", t.PictureSummary, t.Bitrate > 0 ? t.Bitrate.ToString() : "", Format(t), t.Path }.Select(Escape))).Append("\r\n");
            foreach (var track in result.MissingTitles) Row("Missing Title tag", track);
            foreach (var track in result.MultiplePictures) Row(track.CoverPictures > 1 ? "Several embedded covers" : "Several embedded pictures", track);
            foreach (var track in result.LowBitrate) Row("Below " + result.LowBitrateLimit + " kbps", track);
            return text.ToString();
        }
    }
}
