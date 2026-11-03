using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AlbumArtTool.Core
{
    public static class ArtistRules
    {
        private static readonly string[] Separators = { ",", ";", " feat. ", " feat ", " ft. ", " ft ", " featuring ", " with ", " & ", " / " };
        private static readonly HashSet<string> Generic = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "various artists", "various", "va", "v/a", "unknown artist", "unknown" };

        public static bool Specific(string name) => !string.IsNullOrWhiteSpace(name) && !Generic.Contains(name.Trim());

        public static string Suggest(Album album)
        {
            if (Specific(album.TaggedArtist)) return album.TaggedArtist.Trim();
            var first = new string[album.Tracks.Count];
            for (int i = 0; i < first.Length; i++) first[i] = album.Tracks[i].Performers.FirstOrDefault() ?? "";
            if (first.Length == 0 || first.Any(p => !Specific(p))) return "";
            // Prefer an existing complete artist name: commas and ampersands can be part of a band's name.
            foreach (string name in first.OrderBy(p => p.Length).Distinct(StringComparer.OrdinalIgnoreCase))
                if (first.All(p => Leads(p, name))) return name.Trim();
            var candidates = new List<string>();
            foreach (string separator in Separators)
            {
                int index = first[0].IndexOf(separator, StringComparison.OrdinalIgnoreCase);
                if (index > 0) candidates.Add(first[0].Substring(0, index).Trim());
            }
            return candidates.Where(Specific).OrderByDescending(p => p.Length).FirstOrDefault(name => first.All(p => Leads(p, name))) ?? "";
        }

        private static bool Leads(string value, string artist)
        {
            value = value.Trim(); artist = artist.Trim();
            if (string.Equals(value, artist, StringComparison.OrdinalIgnoreCase)) return true;
            if (!value.StartsWith(artist, StringComparison.OrdinalIgnoreCase)) return false;
            string rest = value.Substring(artist.Length);
            foreach (string separator in Separators) if (rest.StartsWith(separator, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static bool Single(string[] values, string artist) => values != null && values.Length == 1 && string.Equals(values[0], artist, StringComparison.Ordinal);

        // Cleanup eligibility is about the track credit the user sees. Missing Album artist
        // tags, capitalization and invisible whitespace alone do not require a rewrite.
        public static bool NeedsChange(Track track, string artist) => NeedsChangeNormalized(track, Normalize(artist));
        internal static bool NeedsChangeNormalized(Track track, string normalizedArtist)
        {
            string current = track.NormalizedSinglePerformer;
            return current == null || current != normalizedArtist;
        }

        // Same result as Regex.Replace(text.Normalize().Trim(), @"\s+", " ").ToUpperInvariant(),
        // without a regex on the hot path.
        public static string Normalize(string text)
        {
            string value = (text ?? "").Normalize().Trim();
            var builder = new StringBuilder(value.Length);
            bool space = false;
            foreach (char c in value)
            {
                if (char.IsWhiteSpace(c)) { if (!space) builder.Append(' '); space = true; }
                else { builder.Append(c); space = false; }
            }
            return builder.ToString().ToUpperInvariant();
        }
    }
}
