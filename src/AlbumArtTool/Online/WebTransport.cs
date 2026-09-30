using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace AlbumArtTool.Online
{
    internal interface IWebTransport : IDisposable
    {
        Task<byte[]> GetAsync(string url, int limit, CancellationToken token);
        Task<byte[]> GetPrefixAsync(string url, int limit, CancellationToken token);
    }

    internal sealed class WebTransport : IWebTransport
    {
        private readonly HttpClient client;
        private readonly Dictionary<string, DateTime> cooldown = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        public WebTransport(HttpMessageHandler handler = null)
        {
            client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate });
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("AlbumArtTool/1.2.0 (+https://github.com/ISASTO/Album-Art-Tool)");
        }

        public Task<byte[]> GetAsync(string url, int limit, CancellationToken token) => FetchAsync(url, limit, false, token);
        public Task<byte[]> GetPrefixAsync(string url, int limit, CancellationToken token) => FetchAsync(url, limit, true, token);

        private async Task<byte[]> FetchAsync(string url, int limit, bool prefixOnly, CancellationToken token)
        {
            if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(16));
                try
                {
                    for (int redirects = 0; redirects < 6; redirects++)
                    {
                        if (!AllowedUrl(url)) throw new InvalidDataException("The artwork source returned an unsupported address.");
                        var uri = new Uri(url);
                        lock (cooldown)
                            if (cooldown.TryGetValue(uri.Host, out var until) && until > DateTime.UtcNow)
                                throw new HttpRequestException("source is busy; try again in " + (int)Math.Ceiling((until - DateTime.UtcNow).TotalSeconds) + " seconds.");
                        using (var request = new HttpRequestMessage(HttpMethod.Get, uri))
                        {
                            if (prefixOnly) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, limit - 1);
                            using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false))
                            {
                                int status = (int)response.StatusCode;
                                if (status == 429 || status == 503)
                                {
                                    TimeSpan pause = response.Headers.RetryAfter?.Delta ??
                                        (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromSeconds(60);
                                    lock (cooldown) cooldown[uri.Host] = DateTime.UtcNow.Add(pause < TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : pause);
                                    throw new HttpRequestException("source is busy or rate-limited. Other sources can still load.");
                                }
                                if (status == 301 || status == 302 || status == 303 || status == 307 || status == 308)
                                {
                                    if (response.Headers.Location == null) throw new HttpRequestException("invalid image redirect.");
                                    var target = new Uri(uri, response.Headers.Location);
                                    // CAA has historically returned HTTP archive.org links; use HTTPS for those public resources.
                                    if (target.Scheme == "http") target = new UriBuilder(target) { Scheme = "https", Port = -1 }.Uri;
                                    url = target.AbsoluteUri;
                                    continue;
                                }
                                response.EnsureSuccessStatusCode();
                                if (!prefixOnly && response.Content.Headers.ContentLength > limit) throw new InvalidDataException("The image or response is too large.");
                                using (var registration = timeout.Token.Register(() => response.Dispose()))
                                using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                                using (var output = new MemoryStream())
                                {
                                    var buffer = new byte[16384];
                                    while (true)
                                    {
                                        int remaining = prefixOnly ? (int)Math.Min(buffer.Length, limit - output.Length) : buffer.Length;
                                        if (remaining == 0) break;
                                        int count = await input.ReadAsync(buffer, 0, remaining, timeout.Token).ConfigureAwait(false);
                                        if (count == 0) break;
                                        if (output.Length + count > limit) throw new InvalidDataException("The image or response is too large.");
                                        output.Write(buffer, 0, count);
                                    }
                                    return output.ToArray();
                                }
                            }
                        }
                    }
                    throw new HttpRequestException("too many image redirects.");
                }
                catch (Exception e) when (timeout.IsCancellationRequested && !token.IsCancellationRequested)
                { throw new IOException("source timed out. Try another cover or search again.", e); }
            }
        }

        internal static bool AllowedUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Port != 443 || uri.UserInfo.Length > 0 || uri.IsLoopback) return false;
            string host = uri.DnsSafeHost;
            foreach (string domain in new[] { "bandcamp.com", "bcbits.com", "musicbrainz.org", "coverartarchive.org", "archive.org", "deezer.com", "dzcdn.net" })
                if (host.Equals(domain, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        public void Dispose() { client.Dispose(); }
    }
}
