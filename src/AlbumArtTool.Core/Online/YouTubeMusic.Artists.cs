using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AlbumArtTool.Core;

namespace AlbumArtTool.Online
{
    public sealed class YtmArtist
    {
        public string BrowseId { get; set; } = "";
        public string Name { get; set; } = "";
        /// <summary>For search results: "Artist · 1.2M monthly audience".</summary>
        public string Subtitle { get; set; } = "";
        public string ThumbnailUrl { get; set; } = "";
        public byte[] Thumbnail { get; set; }
        /// <summary>Albums, singles and EPs, newest first as YouTube Music lists them. Null until loaded.</summary>
        public List<YtmAlbum> Releases { get; set; }
        public string Url => "https://music.youtube.com/channel/" + BrowseId;
    }

    // Artist search and artist pages: the "Albums" and "Singles & EPs" shelves, each expanded through its "More" page.
    internal sealed partial class YouTubeMusic
    {
        // The artists filter of YouTube Music's search page.
        internal const string ArtistFilter = "EgWKAQIgAWoMEA4QChADEAQQCRAF";

        public async Task<List<YtmArtist>> SearchArtistsAsync(string query, CancellationToken token)
        {
            using (var document = await PostAsync("search", "\"query\":" + JsonString(query) + ",\"params\":\"" + ArtistFilter + "\"", token).ConfigureAwait(false))
                return ParseArtists(document.RootElement);
        }

        public async Task LoadArtistAsync(YtmArtist artist, CancellationToken token)
        {
            if (artist.Releases != null) return;
            List<(string Kind, string BrowseId, string Params)> more;
            using (var document = await PostAsync("browse", "\"browseId\":" + JsonString(artist.BrowseId), token).ConfigureAwait(false))
                more = ParseArtistPage(document.RootElement, artist);
            // A shelf shows about ten releases; its "More" page lists them all.
            foreach (var (kind, browseId, parameters) in more)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    string fields = "\"browseId\":" + JsonString(browseId) + (string.IsNullOrEmpty(parameters) ? "" : ",\"params\":" + JsonString(parameters));
                    using (var document = await PostAsync("browse", fields, token).ConfigureAwait(false))
                        Merge(artist, ParseReleases(document.RootElement, kind, artist.Name));
                }
                catch (Exception e) when (e is System.Net.Http.HttpRequestException || e is JsonException || e is System.IO.InvalidDataException) { /* The shelf's first releases are still shown. */ }
            }
        }

        internal static List<YtmArtist> ParseArtists(JsonElement root)
        {
            var artists = new List<YtmArtist>();
            foreach (var item in Find(root, "musicResponsiveListItemRenderer"))
            {
                var browse = Path(item, "navigationEndpoint", "browseEndpoint");
                string browseId = Json.String(browse, "browseId");
                string pageType = Json.String(Path(browse, "browseEndpointContextSupportedConfigs", "browseEndpointContextMusicConfig"), "pageType");
                if (!browseId.StartsWith("UC", StringComparison.Ordinal) || pageType.Length > 0 && pageType != "MUSIC_PAGE_TYPE_ARTIST") continue;
                var columns = Columns(item, "flexColumns", "musicResponsiveListItemFlexColumnRenderer");
                if (columns.Count == 0) continue;
                var artist = new YtmArtist { BrowseId = browseId, Name = string.Concat(columns[0]).Trim(), ThumbnailUrl = Thumbnail(item) };
                if (columns.Count > 1) artist.Subtitle = string.Join(" · ", Split(columns[1]));
                if (artist.Name.Length > 0 && artists.All(a => a.BrowseId != browseId)) artists.Add(artist);
            }
            // The top result card ("musicCardShelfRenderer") is often the artist itself.
            foreach (var card in Find(root, "musicCardShelfRenderer"))
            {
                var browse = Path(card, "title", "runs");
                var run = browse != null && browse.Value.ValueKind == JsonValueKind.Array ? browse.Value.EnumerateArray().FirstOrDefault() : default;
                string browseId = run.ValueKind == JsonValueKind.Object ? Json.String(Path(run, "navigationEndpoint", "browseEndpoint"), "browseId") : "";
                string name = run.ValueKind == JsonValueKind.Object ? Json.String(run, "text").Trim() : "";
                if (!browseId.StartsWith("UC", StringComparison.Ordinal) || name.Length == 0 || artists.Any(a => a.BrowseId == browseId)) continue;
                artists.Insert(0, new YtmArtist { BrowseId = browseId, Name = name, ThumbnailUrl = Thumbnail(card), Subtitle = string.Join(" · ", Split(Runs(Json.Get(card, "subtitle")))) });
            }
            return artists;
        }

        /// <summary>Reads the artist header and release shelves; returns the "More" pages still to load.</summary>
        internal static List<(string Kind, string BrowseId, string Params)> ParseArtistPage(JsonElement root, YtmArtist artist)
        {
            var header = Find(root, "musicImmersiveHeaderRenderer").Cast<JsonElement?>().FirstOrDefault() ?? Find(root, "musicVisualHeaderRenderer").Cast<JsonElement?>().FirstOrDefault()
                ?? Find(root, "musicResponsiveHeaderRenderer").Cast<JsonElement?>().FirstOrDefault();
            if (header != null)
            {
                string name = string.Concat(Runs(Json.Get(header, "title"))).Trim();
                if (name.Length > 0) artist.Name = name;
                string thumb = Thumbnail(header.Value);
                if (thumb.Length > 0) artist.ThumbnailUrl = thumb;
            }
            artist.Releases = new List<YtmAlbum>();
            var more = new List<(string, string, string)>();
            foreach (var shelf in Find(root, "musicCarouselShelfRenderer"))
            {
                var shelfHeader = Path(shelf, "header", "musicCarouselShelfBasicHeaderRenderer");
                var titleRuns = Json.Array(Json.Get(shelfHeader, "title"), "runs").ToList();
                string title = string.Concat(titleRuns.Select(r => Json.String(r, "text"))).Trim();
                string kind = ShelfKind(title);
                if (kind == null) continue;
                Merge(artist, ParseReleases(shelf, kind, artist.Name));
                // "More": the title link or the More button opens the full list.
                var endpoint = titleRuns.Select(r => Path(r, "navigationEndpoint", "browseEndpoint")).FirstOrDefault(e => e != null)
                    ?? Path(shelfHeader, "moreContentButton", "buttonRenderer", "navigationEndpoint", "browseEndpoint");
                string browseId = Json.String(endpoint, "browseId");
                if (browseId.Length > 0) more.Add((kind, browseId, Json.String(endpoint, "params")));
            }
            return more;
        }

        /// <summary>Album, single and EP tiles anywhere under the element (a shelf or a "More" grid).</summary>
        internal static List<YtmAlbum> ParseReleases(JsonElement element, string kind, string artist)
        {
            var releases = new List<YtmAlbum>();
            foreach (var item in Find(element, "musicTwoRowItemRenderer"))
            {
                string browseId = Json.String(Path(item, "navigationEndpoint", "browseEndpoint"), "browseId");
                if (!browseId.StartsWith("MPREb_", StringComparison.Ordinal)) continue;
                var release = new YtmAlbum { BrowseId = browseId, Title = string.Concat(Runs(Json.Get(item, "title"))).Trim(), Artist = artist ?? "", Kind = kind, ThumbnailUrl = Thumbnail(item) };
                // "Album • 2022", "EP • 2021", "Single • 2024", or just "2022".
                foreach (string part in Split(Runs(Json.Get(item, "subtitle"))))
                {
                    if (IsYear(part)) release.Year = part;
                    else if (part == "Album" || part == "EP" || part == "Single") release.Kind = part;
                }
                if (release.Title.Length > 0 && releases.All(r => r.BrowseId != browseId)) releases.Add(release);
            }
            return releases;
        }

        internal static string ShelfKind(string title)
        {
            if (title.IndexOf("Single", StringComparison.OrdinalIgnoreCase) >= 0 || title.Equals("EPs", StringComparison.OrdinalIgnoreCase)) return "Single";
            if (title.IndexOf("Album", StringComparison.OrdinalIgnoreCase) >= 0) return "Album";
            return null;
        }

        private static void Merge(YtmArtist artist, List<YtmAlbum> releases)
        {
            foreach (var release in releases)
            {
                var known = artist.Releases.FirstOrDefault(r => r.BrowseId == release.BrowseId);
                if (known == null) { artist.Releases.Add(release); continue; }
                if (known.Year.Length == 0) known.Year = release.Year;
                if (known.ThumbnailUrl.Length == 0) known.ThumbnailUrl = release.ThumbnailUrl;
            }
        }
    }
}
