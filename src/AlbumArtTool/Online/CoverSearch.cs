using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using AlbumArtTool.Core;

namespace AlbumArtTool.Online
{
    internal sealed class CoverQuery
    {
        public string Title { get; set; }
        public string Artist { get; set; }
        public string Text { get; set; }
        public string CacheKey => Title + "\n" + Artist + "\n" + Text;
        public bool IsDefault => Text == DefaultText(Title, Artist);
        public static string DefaultText(string title, string artist) =>
            (title + " " + (KnownArtist(artist) ? artist : "")).Trim();
        public static bool KnownArtist(string artist) => !string.IsNullOrWhiteSpace(artist) &&
            artist != "Unknown artist" && artist != "Various artists";
    }

    internal sealed class CoverCandidate
    {
        public string Source { get; set; }
        public string Title { get; set; }
        public string Artist { get; set; }
        public string PageUrl { get; set; }
        public string ThumbnailUrl { get; set; }
        public string ImageUrl { get; set; }
        public byte[] Thumbnail { get; set; }
        public int MatchScore { get; set; }
    }

    internal sealed class CoverSearchUpdate
    {
        public List<CoverCandidate> Candidates { get; set; } = new List<CoverCandidate>();
        public List<string> Issues { get; set; } = new List<string>();
        public bool Complete { get; set; }
    }

    internal interface ICoverSearch : IDisposable
    {
        Task SearchAsync(CoverQuery query, bool refresh, IProgress<CoverSearchUpdate> progress, CancellationToken token);
        Task<byte[]> DownloadAsync(CoverCandidate candidate, CancellationToken token);
    }

    internal sealed class CoverSearchService : ICoverSearch
    {
        private readonly IWebTransport web;
        private readonly object sync = new object();
        private readonly Dictionary<string, CoverSearchUpdate> cache = new Dictionary<string, CoverSearchUpdate>();
        private readonly Queue<string> cacheOrder = new Queue<string>();
        private readonly SemaphoreSlim musicBrainzGate = new SemaphoreSlim(1, 1);
        private DateTime lastMusicBrainz = DateTime.MinValue;
        private string bandcampBlocked;

        public CoverSearchService(IWebTransport transport = null) { web = transport ?? new WebTransport(); }

        public async Task SearchAsync(CoverQuery query, bool refresh, IProgress<CoverSearchUpdate> progress, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            lock (sync)
                if (!refresh && cache.TryGetValue(query.CacheKey, out var saved)) { progress.Report(saved); return; }
            var found = new List<CoverCandidate>();
            var issues = new List<string>();
            var gate = new object();
            async Task Provider(string name, Func<Task<List<CoverCandidate>>> search)
            {
                try
                {
                    var candidates = Rank(await search().ConfigureAwait(false), query).Take(6).ToList();
                    int downloaded = 0;
                    // Small, bounded batches; failed sources never hold up other results.
                    await Task.WhenAll(candidates.Select(async candidate =>
                    {
                        try
                        {
                            var bytes = await web.GetAsync(candidate.ThumbnailUrl, 3 * 1024 * 1024, token).ConfigureAwait(false);
                            candidate.Thumbnail = Artwork.Normalize(bytes, 180);
                            token.ThrowIfCancellationRequested();
                            lock (gate)
                            {
                                downloaded++;
                                found.Add(candidate);
                                progress.Report(Snapshot(found, issues, query, false));
                            }
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                        catch (Exception e) when (Expected(e)) { /* A missing CAA front image is normal. */ }
                    })).ConfigureAwait(false);
                    if (candidates.Count > 0 && downloaded == 0)
                        lock (gate) issues.Add(name + ": no cover previews could be downloaded.");
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception e) when (Expected(e)) { lock (gate) issues.Add(name + ": " + e.Message); }
            }
            await Task.WhenAll(
                Provider("Bandcamp", () => BandcampAsync(query, token)),
                Provider("Deezer", () => DeezerAsync(query, token)),
                Provider("Cover Art Archive", () => MusicBrainzAsync(query, token))).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            CoverSearchUpdate final;
            lock (gate) final = Snapshot(found, issues, query, true);
            lock (sync)
            {
                // Keep successful queries only. A temporary outage must remain retryable.
                if (final.Candidates.Count > 0)
                {
                    if (!cache.ContainsKey(query.CacheKey)) cacheOrder.Enqueue(query.CacheKey);
                    cache[query.CacheKey] = final;
                    while (cacheOrder.Count > 24) cache.Remove(cacheOrder.Dequeue());
                }
            }
            progress.Report(final);
        }

        public async Task<byte[]> DownloadAsync(CoverCandidate candidate, CancellationToken token)
        {
            var bytes = await web.GetAsync(candidate.ImageUrl, Artwork.MaxImageBytes, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return Artwork.Normalize(bytes);
        }

        private static CoverSearchUpdate Snapshot(List<CoverCandidate> candidates, List<string> issues, CoverQuery query, bool complete) =>
            new CoverSearchUpdate { Candidates = Rank(candidates, query).GroupBy(c => c.ImageUrl, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First()).Take(15).ToList(), Issues = issues.ToList(), Complete = complete };

        private async Task<List<CoverCandidate>> BandcampAsync(CoverQuery query, CancellationToken token)
        {
            if (bandcampBlocked != null) throw new InvalidDataException(bandcampBlocked);
            string html = await TextAsync("https://bandcamp.com/search?q=" + Uri.EscapeDataString(query.Text) + "&item_type=a", token).ConfigureAwait(false);
            if (Regex.IsMatch(html, @"Client Challenge|captcha|verify you are human|browser verification", RegexOptions.IgnoreCase))
            {
                bandcampBlocked = "browser verification required. Use the Bandcamp link or another source.";
                throw new InvalidDataException(bandcampBlocked);
            }
            return ParseBandcamp(html);
        }

        private async Task<List<CoverCandidate>> DeezerAsync(CoverQuery query, CancellationToken token)
        {
            string terms = query.IsDefault && CoverQuery.KnownArtist(query.Artist)
                ? "album:\"" + Quotes(query.Title) + "\" artist:\"" + Quotes(query.Artist) + "\"" : query.Text;
            return ParseDeezer(await TextAsync("https://api.deezer.com/search/album?q=" + Uri.EscapeDataString(terms) + "&limit=8", token).ConfigureAwait(false));
        }

        private async Task<List<CoverCandidate>> MusicBrainzAsync(CoverQuery query, CancellationToken token)
        {
            await musicBrainzGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var delay = TimeSpan.FromMilliseconds(1100) - (DateTime.UtcNow - lastMusicBrainz);
                if (delay > TimeSpan.Zero) await Task.Delay(delay, token).ConfigureAwait(false);
                lastMusicBrainz = DateTime.UtcNow;
                string terms = query.IsDefault ? "releasegroup:\"" + Lucene(query.Title) + "\"" +
                    (CoverQuery.KnownArtist(query.Artist) ? " AND artist:\"" + Lucene(query.Artist) + "\"" : "") : query.Text;
                return ParseMusicBrainz(await TextAsync("https://musicbrainz.org/ws/2/release-group/?query=" + Uri.EscapeDataString(terms) + "&fmt=json&limit=6", token).ConfigureAwait(false));
            }
            finally { musicBrainzGate.Release(); }
        }

        private async Task<string> TextAsync(string url, CancellationToken token) =>
            Encoding.UTF8.GetString(await web.GetAsync(url, 2 * 1024 * 1024, token).ConfigureAwait(false));

        internal static List<CoverCandidate> ParseBandcamp(string html)
        {
            var list = new List<CoverCandidate>();
            foreach (Match row in Matches(html, @"<li\b[^>]*class\s*=\s*[""'][^""']*\bsearchresult\b[^""']*[""'][^>]*>(.*?)</li>"))
            {
                string block = row.Groups[1].Value;
                var link = Matches(block, @"<a\b[^>]*href\s*=\s*[""']([^""']+/album/[^""']*)[""'][^>]*>(.*?)</a>").Cast<Match>().FirstOrDefault();
                var picture = Matches(block, @"<img\b[^>]*(?:src|data-src)\s*=\s*[""']([^""']*bcbits\.com/[^""']+)[""']").Cast<Match>().FirstOrDefault();
                if (link == null || picture == null) continue;
                string page = SecureUrl(WebUtility.HtmlDecode(link.Groups[1].Value)), thumb = SecureUrl(WebUtility.HtmlDecode(picture.Groups[1].Value));
                string title = ClassText(block, "heading");
                if (title.Length == 0) title = Plain(link.Groups[2].Value);
                string artist = Regex.Replace(ClassText(block, "subhead"), @"^by\s+", "", RegexOptions.IgnoreCase);
                if (!WebTransport.AllowedUrl(page) || !WebTransport.AllowedUrl(thumb) || title.Length == 0) continue;
                string full = Regex.Replace(thumb, @"_\d+(\.(?:jpg|jpeg|png))(?:\?.*)?$", "_0$1", RegexOptions.IgnoreCase);
                list.Add(new CoverCandidate { Source = "Bandcamp", Title = title, Artist = artist, PageUrl = page, ThumbnailUrl = thumb, ImageUrl = full });
            }
            if (list.Count == 0 && !Regex.IsMatch(html, @"searchresult|no results|search-results|no matching", RegexOptions.IgnoreCase))
                throw new InvalidDataException("search is unavailable; use the Bandcamp link to search in your browser.");
            return list;
        }

        internal static List<CoverCandidate> ParseDeezer(string json)
        {
            var root = Json(json);
            if (Get(root, "error") != null) throw new InvalidDataException("album search is temporarily unavailable.");
            var list = new List<CoverCandidate>();
            foreach (var item in Array(root, "data"))
            {
                string image = SecureUrl(String(item, "cover_xl"));
                string thumb = SecureUrl(String(item, "cover_medium"));
                string page = SecureUrl(String(item, "link"));
                if (!WebTransport.AllowedUrl(image) || !WebTransport.AllowedUrl(thumb) || !WebTransport.AllowedUrl(page)) continue;
                list.Add(new CoverCandidate { Source = "Deezer", Title = String(item, "title"), Artist = String(Get(item, "artist"), "name"),
                    PageUrl = page, ThumbnailUrl = thumb, ImageUrl = image });
            }
            return list;
        }

        internal static List<CoverCandidate> ParseMusicBrainz(string json)
        {
            var root = Json(json);
            var list = new List<CoverCandidate>();
            foreach (var item in Array(root, "release-groups"))
            {
                string id = String(item, "id");
                if (!Guid.TryParse(id, out var guid)) continue;
                string artist = string.Concat(Array(item, "artist-credit").Select(a =>
                    (String(a, "name").Length > 0 ? String(a, "name") : String(Get(a, "artist"), "name")) + String(a, "joinphrase")));
                string rootUrl = "https://coverartarchive.org/release-group/" + guid;
                list.Add(new CoverCandidate { Source = "Cover Art Archive", Title = String(item, "title"), Artist = artist,
                    PageUrl = "https://musicbrainz.org/release-group/" + guid, ThumbnailUrl = rootUrl + "/front-250", ImageUrl = rootUrl + "/front-1200" });
            }
            return list;
        }

        internal static List<CoverCandidate> Rank(IEnumerable<CoverCandidate> candidates, CoverQuery query)
        {
            foreach (var candidate in candidates)
                candidate.MatchScore = (int)Math.Round(70 * Similarity(query.Title, candidate.Title) +
                    30 * (CoverQuery.KnownArtist(query.Artist) ? Similarity(query.Artist, candidate.Artist) : 1));
            // A strong exact match beats a poor Bandcamp match; Bandcamp wins within a match tier.
            return candidates.Where(c => !query.IsDefault || c.MatchScore >= 45)
                .OrderByDescending(c => c.MatchScore / 10)
                .ThenBy(c => c.Source == "Bandcamp" ? 0 : c.Source == "Deezer" ? 1 : 2)
                .ThenByDescending(c => c.MatchScore).ToList();
        }

        private static double Similarity(string first, string second)
        {
            string a = Normalize(first), b = Normalize(second);
            if (a.Length == 0 || b.Length == 0) return 0;
            if (a == b) return 1;
            var left = new HashSet<string>(a.Split(' ')); var right = new HashSet<string>(b.Split(' '));
            return (double)left.Intersect(right).Count() / left.Union(right).Count();
        }
        private static string Normalize(string value)
        {
            var builder = new StringBuilder();
            foreach (char c in (value ?? "").Normalize(NormalizationForm.FormD).ToLowerInvariant())
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) builder.Append(char.IsLetterOrDigit(c) ? c : ' ');
            return Regex.Replace(builder.ToString(), @"\s+", " ").Trim();
        }
        private static string Quotes(string value) => (value ?? "").Replace("\"", " ").Trim();
        private static string Lucene(string value) => (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        private static object Json(string text) => new JavaScriptSerializer { MaxJsonLength = 2 * 1024 * 1024, RecursionLimit = 40 }.DeserializeObject(text);
        private static object Get(object item, string key) => item is Dictionary<string, object> values && values.TryGetValue(key, out var value) ? value : null;
        private static string String(object item, string key) => Get(item, key)?.ToString() ?? "";
        private static IEnumerable<object> Array(object item, string key) => (Get(item, key) as IEnumerable)?.Cast<object>() ?? Enumerable.Empty<object>();
        private static MatchCollection Matches(string input, string pattern) => Regex.Matches(input, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(1));
        private static string ClassText(string html, string css)
        {
            var match = Matches(html, "<div\\b[^>]*class\\s*=\\s*[\"'][^\"']*\\b" + css + "\\b[^\"']*[\"'][^>]*>(.*?)</div>").Cast<Match>().FirstOrDefault();
            return match == null ? "" : Plain(match.Groups[1].Value);
        }
        private static string Plain(string html) => Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " ")), @"\s+", " ").Trim();
        private static string SecureUrl(string url) => url.StartsWith("//", StringComparison.Ordinal) ? "https:" + url :
            url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "https://" + url.Substring(7) : url;
        internal static bool Expected(Exception e) => e is IOException || e is System.Net.Http.HttpRequestException ||
            e is OperationCanceledException || e is ArgumentException || e is InvalidOperationException ||
            e is RegexMatchTimeoutException || Artwork.IsImageError(e);
        public void Dispose() { web.Dispose(); }
    }
}
