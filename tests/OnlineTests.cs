using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using AlbumArtTool;
using AlbumArtTool.Core;
using AlbumArtTool.Online;

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
        Assert(deezer.ImageUrl.EndsWith("1000x1000.jpg"), "Deezer uses the high-resolution album image");
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

        var transport = new FakeTransport(Cover);
        using (var service = new CoverSearchService(transport))
        {
            CoverSearchUpdate final = null;
            await service.SearchAsync(query, false, new DirectProgress<CoverSearchUpdate>(u => final = u), CancellationToken.None);
            Assert(final.Complete && final.Candidates.Count == 3 && final.Candidates[0].Source == "Bandcamp", "independent sources produce ranked, decoded previews");
            int requests = transport.Count;
            await service.SearchAsync(query, false, new DirectProgress<CoverSearchUpdate>(u => final = u), CancellationToken.None);
            Assert(requests == transport.Count, "reselecting an album uses the bounded session cache");
            var image = await service.DownloadAsync(final.Candidates[0], CancellationToken.None);
            using (var decoded = Artwork.Decode(image)) Assert(decoded.Width == 600, "selected original is downloaded and validated");
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
        using (var client = new WebTransport(new FakeHandler(_ =>
        { var r = new HttpResponseMessage(HttpStatusCode.Redirect); r.Headers.Location = new Uri("https://localhost/private"); return r; })))
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
    }

    private static async Task OnlineGuiTest(MainForm form, AlbumList albums, string output)
    {
        var panel = Descendants(form).OfType<CoverSearchPanel>().Single();
        await panel.CurrentSearch;
        await Task.Delay(60); // Drain posted UI progress messages.
        Assert(panel.DisplayedCandidates.Count == 2, "selecting an album automatically displays online suggestions");
        var selected = albums.SelectedAlbum;
        string before = CoverEditor.Hash(selected.Tracks[0].Path);
        await form.ApplyOnlineCoverAsync("another-album", panel.DisplayedCandidates[0]);
        Assert(CoverEditor.Hash(selected.Tracks[0].Path) == before, "a stale artwork result cannot edit a different album");
        var firstAction = Descendants(panel).OfType<Button>().First(b => b.Text == "Apply this cover");
        var viewport = firstAction.Parent.Parent;
        Assert(viewport.RectangleToScreen(viewport.ClientRectangle).Contains(firstAction.RectangleToScreen(firstAction.ClientRectangle)),
            "the first cover action is fully visible without scrolling");
        using (var bitmap = new Bitmap(form.Width, form.Height))
        { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(output, "online-cover-search.png"), ImageFormat.Png); }
        var bad = new CoverCandidate { Source = "Broken source", ImageUrl = "invalid" };
        await form.ApplyOnlineCoverAsync(selected.Key, bad);
        Assert(CoverEditor.Hash(selected.Tracks[0].Path) == before, "failed image downloads leave album files unchanged");
        await panel.CurrentSearch; await Task.Delay(60);
        var use = Descendants(panel).OfType<Button>().First(b => b.Text == "Apply this cover");
        use.PerformClick(); await panel.CurrentApply;
        Assert(CoverEditor.Hash(selected.Tracks[0].Path) != before, "one click on a result applies its artwork");
        Assert(Descendants(form).OfType<Button>().Single(b => b.Text == "Undo last change").Enabled, "online edits participate in Undo");
    }

    private static async Task<int> LiveSearch(string output)
    {
        Directory.CreateDirectory(output);
        using (var service = new CoverSearchService())
        {
            var query = new CoverQuery { Title = "The Shape of Medieval Music to Come", Artist = "Vox Vulgaris",
                Text = "The Shape of Medieval Music to Come Vox Vulgaris" };
            CoverSearchUpdate final = null;
            await service.SearchAsync(query, true, new DirectProgress<CoverSearchUpdate>(u => final = u), CancellationToken.None);
            foreach (string issue in final.Issues) Console.WriteLine("SOURCE NOTE: " + issue);
            foreach (var result in final.Candidates) Console.WriteLine("LIVE RESULT: " + result.Source + " | " + result.Title + " | " + result.Artist);
            if (final.Candidates.Count == 0) throw new Exception("Live lookup returned no usable covers from any source.");
            var image = await service.DownloadAsync(final.Candidates[0], CancellationToken.None);
            using (var bitmap = Artwork.Decode(image)) Console.WriteLine("LIVE COVER: " + bitmap.Width + " x " + bitmap.Height + ", " + image.Length + " bytes.");
            File.WriteAllBytes(Path.Combine(output, "live-cover.jpg"), image);
            Console.WriteLine("PASS live album lookup and high-resolution download.");
            return 0;
        }
    }

    private sealed class DirectProgress<T> : IProgress<T>
    {
        private readonly Action<T> action;
        public DirectProgress(Action<T> action) { this.action = action; }
        public void Report(T value) { action(value); }
    }
    private sealed class FakeTransport : IWebTransport
    {
        private readonly byte[] image;
        public int Count, BandcampRequests;
        public bool Challenge;
        public FakeTransport(byte[] image) { this.image = image; }
        public Task<byte[]> GetAsync(string url, int limit, CancellationToken token)
        {
            Interlocked.Increment(ref Count); token.ThrowIfCancellationRequested();
            if (url.StartsWith("https://bandcamp.com/search"))
            { BandcampRequests++; return Task.FromResult(Encoding.UTF8.GetBytes(Challenge ? "<title>Client Challenge</title>" : BandcampHtml)); }
            if (url.Contains("api.deezer.com")) return Task.FromResult(Encoding.UTF8.GetBytes(DeezerJson));
            if (url.Contains("musicbrainz.org/ws")) return Task.FromResult(Encoding.UTF8.GetBytes(MusicBrainzJson));
            return Task.FromResult(image);
        }
        public void Dispose() { }
    }
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> respond;
        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) { this.respond = respond; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
    private sealed class FakeCoverSearch : ICoverSearch
    {
        private readonly byte[] image;
        public FakeCoverSearch(byte[] image) { this.image = image; }
        public Task SearchAsync(CoverQuery query, bool refresh, IProgress<CoverSearchUpdate> progress, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            progress.Report(new CoverSearchUpdate { Complete = true, Candidates = new List<CoverCandidate> {
                new CoverCandidate { Source = "Bandcamp", Title = query.Title, Artist = query.Artist, MatchScore = 100, Thumbnail = image,
                    ImageUrl = "https://f4.bcbits.com/img/a123_0.jpg", PageUrl = "https://atlas.bandcamp.com/album/morning-miles" },
                new CoverCandidate { Source = "Deezer", Title = query.Title + " (alternate edition)", Artist = query.Artist, MatchScore = 90,
                    Thumbnail = MakeCover(Color.Coral), ImageUrl = "https://cdn-images.dzcdn.net/images/cover/abc/1000x1000.jpg", PageUrl = "https://deezer.com/album/123" }
            } });
            return Task.CompletedTask;
        }
        public Task<byte[]> DownloadAsync(CoverCandidate candidate, CancellationToken token)
        {
            if (candidate.ImageUrl == "invalid") throw new IOException("Simulated image download failure.");
            return Task.FromResult(Artwork.Normalize(MakeCover(Color.MediumPurple)));
        }
        public void Dispose() { }
    }
}
