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
        public int TracksChecked { get; set; }
    }

    // Read-only checks over a library scan. Everything comes from tags the scan already read,
    // so the checks are instant and never open or change a music file.
    public static class LibraryChecks
    {
        public static LibraryCheckResult Run(IEnumerable<Album> albums)
        {
            var result = new LibraryCheckResult();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var track in albums.SelectMany(a => a.Tracks))
            {
                if (!seen.Add(track.Path)) continue;
                result.TracksChecked++;
                if (!track.HasTitleTag) result.MissingTitles.Add(track);
                if (track.PictureCount > 1) result.MultiplePictures.Add(track);
            }
            result.MissingTitles.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Path, b.Path));
            var ordered = result.MultiplePictures.OrderByDescending(t => t.CoverPictures > 1).ThenByDescending(t => t.PictureCount)
                .ThenBy(t => t.Path, StringComparer.CurrentCultureIgnoreCase).ToList();
            result.MultiplePictures.Clear(); result.MultiplePictures.AddRange(ordered);
            return result;
        }

        public static string Csv(LibraryCheckResult result)
        {
            var text = new StringBuilder("Check,Artist,Album,Title,Embedded pictures,Path\r\n");
            string Escape(string value)
            {
                value = value ?? "";
                // Spreadsheet formula injection guard.
                if (value.Length > 0 && "=+-@\t\r".IndexOf(value[0]) >= 0) value = "'" + value;
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            }
            void Row(string check, Track t) => text.Append(string.Join(",", new[] { check, t.ArtistText, t.AlbumTitle, t.HasTitleTag ? t.Title : "", t.PictureSummary, t.Path }.Select(Escape))).Append("\r\n");
            foreach (var track in result.MissingTitles) Row("Missing Title tag", track);
            foreach (var track in result.MultiplePictures) Row(track.CoverPictures > 1 ? "Several embedded covers" : "Several embedded pictures", track);
            return text.ToString();
        }
    }
}
