using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AlbumArtTool.Core;

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
        // Before 4.0 the app was Music Library Manager: repository Album-Art-Tool, installer AlbumArtTool-Setup.exe.
        // Updates accept the old and the new names, so the repository can be renamed without stranding anyone.
        internal const string Owner = "ISASTO";
        internal static readonly string[] Repositories = { "Muzikette", "Album-Art-Tool" };
        internal const string RepositoryUrl = "https://github.com/" + Owner + "/Muzikette";
        internal const string LatestUrl = "https://api.github.com/repos/" + Owner + "/Muzikette/releases/latest";
        internal const string InstallerName = "Muzikette-Setup.exe", LegacyInstallerName = "AlbumArtTool-Setup.exe";
        internal const string ProductName = "Muzikette", LegacyProductName = "Music Library Manager";
        internal static string LatestUrlFor(string repository) => "https://api.github.com/repos/" + Owner + "/" + repository + "/releases/latest";
        internal const int MaxDownload = 200 * 1024 * 1024;
        private readonly HttpClient client;
        internal static Version CurrentVersion => NormalizeVersion(typeof(UpdateService).Assembly.GetName().Version);
        internal static string DisplayVersion => CurrentVersion.ToString(3);

        internal UpdateService(HttpMessageHandler handler = null)
        {
            client = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Muzikette/" + DisplayVersion);
        }

        internal async Task<AppRelease> CheckAsync(CancellationToken token, Version installedVersion = null)
        {
            // The new repository name first; until the repository is renamed it does not exist yet, so the old one answers.
            for (int i = 0; ; i++)
            {
                using (var output = new MemoryStream())
                {
                    try { await GetAsync(LatestUrlFor(Repositories[i]), output, 1024 * 1024, null, token, TimeSpan.FromSeconds(25)).ConfigureAwait(false); }
                    catch (NotFoundException) when (i + 1 < Repositories.Length) { continue; }
                    return ParseRelease(Encoding.UTF8.GetString(output.ToArray()), installedVersion ?? CurrentVersion);
                }
            }
        }

        private sealed class NotFoundException : HttpRequestException
        {
            internal NotFoundException() : base("The release download is not available. Please try again later.") { }
        }

        internal static AppRelease ParseRelease(string json, Version current)
        {
            using (var document = Json.Parse(json))
            {
                var data = document.RootElement;
                if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("draft", out var draft) || !data.TryGetProperty("prerelease", out var prerelease))
                    throw new InvalidDataException("GitHub returned incomplete release information.");
                if (draft.ValueKind != JsonValueKind.False || prerelease.ValueKind != JsonValueKind.False) return null;
                string tag = Json.String(data, "tag_name");
                string number = tag.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? tag.Substring(1) : tag;
                if (!Version.TryParse(number, out var version)) throw new InvalidDataException("The release has an unsupported version number.");
                version = NormalizeVersion(version);
                if (version <= NormalizeVersion(current)) return null;
                if (!data.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("The release does not contain a Windows download yet. Try again shortly.");
                // Muzikette-Setup.exe, or the installer's name before 4.0.
                string name = InstallerName;
                var matches = assets.EnumerateArray().Where(a => Json.String(a, "name") == InstallerName).ToList();
                if (matches.Count == 0) { name = LegacyInstallerName; matches = assets.EnumerateArray().Where(a => Json.String(a, "name") == LegacyInstallerName).ToList(); }
                if (matches.Count != 1) throw new InvalidDataException("The release does not contain a single Windows installer. Try again shortly.");
                JsonElement? asset = matches[0];
                string url = Json.String(asset, "browser_download_url"), digest = Json.String(asset, "digest");
                if (!Repositories.Any(r => string.Equals(url, "https://github.com/" + Owner + "/" + r + "/releases/download/" + Uri.EscapeDataString(tag) + "/" + name, StringComparison.OrdinalIgnoreCase)) || !AllowedUrl(url))
                    throw new InvalidDataException("The update download is not from this app's GitHub release.");
                if (!digest.StartsWith("sha256:", StringComparison.Ordinal) || !ValidHash(digest.Substring(7)))
                    throw new InvalidDataException("GitHub has not supplied a SHA-256 checksum for this download. Try again later.");
                if (!long.TryParse(Json.String(asset, "size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long size) || size <= 0 || size > MaxDownload)
                    throw new InvalidDataException("The update download has an invalid size.");
                return new AppRelease { Version = version, DownloadUrl = url, Sha256 = digest.Substring(7).ToLowerInvariant(), Size = size };
            }
        }

        internal async Task DownloadAsync(AppRelease release, string destination, IProgress<int> progress, CancellationToken token)
        {
            try
            {
                using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                {
                    await GetAsync(release.DownloadUrl, output, release.Size, progress, token, TimeSpan.FromMinutes(5)).ConfigureAwait(false);
                    await output.FlushAsync(token).ConfigureAwait(false);
                    if (output.Length != release.Size) throw new InvalidDataException("The update download was incomplete. Your current app has not changed.");
                }
                token.ThrowIfCancellationRequested();
                VerifyInstaller(destination, release.Sha256, release.Version);
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
                            if (new Uri(url).Host == "api.github.com")
                            {
                                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                                request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
                                request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
                            }
                            using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false))
                            {
                                int code = (int)response.StatusCode;
                                if (code == 403 || code == 429) throw new HttpRequestException("GitHub is limiting update requests. Please try again later.");
                                if (code == 404) throw new NotFoundException();
                                if (code == 301 || code == 302 || code == 303 || code == 307 || code == 308)
                                {
                                    if (response.Headers.Location == null) throw new InvalidDataException("GitHub returned an invalid download redirect.");
                                    url = new Uri(new Uri(url), response.Headers.Location).AbsoluteUri;
                                    continue;
                                }
                                response.EnsureSuccessStatusCode();
                                if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("The update response is larger than expected.");
                                using (timeout.Token.Register(response.Dispose))
                                using (var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
                                {
                                    byte[] buffer = new byte[65536];
                                    long received = 0; int lastPercent = -1;
                                    while (true)
                                    {
                                        int count = await input.ReadAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false);
                                        if (count == 0) break;
                                        received += count;
                                        if (received > limit) throw new InvalidDataException("The update response is larger than expected.");
                                        await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token).ConfigureAwait(false);
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
            // GitHub answers a renamed repository's old address with a redirect to its numeric id.
            if (uri.Host == "api.github.com") return uri.Query.Length == 0 && (Repositories.Any(r => string.Equals(uri.AbsoluteUri, LatestUrlFor(r), StringComparison.OrdinalIgnoreCase)) ||
                System.Text.RegularExpressions.Regex.IsMatch(uri.AbsolutePath, "^/repositories/[0-9]{1,12}/releases/latest$"));
            if (uri.Host == "github.com") return uri.Query.Length == 0 && Repositories.Any(r => uri.AbsolutePath.StartsWith("/" + Owner + "/" + r + "/releases/download/", StringComparison.OrdinalIgnoreCase));
            return uri.Host == "release-assets.githubusercontent.com" || uri.Host == "objects.githubusercontent.com";
        }

        // The checksum proves the bytes are the published release. The version resource proves
        // the release asset is this app's installer at the expected version.
        internal static void VerifyInstaller(string path, string hash, Version version)
        {
            if (!ValidHash(hash) || !string.Equals(Hash(path), hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The update checksum did not match. Your current app has not changed.");
            var info = FileVersionInfo.GetVersionInfo(path);
            var found = NormalizeVersion(new Version(Math.Max(0, info.FileMajorPart), Math.Max(0, info.FileMinorPart), Math.Max(0, info.FileBuildPart), Math.Max(0, info.FilePrivatePart)));
            string product = info.ProductName?.Trim();
            if (product != ProductName && product != LegacyProductName || found != NormalizeVersion(version))
                throw new InvalidDataException("The downloaded installer does not match the expected app and version.");
        }

        internal static string Hash(string path) => Convert.ToHexString(FileTransfer.HashBytes(path)).ToLowerInvariant();
        internal static Version NormalizeVersion(Version version) => new Version(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
        internal static bool ValidHash(string hash) => hash != null && hash.Length == 64 && hash.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f' || c >= 'A' && c <= 'F');
        internal static void TryDelete(string path) { try { File.Delete(path); } catch { } }
        public void Dispose() => client.Dispose();
    }
}
