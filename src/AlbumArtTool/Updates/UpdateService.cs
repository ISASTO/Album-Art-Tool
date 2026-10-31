using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace AlbumArtTool.Updates
{
    internal sealed class AppRelease
    {
        internal Version Version;
        internal string DownloadUrl, Sha256;
        internal long Size;
    }

    internal sealed class UpdateService : IDisposable
    {
        internal const string RepositoryUrl = "https://github.com/ISASTO/Album-Art-Tool";
        internal const string LatestUrl = "https://api.github.com/repos/ISASTO/Album-Art-Tool/releases/latest";
        internal const int MaxDownload = 50 * 1024 * 1024;
        private readonly HttpClient client;
        internal static Version CurrentVersion => NormalizeVersion(typeof(UpdateService).Assembly.GetName().Version);
        internal static string DisplayVersion => CurrentVersion.ToString(3);

        internal UpdateService(HttpMessageHandler handler = null)
        {
            client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("AlbumArtTool/" + DisplayVersion);
        }

        internal async Task<AppRelease> CheckAsync(CancellationToken token)
        {
            using (var output = new MemoryStream())
            {
                await GetAsync(LatestUrl, output, 1024 * 1024, null, token, TimeSpan.FromSeconds(25)).ConfigureAwait(false);
                return ParseRelease(Encoding.UTF8.GetString(output.ToArray()), CurrentVersion);
            }
        }

        internal static AppRelease ParseRelease(string json, Version current)
        {
            var data = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 }.DeserializeObject(json) as Dictionary<string, object>;
            if (data == null || !data.TryGetValue("draft", out var draft) || !data.TryGetValue("prerelease", out var prerelease))
                throw new InvalidDataException("GitHub returned incomplete release information.");
            if (!Equals(draft, false) || !Equals(prerelease, false)) return null;
            string tag = StringValue(data, "tag_name");
            string number = tag.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? tag.Substring(1) : tag;
            if (!Version.TryParse(number, out var version)) throw new InvalidDataException("The release has an unsupported version number.");
            version = NormalizeVersion(version);
            if (version <= NormalizeVersion(current)) return null;
            if (!data.TryGetValue("assets", out var value) || !(value is object[] assets))
                throw new InvalidDataException("The release does not contain a Windows download yet. Try again shortly.");
            var matches = assets.OfType<Dictionary<string, object>>().Where(a => StringValue(a, "name") == "AlbumArtTool.exe").ToList();
            if (matches.Count != 1) throw new InvalidDataException("The release does not contain a single Windows EXE. Try again shortly.");
            var asset = matches[0];
            string url = StringValue(asset, "browser_download_url"), digest = StringValue(asset, "digest");
            string expectedUrl = RepositoryUrl + "/releases/download/" + Uri.EscapeDataString(tag) + "/AlbumArtTool.exe";
            if (!string.Equals(url, expectedUrl, StringComparison.Ordinal) || !AllowedUrl(url))
                throw new InvalidDataException("The update download is not from this app's GitHub release.");
            if (!digest.StartsWith("sha256:", StringComparison.Ordinal) || !ValidHash(digest.Substring(7)))
                throw new InvalidDataException("GitHub has not supplied a SHA-256 checksum for this download. Try again later.");
            if (!asset.TryGetValue("size", out var sizeValue) || !long.TryParse(Convert.ToString(sizeValue, CultureInfo.InvariantCulture), out long size) || size <= 0 || size > MaxDownload)
                throw new InvalidDataException("The update download has an invalid size.");
            return new AppRelease { Version = version, DownloadUrl = url, Sha256 = digest.Substring(7).ToLowerInvariant(), Size = size };
        }

        internal async Task DownloadAsync(AppRelease release, string destination, IProgress<int> progress, CancellationToken token)
        {
            try
            {
                using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                {
                    await GetAsync(release.DownloadUrl, output, release.Size, progress, token, TimeSpan.FromMinutes(3)).ConfigureAwait(false);
                    await output.FlushAsync(token).ConfigureAwait(false);
                    if (output.Length != release.Size) throw new InvalidDataException("The update download was incomplete. Your current app has not changed.");
                }
                token.ThrowIfCancellationRequested();
                VerifyExecutable(destination, release.Sha256, release.Version);
            }
            catch { TryDelete(destination); throw; }
        }

        private async Task GetAsync(string url, Stream output, long limit, IProgress<int> progress, CancellationToken token, TimeSpan duration)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(duration);
                try
                {
                    for (int redirects = 0; redirects < 6; redirects++)
                    {
                        if (!AllowedUrl(url)) throw new InvalidDataException("GitHub redirected the download to an unsupported address.");
                        using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                        {
                            if (url == LatestUrl)
                            {
                                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                                request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
                                request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
                            }
                            using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false))
                            {
                                int code = (int)response.StatusCode;
                                if (code == 403 || code == 429) throw new HttpRequestException("GitHub is limiting update requests. Please try again later.");
                                if (code == 404) throw new HttpRequestException("The release download is not available. Please try again later.");
                                if (code == 301 || code == 302 || code == 303 || code == 307 || code == 308)
                                {
                                    if (response.Headers.Location == null) throw new InvalidDataException("GitHub returned an invalid download redirect.");
                                    url = new Uri(new Uri(url), response.Headers.Location).AbsoluteUri;
                                    continue;
                                }
                                response.EnsureSuccessStatusCode();
                                if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("The update response is larger than expected.");
                                using (var registration = timeout.Token.Register(response.Dispose))
                                using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                                {
                                    byte[] buffer = new byte[65536];
                                    long received = 0; int lastPercent = -1;
                                    while (true)
                                    {
                                        int count = await input.ReadAsync(buffer, 0, buffer.Length, timeout.Token).ConfigureAwait(false);
                                        if (count == 0) break;
                                        received += count;
                                        if (received > limit) throw new InvalidDataException("The update response is larger than expected.");
                                        await output.WriteAsync(buffer, 0, count, timeout.Token).ConfigureAwait(false);
                                        int percent = (int)(100 * received / limit);
                                        if (percent != lastPercent) { progress?.Report(percent); lastPercent = percent; }
                                    }
                                }
                                return;
                            }
                        }
                    }
                    throw new HttpRequestException("GitHub returned too many download redirects.");
                }
                catch (Exception e) when (timeout.IsCancellationRequested)
                {
                    if (token.IsCancellationRequested) throw new OperationCanceledException(token);
                    throw new IOException("The update request timed out. Please check your connection and try again.", e);
                }
            }
        }

        internal static bool AllowedUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Port != 443 || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0) return false;
            if (uri.Host == "api.github.com") return uri.AbsoluteUri == LatestUrl;
            if (uri.Host == "github.com") return uri.AbsolutePath.StartsWith("/ISASTO/Album-Art-Tool/releases/download/", StringComparison.Ordinal) && uri.Query.Length == 0;
            return uri.Host == "release-assets.githubusercontent.com" || uri.Host == "objects.githubusercontent.com";
        }

        internal static void VerifyExecutable(string path, string hash, Version version)
        {
            if (!ValidHash(hash) || !string.Equals(Hash(path), hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The update checksum did not match. Your current app has not changed.");
            var assembly = AssemblyName.GetAssemblyName(path);
            if (assembly.Name != "AlbumArtTool" || NormalizeVersion(assembly.Version) != NormalizeVersion(version))
                throw new InvalidDataException("The downloaded EXE does not match the expected app and version.");
        }

        internal static string Hash(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
        internal static Version NormalizeVersion(Version version) => new Version(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
        internal static bool ValidHash(string hash) => hash != null && hash.Length == 64 && hash.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f' || c >= 'A' && c <= 'F');
        private static string StringValue(Dictionary<string, object> data, string key) => data.TryGetValue(key, out var value) ? value as string ?? "" : "";
        internal static void TryDelete(string path) { try { File.Delete(path); } catch { } }
        public void Dispose() => client.Dispose();
    }
}
