using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AlbumArtTool.Core;
using AlbumArtTool.Online;
using AlbumArtTool.Updates;
using SkiaSharp;

internal static partial class Tests
{
    private const string BandcampHtml = "<ul class='result-items'><li class='searchresult data-search'><div class='art'><a href='https://atlas.bandcamp.com/album/morning-miles'><img src='https://f4.bcbits.com/img/a123_9.jpg'></a></div><div class='heading'><a href='https://atlas.bandcamp.com/album/morning-miles'>Morning &amp; Miles</a></div><div class='subhead'>by Atlas Sessions</div></li></ul>";
    private const string DeezerJson = "{\"data\":[{\"title\":\"Morning & Miles\",\"artist\":{\"name\":\"Atlas Sessions\"},\"link\":\"https://www.deezer.com/album/123\",\"cover_medium\":\"https://cdn-images.dzcdn.net/images/cover/abc/250x250.jpg\",\"cover_xl\":\"https://cdn-images.dzcdn.net/images/cover/abc/1000x1000.jpg\"}]}";
    private const string MusicBrainzJson = "{\"release-groups\":[{\"id\":\"48140466-cff6-3222-bd55-63c27e43190d\",\"title\":\"Morning & Miles\",\"artist-credit\":[{\"name\":\"Atlas Sessions\",\"joinphrase\":\"\"}]}]}";

    private static async Task OnlineTests()
    {
        var bandcamp = CoverSearchService.ParseBandcamp(BandcampHtml).Single();
        Assert(bandcamp.Title == "Morning & Miles" && bandcamp.Artist == "Atlas Sessions", "Bandcamp HTML titles and artists are decoded");
        Assert(bandcamp.ImageUrl.EndsWith("_0.jpg") && bandcamp.ThumbnailUrl.EndsWith("_9.jpg"), "Bandcamp original artwork is separate from its small preview");
        var deezer = CoverSearchService.ParseDeezer(DeezerJson).Single();
        Assert(deezer.ImageUrl.EndsWith("1000x1000.jpg") && deezer.Artist == "Atlas Sessions", "Deezer uses the high-resolution album image");
        Assert(Throws(() => CoverSearchService.ParseDeezer("{\"error\":{\"code\":4}}")), "Deezer error responses are reported");
        Assert(Throws(() => CoverSearchService.ParseDeezer("not json")), "malformed source responses are rejected");
        var caa = CoverSearchService.ParseMusicBrainz(MusicBrainzJson).Single();
        Assert(caa.ImageUrl.EndsWith("/front-1200") && caa.Artist == "Atlas Sessions", "CAA front covers are requested at 1200 pixels");
        var query = new CoverQuery { Title = "Morning & Miles", Artist = "Atlas Sessions", Text = "Morning & Miles Atlas Sessions" };
        var ranked = CoverSearchService.Rank(new[] { deezer, bandcamp, caa }, query);
        Assert(ranked[0].Source == "Bandcamp" && ranked[0].MatchScore == 100, "Bandcamp is first for equivalent strong matches");
        bandcamp.Artist = "Someone Else";
        Assert(CoverSearchService.Rank(new[] { deezer, bandcamp }, query)[0].Source == "Deezer", "an accurate album match outranks a wrong-artist Bandcamp result");
        foreach (string url in new[] { "file:///C:/secret.jpg", "https://localhost/a", "https://127.0.0.1/a", "https://bandcamp.com.evil.example/a", "https://bandcamp.com@evil.example/a", "http://bandcamp.com/a", "https://bandcamp.com:8080/a" })
            Assert(!WebTransport.AllowedUrl(url), "unsafe artwork URL rejected: " + url);
        Assert(WebTransport.AllowedUrl("https://ia800100.us.archive.org/cover.jpg"), "CAA archive CDN redirect is allowed");
        Assert(ImageHeader.ReadSize(Cover) == new ImageSize(600, 600), "JPEG source dimensions are read without using the scaled thumbnail");
        using (var bitmap = new SKBitmap(1234, 345))
        { bitmap.Erase(SKColors.Gray); Assert(ImageHeader.ReadSize(Encode(bitmap, SKEncodedImageFormat.Png, 100).Take(24).ToArray()) == new ImageSize(1234, 345), "PNG dimensions need only the small IHDR header"); }
        Assert(ImageHeader.ReadSize(Artwork.Normalize(Cover, 300, ImageKind.Gif)) == new ImageSize(300, 300), "GIF dimensions are read from the header");
        Assert(ImageHeader.ReadSize(new byte[] { 0xff, 0xd8, 0xff, 0xe1, 0xff, 0xff }).IsEmpty, "truncated image metadata has no invented dimensions");

        var transport = new FakeTransport(Cover);
        using (var service = new CoverSearchService(transport))
        {
            CoverSearchUpdate final = null;
            await service.SearchAsync(query, false, new DirectProgress<CoverSearchUpdate>(u => final = u), CancellationToken.None);
            Assert(final.Complete && final.Candidates.Count == 3 && final.Candidates[0].Source == "Bandcamp", "independent sources produce ranked, decoded previews");
            Assert(final.Candidates.All(c => c.Resolution == "600 × 600 px"), "every result gets the full-size image resolution");
            Assert(Size(final.Candidates[0].Thumbnail).Width == 180, "full-size resolution is distinct from 180-pixel display thumbnails");
            int requests = transport.Count;
            await service.SearchAsync(query, false, new DirectProgress<CoverSearchUpdate>(u => final = u), CancellationToken.None);
            Assert(requests == transport.Count, "reselecting an album uses the bounded session cache");
            var image = await service.DownloadAsync(final.Candidates[0], CancellationToken.None);
            Assert(Size(image).Width == 600, "selected original is downloaded and validated");
        }
        transport = new FakeTransport(Cover) { Challenge = true };
        using (var service = new CoverSearchService(transport))
        {
            CoverSearchUpdate final = null;
            await service.SearchAsync(query, true, new DirectProgress<CoverSearchUpdate>(u => final = u), CancellationToken.None);
            Assert(final.Candidates.Count == 2 && final.Issues.Any(i => i.Contains("verification")), "blocked Bandcamp does not block other source results");
            await service.SearchAsync(query, true, new DirectProgress<CoverSearchUpdate>(u => final = u), CancellationToken.None);
            Assert(transport.BandcampRequests == 1, "verification pages are not bypassed or repeatedly requested");
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel(); bool stopped = false;
                try { await service.SearchAsync(query, false, new DirectProgress<CoverSearchUpdate>(u => { }), cancel.Token); }
                catch (OperationCanceledException) { stopped = true; }
                Assert(stopped, "obsolete searches can be cancelled");
            }
        }
        using (var client = new WebTransport(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[2048]) })))
        {
            bool limited = false; try { await client.GetAsync("https://bandcamp.com/search", 100, CancellationToken.None); } catch (InvalidDataException) { limited = true; }
            Assert(limited, "oversized network responses rejected");
        }
        using (var client = new WebTransport(new FakeHandler(_ => { var r = new HttpResponseMessage(HttpStatusCode.Redirect); r.Headers.Location = new Uri("https://localhost/private"); return r; })))
        {
            bool blocked = false; try { await client.GetAsync("https://bandcamp.com/search", 1024, CancellationToken.None); } catch (InvalidDataException) { blocked = true; }
            Assert(blocked, "redirect destinations are validated before fetching");
        }
        int rateRequests = 0;
        using (var client = new WebTransport(new FakeHandler(_ => { rateRequests++; return new HttpResponseMessage((HttpStatusCode)429); })))
        {
            for (int i = 0; i < 2; i++) try { await client.GetAsync("https://musicbrainz.org/ws/2/", 1024, CancellationToken.None); } catch (HttpRequestException) { }
            Assert(rateRequests == 1, "rate-limited servers are given a cooldown");
        }
        bool rangeSent = false;
        using (var client = new WebTransport(new FakeHandler(request =>
        {
            rangeSent = request.Headers.Range?.ToString() == "bytes=0-127";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[4096]) };
        })))
        {
            var prefix = await client.GetPrefixAsync("https://f4.bcbits.com/img/cover.jpg", 128, CancellationToken.None);
            Assert(rangeSent && prefix.Length == 128, "dimension checks request a byte range and stay bounded even when servers ignore it");
        }
    }

    private static async Task<int> LiveSearch()
    {
        using (var service = new CoverSearchService())
        {
            var query = new CoverQuery { Title = "The Shape of Medieval Music to Come", Artist = "Vox Vulgaris", Text = "The Shape of Medieval Music to Come Vox Vulgaris" };
            CoverSearchUpdate final = null;
            await service.SearchAsync(query, true, new DirectProgress<CoverSearchUpdate>(u => final = u), CancellationToken.None);
            foreach (string issue in final.Issues) Console.WriteLine("SOURCE NOTE: " + issue);
            foreach (var result in final.Candidates) Console.WriteLine("LIVE RESULT: " + result.Source + " | " + result.Title + " | " + result.Artist + " | " + result.Resolution);
            if (final.Candidates.Count == 0) throw new Exception("Live lookup returned no usable covers from any source.");
            var image = await service.DownloadAsync(final.Candidates[0], CancellationToken.None);
            Console.WriteLine("LIVE COVER: " + Size(image) + ", " + image.Length + " bytes.");
            if (!final.Candidates.Any(c => c.ImageWidth > 0 && c.ImageHeight > 0)) throw new Exception("No source image dimensions were resolved.");
            File.WriteAllBytes(Path.Combine(output, "live-cover.jpg"), image);
            Console.WriteLine("PASS live album lookup and high-resolution download.");
            return 0;
        }
    }

    // A real file whose version resource names this product at the current version.
    private static string InstallerStandIn => typeof(UpdateService).Assembly.Location;

    private static string ReleaseJson(string tag, byte[] bytes, string digest = null, bool prerelease = false, bool draft = false, string url = null, string name = UpdateService.InstallerName)
    {
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        using (var stream = new MemoryStream())
        {
            using (var json = new Utf8JsonWriter(stream))
            {
                json.WriteStartObject();
                json.WriteString("tag_name", tag); json.WriteBoolean("draft", draft); json.WriteBoolean("prerelease", prerelease);
                json.WriteStartArray("assets");
                json.WriteStartObject();
                json.WriteString("name", name); json.WriteNumber("size", bytes.Length); json.WriteString("digest", digest ?? "sha256:" + hash);
                json.WriteString("browser_download_url", url ?? UpdateService.RepositoryUrl + "/releases/download/" + tag + "/" + name);
                json.WriteEndObject();
                // The legacy bridge for v2.x is published alongside and must be ignored by v3.
                json.WriteStartObject(); json.WriteString("name", "AlbumArtTool.exe"); json.WriteNumber("size", 10); json.WriteString("digest", "sha256:" + hash);
                json.WriteString("browser_download_url", UpdateService.RepositoryUrl + "/releases/download/" + tag + "/AlbumArtTool.exe"); json.WriteEndObject();
                json.WriteEndArray();
                json.WriteEndObject();
            }
            return Encoding.UTF8.GetString(stream.ToArray());
        }
    }

    private static async Task<int> LiveUpdateTest()
    {
        string directory = NewFolder("live-update");
        using (var service = new UpdateService())
        {
            var release = await service.CheckAsync(CancellationToken.None, new Version(0, 0));
            if (release == null) throw new Exception("No published release found.");
            await service.DownloadAsync(release, Path.Combine(directory, "Setup.exe"), null, CancellationToken.None);
            Console.WriteLine("LIVE UPDATE: GitHub release " + release.Version + ", " + release.Size + " bytes; SHA-256 and installer version verified.");
        }
        return 0;
    }

    private static async Task UpdateTests()
    {
        byte[] appBytes = File.ReadAllBytes(InstallerStandIn);
        string version = UpdateService.CurrentVersion.ToString(3);
        string json = ReleaseJson("v" + version, appBytes);
        var old = new Version(1, 2, 1);
        var release = UpdateService.ParseRelease(json, old);
        Assert(release != null && release.Version == UpdateService.CurrentVersion && release.DownloadUrl.EndsWith("/" + UpdateService.InstallerName), "a new stable release offers its installer");
        Assert(UpdateService.ParseRelease(json, UpdateService.CurrentVersion) == null, "equal version does not reinstall");
        Assert(UpdateService.ParseRelease(json, new Version(99, 0)) == null, "older release never downgrades the app");
        Assert(UpdateService.ParseRelease(ReleaseJson("v2.0.0", appBytes, prerelease: true), old) == null &&
            UpdateService.ParseRelease(ReleaseJson("v2.0.0", appBytes, draft: true), old) == null, "draft and prerelease builds are not installed");
        Assert(UpdateService.ParseRelease(ReleaseJson("v1.10.0", appBytes), new Version(1, 9)).Version == new Version(1, 10, 0, 0), "versions compare numerically, including two-digit components");
        Assert(Throws(() => UpdateService.ParseRelease(ReleaseJson("v2.0.0", appBytes, digest: ""), old)), "updates without a checksum are rejected");
        Assert(Throws(() => UpdateService.ParseRelease(ReleaseJson("v2.0.0", appBytes, url: "https://github.com/another/repo/releases/download/v2.0.0/" + UpdateService.InstallerName), old)), "another repository's download is rejected");
        Assert(Throws(() => UpdateService.ParseRelease(ReleaseJson("v2.0.0", appBytes, name: "Other.exe"), old)), "a release without the installer is not offered");
        Assert(Throws(() => UpdateService.ParseRelease("{}", old)), "incomplete release metadata is rejected");
        foreach (string address in new[] { "http://github.com/ISASTO/Album-Art-Tool/releases/download/v2/a.exe", "https://github.com.evil.test/ISASTO/Album-Art-Tool/releases/download/a", "https://github.com@evil.test/a", "file:///C:/a.exe", "https://localhost/a", "https://release-assets.githubusercontent.com:8443/a", "https://example.com/a", "https://api.github.com/user" })
            Assert(!UpdateService.AllowedUrl(address), "unsafe update URL rejected: " + address);
        Assert(UpdateService.AllowedUrl("https://release-assets.githubusercontent.com/github-production-release-asset/1/2?token=abc"), "GitHub's release asset CDN is allowed");

        string download = Path.Combine(NewFolder("update-download"), "Setup.exe");
        int lastProgress = -1;
        using (var service = new UpdateService(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(appBytes) })))
        {
            await service.DownloadAsync(release, download, new DirectProgress<int>(p => lastProgress = p), CancellationToken.None);
            Assert(lastProgress == 100 && UpdateService.Hash(download) == release.Sha256, "verified complete download reports 100 percent");
            File.Delete(download);
        }
        using (var service = new UpdateService(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(appBytes.Take(appBytes.Length - 10).ToArray()) })))
            Assert(await Rejects(() => service.DownloadAsync(release, download, null, CancellationToken.None)) && !File.Exists(download), "truncated download is removed");
        var corrupt = (byte[])appBytes.Clone(); corrupt[100] ^= 1;
        using (var service = new UpdateService(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(corrupt) })))
            Assert(await Rejects(() => service.DownloadAsync(release, download, null, CancellationToken.None)) && !File.Exists(download), "checksum mismatch is removed before execution");
        var wrongVersion = UpdateService.ParseRelease(ReleaseJson("v99.0.0", appBytes), old);
        using (var service = new UpdateService(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(appBytes) })))
            Assert(await Rejects(() => service.DownloadAsync(wrongVersion, download, null, CancellationToken.None)) && !File.Exists(download), "installer version must match the release tag");
        int requests = 0;
        using (var service = new UpdateService(new FakeHandler(_ => { requests++; var r = new HttpResponseMessage(HttpStatusCode.Redirect); r.Headers.Location = new Uri("https://evil.test/update.exe"); return r; })))
            Assert(await Rejects(() => service.DownloadAsync(release, download, null, CancellationToken.None)) && requests == 1, "unsafe redirects are rejected before a second request");
        using (var service = new UpdateService(new FakeHandler(_ => new HttpResponseMessage((HttpStatusCode)429))))
            Assert(await Rejects(() => service.CheckAsync(CancellationToken.None)), "rate limit becomes a recoverable update error");
        using (var cancel = new CancellationTokenSource())
        using (var service = new UpdateService(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(appBytes) })))
        {
            cancel.Cancel();
            Assert(await Rejects(() => service.DownloadAsync(release, download, null, cancel.Token)) && !File.Exists(download), "cancelled update leaves no partial installer");
        }
        Assert(UpdateInstaller.QuoteArgument(@"G:\") == "\"G:\\\\\"", "drive root trailing slash survives Windows argument quoting");
        Assert(UpdateInstaller.QuoteArgument("a\"b") == "\"a\\\"b\"", "embedded quotes are escaped without using a shell");
        Assert(UpdateInstaller.SetupArguments(@"D:\Music & 100% élite\") == "/SILENT /SUPPRESSMSGBOXES /NOCANCEL /NORESTART \"/MUSICFOLDER=D:\\Music & 100% élite\\\"",
            "the installer receives the current music folder to reopen after updating");
        Assert(UpdateInstaller.SetupArguments(null).EndsWith("/LAUNCH=1"), "an update without a folder still reopens the app");
        string workspaceFolder = UpdateInstaller.CreateWorkspace(); File.WriteAllText(Path.Combine(workspaceFolder, UpdateInstaller.SetupFile), "x");
        File.WriteAllText(Path.Combine(workspaceFolder, "keep.txt"), "not ours");
        UpdateInstaller.CleanWorkspace(workspaceFolder);
        Assert(!File.Exists(Path.Combine(workspaceFolder, UpdateInstaller.SetupFile)) && File.Exists(Path.Combine(workspaceFolder, "keep.txt")), "workspace cleanup only removes updater files");
    }

    internal sealed class FakeTransport : IWebTransport
    {
        private readonly byte[] image;
        public int Count, BandcampRequests;
        public bool Challenge;
        public FakeTransport(byte[] image) { this.image = image; }
        public Task<byte[]> GetAsync(string url, int limit, CancellationToken token)
        {
            Interlocked.Increment(ref Count); token.ThrowIfCancellationRequested();
            if (url.StartsWith("https://bandcamp.com/search")) { Interlocked.Increment(ref BandcampRequests); return Task.FromResult(Encoding.UTF8.GetBytes(Challenge ? "<title>Client Challenge</title>" : BandcampHtml)); }
            if (url.Contains("api.deezer.com")) return Task.FromResult(Encoding.UTF8.GetBytes(DeezerJson));
            if (url.Contains("musicbrainz.org/ws")) return Task.FromResult(Encoding.UTF8.GetBytes(MusicBrainzJson));
            return Task.FromResult(image);
        }
        public async Task<byte[]> GetPrefixAsync(string url, int limit, CancellationToken token) => (await GetAsync(url, limit, token)).Take(limit).ToArray();
        public void Dispose() { }
    }

    internal sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> respond;
        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) { this.respond = respond; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(respond(request)); }
    }

    internal sealed class FakeCoverSearch : ICoverSearch
    {
        private readonly byte[] image;
        public FakeCoverSearch(byte[] image) { this.image = image; }
        public Task SearchAsync(CoverQuery query, bool refresh, IProgress<CoverSearchUpdate> progress, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            progress.Report(new CoverSearchUpdate { Complete = true, Candidates = new List<CoverCandidate> {
                new CoverCandidate { Source = "Bandcamp", Title = query.Title, Artist = query.Artist, MatchScore = 100, Thumbnail = image,
                    ImageWidth = 600, ImageHeight = 600, SizeChecked = true, ImageUrl = "https://f4.bcbits.com/img/a123_0.jpg", PageUrl = "https://atlas.bandcamp.com/album/morning-miles" },
                new CoverCandidate { Source = "Deezer", Title = query.Title + " (alternate edition)", Artist = query.Artist, MatchScore = 90,
                    ImageWidth = 600, ImageHeight = 600, SizeChecked = true, Thumbnail = MakeCover(SKColors.Coral), ImageUrl = "https://cdn-images.dzcdn.net/images/cover/abc/1000x1000.jpg", PageUrl = "https://deezer.com/album/123" }
            } });
            return Task.CompletedTask;
        }
        public Task<byte[]> DownloadAsync(CoverCandidate candidate, CancellationToken token)
        {
            if (candidate.ImageUrl == "invalid") throw new IOException("Simulated image download failure.");
            return Task.FromResult(Artwork.Normalize(MakeCover(SKColors.MediumPurple)));
        }
        public void Dispose() { }
    }

    internal sealed class GatedCoverSearch : ICoverSearch
    {
        private readonly FakeCoverSearch previews = new FakeCoverSearch(Cover);
        internal TaskCompletionSource<bool> Started, Release;
        internal GatedCoverSearch() { ResetGate(); }
        internal void ResetGate()
        {
            Started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        public Task SearchAsync(CoverQuery query, bool refresh, IProgress<CoverSearchUpdate> progress, CancellationToken token) => previews.SearchAsync(query, refresh, progress, token);
        public async Task<byte[]> DownloadAsync(CoverCandidate candidate, CancellationToken token)
        {
            if (candidate.ImageUrl == "invalid") throw new IOException("Simulated download failure.");
            if (candidate.ImageUrl == "gated") { Started.TrySetResult(true); await Release.Task; }
            return Artwork.Normalize(MakeCover(new SKColor(0, 128, 128)));
        }
        public void Dispose() { previews.Dispose(); }
    }
}
