using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using AlbumArtTool;
using AlbumArtTool.Core;
using AlbumArtTool.Online;
using AlbumArtTool.Ui;

// The README screenshots: the app on a library built by tools/showcase/library.py, in the Silver and Black finishes.
//   AlbumArtTool.Tests.dll --showcase <library folder> <covers folder> <output folder>
internal static partial class Tests
{
    private sealed class ShowcaseAlbum
    {
        public string Artist { get; set; }
        public string Title { get; set; }
        public string Cover { get; set; }
        public string Source { get; set; }
    }

    // Online results for the screenshots: the album's real cover, as the cover search would find it.
    private sealed class ShowcaseCoverSearch : ICoverSearch
    {
        private readonly List<ShowcaseAlbum> albums;
        internal ShowcaseCoverSearch(List<ShowcaseAlbum> albums) { this.albums = albums; }
        private ShowcaseAlbum Find(string artist, string title) => albums.FirstOrDefault(a => string.Equals(a.Title, title, StringComparison.OrdinalIgnoreCase) && string.Equals(a.Artist, artist, StringComparison.OrdinalIgnoreCase));
        public Task SearchAsync(CoverQuery query, bool refresh, IProgress<CoverSearchUpdate> progress, CancellationToken token)
        {
            var album = Find(query.Artist, query.Title);
            var found = new List<CoverCandidate>();
            if (album != null)
            {
                byte[] image = File.ReadAllBytes(album.Cover), thumbnail = Artwork.Thumbnail(image);
                var size = Artwork.ReadInfo(image);
                found.Add(new CoverCandidate { Source = album.Source, Title = album.Title, Artist = album.Artist, MatchScore = 100, Thumbnail = thumbnail, ImageUrl = album.Cover,
                    ImageWidth = size.Width, ImageHeight = size.Height, SizeChecked = true, PageUrl = "https://www.deezer.com/" });
                found.Add(new CoverCandidate { Source = "MusicBrainz", Title = album.Title, Artist = album.Artist, MatchScore = 96, Thumbnail = thumbnail, ImageUrl = album.Cover,
                    ImageWidth = size.Width, ImageHeight = size.Height, SizeChecked = true, PageUrl = "https://musicbrainz.org/" });
            }
            progress.Report(new CoverSearchUpdate { Complete = true, Candidates = found });
            return Task.CompletedTask;
        }
        public Task<byte[]> DownloadAsync(CoverCandidate candidate, CancellationToken token) => Task.FromResult(Artwork.Normalize(File.ReadAllBytes(candidate.ImageUrl)));
        public void Dispose() { }
    }

    private static int Showcase(string library, string covers, string folder)
    {
        var albums = JsonSerializer.Deserialize<List<ShowcaseAlbum>>(File.ReadAllText(Path.Combine(covers, "albums.json")), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Directory.CreateDirectory(folder);
        using (var session = HeadlessUnitTestSession.StartNew(typeof(Tests)))
            return session.Dispatch<int>(async () =>
            {
                MainWindow.PageFade = false; Motion.Enabled = false; Chrome.Enabled = true; Zoom.SettingsFile = null;
                var window = new MainWindow(library, false, new ShowcaseCoverSearch(albums));
                window.Show();
                try
                {
                    await window.ScanAsync();
                    foreach (var (finish, dark) in new[] { ("silver", false), ("black", true) })
                    {
                        Appearance.SetDark(dark, persist: false); await Settle(100);
                        var list = window.Albums;
                        async Task Pick(string title)
                        {
                            for (int i = 0; i < list.AlbumCount; i++)
                                if (string.Equals(list.AlbumAt(i).Title, title, StringComparison.OrdinalIgnoreCase)) { list.Select(i); break; }
                            await Settle(150); await window.Online.CurrentSearch; await Settle(450);
                        }
                        // Albums: every album, one with its cover and its colors washed behind the details.
                        window.Navigate(MainWindow.Page.Albums);
                        Click(ButtonStarting(window, "All ("));
                        await Pick("SECRETS");
                        Shot(window, "albums-" + finish + ".png");
                        // Missing art: a new cover found online, ready to apply.
                        Click(ButtonStarting(window, "Missing art ("));
                        await Pick("Saisons");
                        var saisons = albums.FirstOrDefault(a => a.Title == "Saisons");
                        if (saisons != null) { window.StageImage(saisons.Cover); await Settle(300); }
                        Shot(window, "new-cover-" + finish + ".png");
                        window.DiscardStaged();
                        // Artists: guest credits to collapse into the album's main artist.
                        window.Navigate(MainWindow.Page.Artists); await Settle(200);
                        await Pick("Romeo");
                        Shot(window, "artists-" + finish + ".png");
                        // Health: what needs attention, with the low-bitrate album measured.
                        window.Navigate(MainWindow.Page.Health); await window.Health.Verification; await Settle(200);
                        var low = Controls(window).OfType<Button>().FirstOrDefault(b => b.AccessibleName() == "Health check Low bitrate");
                        if (low != null) Click(low);
                        await Settle(200); await window.Health.Verification; await Settle(300);
                        Shot(window, "health-" + finish + ".png");
                        window.Navigate(MainWindow.Page.Organize); await Settle(300);
                        var preset = Controls(window).OfType<Button>().FirstOrDefault(b => b.IsEffectivelyVisible && b.Text() == "Full clean-up");
                        if (preset != null) { Click(preset); await Settle(200); }
                        Shot(window, "organize-" + finish + ".png");
                        window.Navigate(MainWindow.Page.Settings); await Settle(200);
                        Shot(window, "settings-" + finish + ".png");
                    }
                    return 0;
                }
                finally { window.Close(); }
            }, CancellationToken.None).GetAwaiter().GetResult();
    }
}
