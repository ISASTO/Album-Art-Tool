using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AlbumArtTool.Core;

namespace AlbumArtTool.Online
{
    public sealed class YtmTrack
    {
        public int Index { get; set; }
        public string Title { get; set; } = "";
        public string Artists { get; set; } = "";
        public string VideoId { get; set; } = "";
        public TimeSpan Duration { get; set; }
        /// <summary>True when YouTube Music lists the official music video for this album track (its audio can differ, e.g. an intro).</summary>
        public bool MusicVideo { get; set; }
        /// <summary>For song-search results: the album the audio upload belongs to.</summary>
        public string AlbumBrowseId { get; set; } = "";
        public string AlbumTitle { get; set; } = "";
        /// <summary>Set when a music-video entry was replaced by the artist's audio upload (the original video id is kept here).</summary>
        public string ReplacedVideoId { get; set; }
        /// <summary>True when YouTube Music only has the music video of this track (no audio upload was found).</summary>
        public bool VideoOnly { get; set; }
        public string Url => "https://music.youtube.com/watch?v=" + VideoId;
    }

    public sealed class YtmAlbum
    {
        public string BrowseId { get; set; } = "";
        public string Title { get; set; } = "";
        public string Artist { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Year { get; set; } = "";
        public string ThumbnailUrl { get; set; } = "";
        public byte[] Thumbnail { get; set; }
        public int TrackCount { get; set; }
        public string PlaylistId { get; set; } = "";
        public List<YtmTrack> Tracks { get; set; }
        /// <summary>Music-video entries have been swapped for audio uploads (see AlbumUpgrade.ResolveAudioAsync).</summary>
        public bool AudioResolved { get; set; }
        public string Url => string.IsNullOrEmpty(PlaylistId) ? "https://music.youtube.com/browse/" + BrowseId : "https://music.youtube.com/playlist?list=" + PlaylistId;
        public string Summary => string.Join(" · ", new[] { Kind, Artist, Year, TrackCount > 0 ? TrackCount + (TrackCount == 1 ? " song" : " songs") : "" }.Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    internal interface IYouTubeMusic : IDisposable
    {
        Task<List<YtmAlbum>> SearchAlbumsAsync(string query, CancellationToken token);
        /// <summary>Fills Tracks, TrackCount and PlaylistId.</summary>
        Task LoadAlbumAsync(YtmAlbum album, CancellationToken token);
        Task<byte[]> ThumbnailAsync(string url, CancellationToken token);
        /// <summary>Song search: audio uploads ("Art Tracks") with their artist, album and length.</summary>
        Task<List<YtmTrack>> SearchSongsAsync(string query, CancellationToken token);
    }

    // YouTube Music (music.youtube.com) only, through the same web API its site uses (the "WEB_REMIX" client).
    // Search is restricted to albums (EPs and singles included); album pages give the track list with durations.
    internal sealed class YouTubeMusic : IYouTubeMusic
    {
        private const string Api = "https://music.youtube.com/youtubei/v1/";
        private const string ClientVersion = "1.20251015.01.00";
        // The albums filter of YouTube Music's search page.
        internal const string AlbumFilter = "EgWKAQIYAWoMEA4QChADEAQQCRAF";
        // The songs filter: audio uploads only, never music videos.
        internal const string SongFilter = "EgWKAQIIAWoMEA4QChADEAQQCRAF";
        private readonly HttpClient client;

        internal YouTubeMusic(HttpMessageHandler handler = null)
        {
            client = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.All });
            client.Timeout = TimeSpan.FromSeconds(20);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://music.youtube.com");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://music.youtube.com/");
            client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.8");
        }

        public async Task<List<YtmAlbum>> SearchAlbumsAsync(string query, CancellationToken token)
        {
            using (var document = await PostAsync("search", "\"query\":" + JsonString(query) + ",\"params\":\"" + AlbumFilter + "\"", token).ConfigureAwait(false))
                return ParseSearch(document.RootElement);
        }

        public async Task<List<YtmTrack>> SearchSongsAsync(string query, CancellationToken token)
        {
            using (var document = await PostAsync("search", "\"query\":" + JsonString(query) + ",\"params\":\"" + SongFilter + "\"", token).ConfigureAwait(false))
                return ParseSongs(document.RootElement);
        }

        public async Task LoadAlbumAsync(YtmAlbum album, CancellationToken token)
        {
            if (album.Tracks != null) return;
            using (var document = await PostAsync("browse", "\"browseId\":" + JsonString(album.BrowseId), token).ConfigureAwait(false))
                ParseAlbum(document.RootElement, album);
        }

        public async Task<byte[]> ThumbnailAsync(string url, CancellationToken token)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                !(uri.Host.EndsWith(".googleusercontent.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".ytimg.com", StringComparison.OrdinalIgnoreCase) || uri.Host == "i.ytimg.com"))
                return null;
            using (var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 4 * 1024 * 1024) return null;
                byte[] bytes = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
                return bytes.Length > 4 * 1024 * 1024 ? null : bytes;
            }
        }

        private async Task<JsonDocument> PostAsync(string endpoint, string fields, CancellationToken token)
        {
            string body = "{\"context\":{\"client\":{\"clientName\":\"WEB_REMIX\",\"clientVersion\":\"" + ClientVersion + "\",\"hl\":\"en\",\"gl\":\"US\"}}," + fields + "}";
            using (var content = new StringContent(body, Encoding.UTF8, "application/json"))
            using (var response = await client.PostAsync(Api + endpoint + "?prettyPrint=false", content, token).ConfigureAwait(false))
            {
                if ((int)response.StatusCode == 429) throw new HttpRequestException("YouTube Music is rate-limiting searches. Wait a minute and try again.");
                response.EnsureSuccessStatusCode();
                string text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                if (text.Length > 16 * 1024 * 1024) throw new InvalidDataException("YouTube Music returned an unexpectedly large response.");
                return Json.Parse(text, 512);
            }
        }

        private static string JsonString(string value)
        {
            var builder = new StringBuilder("\"");
            foreach (char c in value ?? "")
                switch (c)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    default: if (c < ' ') builder.Append("\\u").Append(((int)c).ToString("x4")); else builder.Append(c); break;
                }
            return builder.Append('"').ToString();
        }

        // ---- Parsing (kept tolerant: YouTube Music's layout changes over time) ----

        internal static List<YtmAlbum> ParseSearch(JsonElement root)
        {
            var albums = new List<YtmAlbum>();
            foreach (var item in Find(root, "musicResponsiveListItemRenderer"))
            {
                var browse = Path(item, "navigationEndpoint", "browseEndpoint");
                string browseId = Json.String(browse, "browseId");
                string pageType = Json.String(Path(browse, "browseEndpointContextSupportedConfigs", "browseEndpointContextMusicConfig"), "pageType");
                if (!browseId.StartsWith("MPREb_", StringComparison.Ordinal) || pageType.Length > 0 && pageType != "MUSIC_PAGE_TYPE_ALBUM") continue;
                var columns = Columns(item, "flexColumns", "musicResponsiveListItemFlexColumnRenderer");
                if (columns.Count == 0) continue;
                var album = new YtmAlbum { BrowseId = browseId, Title = string.Concat(columns[0]).Trim(), ThumbnailUrl = Thumbnail(item) };
                if (columns.Count > 1)
                {
                    // "Album • Artist[, Artist & Artist] • 2009"
                    var parts = Split(columns[1]);
                    if (parts.Count > 0) album.Kind = parts[0];
                    if (parts.Count > 1 && IsYear(parts[parts.Count - 1])) { album.Year = parts[parts.Count - 1]; parts.RemoveAt(parts.Count - 1); }
                    if (parts.Count > 1) album.Artist = string.Join(" • ", parts.Skip(1));
                }
                if (album.Title.Length > 0 && albums.All(a => a.BrowseId != browseId)) albums.Add(album);
            }
            return albums;
        }

        // "Title" / "Artist • Album • 2:27" with links: the artist run points at a channel, the album run at MPREb_…
        internal static List<YtmTrack> ParseSongs(JsonElement root)
        {
            var songs = new List<YtmTrack>();
            foreach (var item in Find(root, "musicResponsiveListItemRenderer"))
            {
                string videoId = Json.String(Json.Get(item, "playlistItemData"), "videoId");
                var watch = Find(item, "watchEndpoint").Cast<JsonElement?>().FirstOrDefault();
                if (videoId.Length == 0) videoId = Json.String(watch, "videoId");
                string type = Json.String(Path(watch, "watchEndpointMusicSupportedConfigs", "watchEndpointMusicConfig"), "musicVideoType");
                var columns = Json.Array(item, "flexColumns").Select(c => Json.Array(Json.Get(Json.Get(c, "musicResponsiveListItemFlexColumnRenderer"), "text"), "runs").ToList()).ToList();
                if (videoId.Length == 0 || columns.Count < 2) continue;
                var song = new YtmTrack { VideoId = videoId, Title = string.Concat(columns[0].Select(r => Json.String(r, "text"))).Trim(),
                    MusicVideo = type.Length > 0 && type != "MUSIC_VIDEO_TYPE_ATV" };
                var artists = new List<string>();
                foreach (var run in columns[1])
                {
                    string text = Json.String(run, "text").Trim();
                    string browseId = Json.String(Path(run, "navigationEndpoint", "browseEndpoint"), "browseId");
                    if (browseId.StartsWith("MPREb_", StringComparison.Ordinal)) { song.AlbumBrowseId = browseId; song.AlbumTitle = text; }
                    else if (browseId.StartsWith("UC", StringComparison.Ordinal)) artists.Add(text);
                    else if (text.Contains(':') && ParseDuration(text) > TimeSpan.Zero) song.Duration = ParseDuration(text);
                }
                song.Artists = string.Join(", ", artists);
                if (song.Title.Length > 0) songs.Add(song);
            }
            return songs;
        }

        internal static void ParseAlbum(JsonElement root, YtmAlbum album)
        {
            var header = Find(root, "musicResponsiveHeaderRenderer").Cast<JsonElement?>().FirstOrDefault() ?? Find(root, "musicDetailHeaderRenderer").Cast<JsonElement?>().FirstOrDefault();
            if (header != null)
            {
                string title = string.Concat(Runs(Json.Get(header, "title"))).Trim();
                if (title.Length > 0) album.Title = title;
                string artist = string.Concat(Runs(Json.Get(header, "straplineTextOne"))).Trim();
                if (artist.Length > 0) album.Artist = artist;
                var subtitle = Split(Runs(Json.Get(header, "subtitle")));
                if (subtitle.Count > 0 && album.Kind.Length == 0) album.Kind = subtitle[0];
                string year = subtitle.LastOrDefault(IsYear);
                if (year != null) album.Year = year;
                string thumb = Thumbnail(header.Value);
                if (thumb.Length > 0) album.ThumbnailUrl = thumb;
            }
            string canonical = Json.String(Path(root, "microformat", "microformatDataRenderer"), "urlCanonical");
            int list = canonical.IndexOf("list=", StringComparison.Ordinal);
            if (list >= 0) album.PlaylistId = canonical.Substring(list + 5).Split('&')[0];
            var tracks = new List<YtmTrack>();
            var contents = Json.Get(root, "contents");
            if (contents != null)
                foreach (var item in Find(contents.Value, "musicResponsiveListItemRenderer"))
                {
                    var columns = Columns(item, "flexColumns", "musicResponsiveListItemFlexColumnRenderer");
                    string videoId = Json.String(Json.Get(item, "playlistItemData"), "videoId");
                    var watch = Find(item, "watchEndpoint").Cast<JsonElement?>().FirstOrDefault();
                    if (videoId.Length == 0) videoId = Json.String(watch, "videoId");
                    if (videoId.Length == 0 || columns.Count == 0) continue; // unavailable in this region
                    var fixedColumns = Columns(item, "fixedColumns", "musicResponsiveListItemFixedColumnRenderer");
                    int.TryParse(string.Concat(Runs(Json.Get(item, "index"))).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int index);
                    string type = Json.String(Path(watch, "watchEndpointMusicSupportedConfigs", "watchEndpointMusicConfig"), "musicVideoType");
                    tracks.Add(new YtmTrack
                    {
                        Index = index > 0 ? index : tracks.Count + 1, Title = string.Concat(columns[0]).Trim(), VideoId = videoId,
                        Artists = columns.Count > 1 ? string.Concat(columns[1]).Trim() : "",
                        Duration = fixedColumns.Count > 0 ? ParseDuration(string.Concat(fixedColumns[0])) : TimeSpan.Zero,
                        MusicVideo = type == "MUSIC_VIDEO_TYPE_OMV" || type == "MUSIC_VIDEO_TYPE_UGC"
                    });
                }
            album.Tracks = tracks;
            album.TrackCount = tracks.Count;
            if (album.Artist.Length == 0) album.Artist = tracks.Select(t => t.Artists).FirstOrDefault(a => a.Length > 0) ?? "";
        }

        internal static TimeSpan ParseDuration(string text)
        {
            var parts = (text ?? "").Trim().Split(':');
            double seconds = 0;
            foreach (string part in parts)
            {
                if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)) return TimeSpan.Zero;
                seconds = seconds * 60 + value;
            }
            return TimeSpan.FromSeconds(seconds);
        }

        private static bool IsYear(string text) => text.Length == 4 && text.All(char.IsDigit);

        // Text runs separated by " • " become parts; "A, B & C" stays one part.
        private static List<string> Split(IEnumerable<string> runs) =>
            string.Concat(runs).Split('•').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();

        private static List<List<string>> Columns(JsonElement item, string key, string renderer) =>
            Json.Array(item, key).Select(c => Runs(Json.Get(Json.Get(c, renderer), "text")).ToList()).ToList();

        private static IEnumerable<string> Runs(JsonElement? text) => Json.Array(text, "runs").Select(r => Json.String(r, "text"));

        private static string Thumbnail(JsonElement item) =>
            Find(item, "thumbnails").SelectMany(t => t.ValueKind == JsonValueKind.Array ? t.EnumerateArray() : Enumerable.Empty<JsonElement>())
                .Select(t => (Url: Json.String(t, "url"), Width: int.TryParse(Json.String(t, "width"), out int w) ? w : 0))
                .Where(t => t.Url.StartsWith("https://", StringComparison.Ordinal)).OrderByDescending(t => t.Width).Select(t => t.Url).FirstOrDefault() ?? "";

        private static JsonElement? Path(JsonElement? item, params string[] keys)
        {
            foreach (string key in keys) item = Json.Get(item, key);
            return item;
        }

        // Every value stored under the key, depth first (documents are a few MB at most).
        private static IEnumerable<JsonElement> Find(JsonElement element, string key)
        {
            var stack = new Stack<JsonElement>(); stack.Push(element);
            var found = new List<JsonElement>();
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (current.ValueKind == JsonValueKind.Object)
                {
                    var children = new List<JsonElement>();
                    foreach (var property in current.EnumerateObject())
                    {
                        if (property.NameEquals(key)) found.Add(property.Value);
                        else if (property.Value.ValueKind == JsonValueKind.Object || property.Value.ValueKind == JsonValueKind.Array) children.Add(property.Value);
                    }
                    for (int i = children.Count - 1; i >= 0; i--) stack.Push(children[i]);
                }
                else if (current.ValueKind == JsonValueKind.Array)
                {
                    var items = current.EnumerateArray().ToList();
                    for (int i = items.Count - 1; i >= 0; i--) if (items[i].ValueKind == JsonValueKind.Object || items[i].ValueKind == JsonValueKind.Array) stack.Push(items[i]);
                }
            }
            return found;
        }

        public void Dispose() => client.Dispose();
    }
}
