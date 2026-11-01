using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AlbumArtTool.Core
{
    public static class ArtistRules
    {
        private static readonly string[] Separators = { ",", ";", " feat. ", " feat ", " ft. ", " ft ", " featuring ", " with ", " & ", " / " };
        public static bool Specific(string name) => !string.IsNullOrWhiteSpace(name) &&
            !new[] { "various artists", "various", "va", "v/a", "unknown artist", "unknown" }.Contains(name.Trim(), StringComparer.OrdinalIgnoreCase);

        public static string Suggest(Album album)
        {
            if (Specific(album.TaggedArtist)) return album.TaggedArtist.Trim();
            var first = album.Tracks.Select(t => t.Performers.FirstOrDefault() ?? "").ToArray();
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
            return Separators.Any(s => rest.StartsWith(s, StringComparison.OrdinalIgnoreCase));
        }
        public static bool Single(string[] values, string artist) => values != null && values.Length == 1 && string.Equals(values[0], artist, StringComparison.Ordinal);
        // Cleanup eligibility is about the track credit the user sees. Missing Album artist
        // tags, capitalization and invisible whitespace alone do not require a rewrite.
        public static bool NeedsChange(Track track, string artist) => track.Performers == null || track.Performers.Length != 1 || Normalize(track.Performers[0]) != Normalize(artist);
        public static string Normalize(string text) => Regex.Replace((text ?? "").Normalize().Trim(), @"\s+", " ").ToUpperInvariant();
    }
}
