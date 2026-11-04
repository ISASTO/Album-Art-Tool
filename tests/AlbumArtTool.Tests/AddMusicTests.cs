using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using AlbumArtTool;
using AlbumArtTool.Core;
using AlbumArtTool.Management;
using AlbumArtTool.Online;
using AlbumArtTool.Ui;

internal static partial class Tests
{
    // ---- YouTube Music artist pages (shaped like music.youtube.com's responses) ----

    private static string Runs(params string[] texts) => "{\"runs\":[" + string.Join(",", texts.Select(t => "{\"text\":" + JsonSerializer.Serialize(t) + "}")) + "]}";
    private static string Thumb(string url) => "{\"musicThumbnailRenderer\":{\"thumbnail\":{\"thumbnails\":[{\"url\":\"" + url + "\",\"width\":226},{\"url\":\"" + url + "-big\",\"width\":544}]}}}";
    private static string Tile(string id, string title, params string[] subtitle) =>
        "{\"musicTwoRowItemRenderer\":{\"thumbnailRenderer\":" + Thumb("https://lh3.googleusercontent.com/" + id) + ",\"title\":" + Runs(title) + ",\"subtitle\":" + Runs(subtitle) +
        ",\"navigationEndpoint\":{\"browseEndpoint\":{\"browseId\":\"" + id + "\"}}}}";
    private static string Shelf(string title, string moreId, string moreParams, params string[] tiles) =>
        "{\"musicCarouselShelfRenderer\":{\"header\":{\"musicCarouselShelfBasicHeaderRenderer\":{\"title\":{\"runs\":[{\"text\":\"" + title + "\"" +
        (moreId == null ? "" : ",\"navigationEndpoint\":{\"browseEndpoint\":{\"browseId\":\"" + moreId + "\",\"params\":\"" + moreParams + "\"}}") + "}]}}},\"contents\":[" + string.Join(",", tiles) + "]}}";

    private static void AddMusicParsingTests()
    {
        string search = "{\"contents\":{\"tabbedSearchResultsRenderer\":{\"tabs\":[{\"tabRenderer\":{\"content\":{\"sectionListRenderer\":{\"contents\":[{\"musicShelfRenderer\":{\"contents\":[" +
            "{\"musicResponsiveListItemRenderer\":{\"thumbnail\":" + Thumb("https://lh3.googleusercontent.com/yeule") + ",\"flexColumns\":[{\"musicResponsiveListItemFlexColumnRenderer\":{\"text\":" + Runs("yeule") + "}}," +
            "{\"musicResponsiveListItemFlexColumnRenderer\":{\"text\":" + Runs("Artist", " • ", "412K monthly audience") + "}}],\"navigationEndpoint\":{\"browseEndpoint\":{\"browseId\":\"UCyeule\",\"browseEndpointContextSupportedConfigs\":{\"browseEndpointContextMusicConfig\":{\"pageType\":\"MUSIC_PAGE_TYPE_ARTIST\"}}}}}}," +
            "{\"musicResponsiveListItemRenderer\":{\"flexColumns\":[{\"musicResponsiveListItemFlexColumnRenderer\":{\"text\":" + Runs("A playlist") + "}}],\"navigationEndpoint\":{\"browseEndpoint\":{\"browseId\":\"VLPL123\"}}}}" +
            "]}}]}}}}]}}}";
        using (var document = JsonDocument.Parse(search))
        {
            var artists = YouTubeMusic.ParseArtists(document.RootElement);
            Assert(artists.Count == 1 && artists[0].Name == "yeule" && artists[0].BrowseId == "UCyeule" && artists[0].Subtitle == "Artist · 412K monthly audience" && artists[0].ThumbnailUrl.EndsWith("-big"),
                "artist search keeps artist pages only, with name, audience and the largest picture");
        }
        string page = "{\"header\":{\"musicImmersiveHeaderRenderer\":{\"title\":" + Runs("yeule") + ",\"thumbnail\":" + Thumb("https://lh3.googleusercontent.com/face") + "}}," +
            "\"contents\":{\"singleColumnBrowseResultsRenderer\":{\"tabs\":[{\"tabRenderer\":{\"content\":{\"sectionListRenderer\":{\"contents\":[" +
            Shelf("Albums", "MPADUCyeule", "albums", Tile("MPREb_softscars", "softscars", "2023"), Tile("MPREb_glitch", "Glitch Princess", "Album", " • ", "2022")) + "," +
            Shelf("Singles & EPs", null, null, Tile("MPREb_eyes", "Eyes", "EP", " • ", "2024"), Tile("MPREb_dazies", "Dazies", "2025")) + "," +
            Shelf("Videos", null, null, "{\"musicTwoRowItemRenderer\":{\"title\":" + Runs("A video") + ",\"navigationEndpoint\":{\"watchEndpoint\":{\"videoId\":\"abc\"}}}}") + "," +
            Shelf("Fans might also like", null, null, Tile("UCother", "Someone else")) +
            "]}}}}]}}}";
        var artist = new YtmArtist { BrowseId = "UCyeule", Name = "Yeule" };
        using (var document = JsonDocument.Parse(page))
        {
            var more = YouTubeMusic.ParseArtistPage(document.RootElement, artist);
            Assert(artist.Name == "yeule" && artist.ThumbnailUrl.Contains("face") && artist.Releases.Count == 4, "the artist page lists the albums, singles and EPs, not videos or other artists");
            Assert(artist.Releases.Select(r => r.Kind + " " + r.Title + " " + r.Year).SequenceEqual(new[] { "Album softscars 2023", "Album Glitch Princess 2022", "EP Eyes 2024", "Single Dazies 2025" }) && artist.Releases.All(r => r.Artist == "yeule"),
                "each release knows whether it is an album, an EP or a single, and its year");
            Assert(more.Count == 1 && more[0] == ("Album", "MPADUCyeule", "albums"), "a shelf's More page is loaded for the full list");
        }
        string grid = "{\"contents\":{\"singleColumnBrowseResultsRenderer\":{\"tabs\":[{\"tabRenderer\":{\"content\":{\"sectionListRenderer\":{\"contents\":[{\"gridRenderer\":{\"items\":[" +
            Tile("MPREb_softscars", "softscars", "2023") + "," + Tile("MPREb_serotonin", "Serotonin II", "2019") + "]}}]}}}}]}}}";
        using (var document = JsonDocument.Parse(grid))
        {
            var all = YouTubeMusic.ParseReleases(document.RootElement, "Album", "yeule");
            Assert(all.Count == 2 && all[1].Title == "Serotonin II" && all[1].Kind == "Album" && all[1].Year == "2019", "the More page's grid gives the remaining albums");
        }
        Assert(YouTubeMusic.ShelfKind("Singles & EPs") == "Single" && YouTubeMusic.ShelfKind("Albums") == "Album" && YouTubeMusic.ShelfKind("Videos") == null && YouTubeMusic.ShelfKind("Featured on") == null,
            "only album, single and EP shelves are read");
        var releases = new[] { new YtmAlbum { Kind = "Album" }, new YtmAlbum { Kind = "EP" }, new YtmAlbum { Kind = "Single" } };
        Assert(AddMusicPage.Shown(releases, "All").Count() == 3 && AddMusicPage.Shown(releases, "Album").Count() == 1 && AddMusicPage.Shown(releases, "Single").Count() == 2, "release filters: all, albums, singles and EPs");

        // Names, paths and tags
        Assert(AddMusic.SafeName("AC/DC: Live? <2024>.", "x") == "AC-DC- Live  2024".Replace("  ", " ") && AddMusic.SafeName("CON", "x") == "_CON" && AddMusic.SafeName("...", "fallback") == "fallback", "file names are safe on Windows");
        string staging = NewFolder("new-music-names");
        var info = new NewTrack { Title = "Bloodbunny", Album = "Glitch Princess", AlbumArtist = "yeule", Number = 3 };
        string first = AddMusic.TargetPath(staging, info);
        Assert(first == Path.Combine(staging, "yeule", "Glitch Princess", "03 - Bloodbunny.mp3"), "new songs go to New music \\ Artist \\ Album \\ NN - Title.mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(first)); File.WriteAllText(first, "x");
        Assert(AddMusic.TargetPath(staging, info).EndsWith("03 - Bloodbunny (2).mp3"), "a name already taken is kept with (2)");
        Assert(AddMusic.LargeCover("https://lh3.googleusercontent.com/abc=w544-h544-l90-rj") == "https://lh3.googleusercontent.com/abc=w1200-h1200-l90-rj" && AddMusic.LargeCover("https://x/y") == "https://x/y", "covers are fetched at their largest size");
        Assert(AddMusic.SplitArtists("Charli xcx, Lorde & Troye Sivan").SequenceEqual(new[] { "Charli xcx", "Lorde", "Troye Sivan" }), "several credited artists become separate artist tags");
        var described = AddMusic.Describe(new YtmAlbum { Title = "Glitch Princess", Artist = "yeule", Year = "2022", TrackCount = 12 }, new YtmTrack { Index = 2, Title = "Electric", Artists = "yeule & Danny L Harle", Duration = TimeSpan.FromSeconds(201) });
        Assert(described.AlbumArtist == "yeule" && described.Artists.Length == 2 && described.Number == 2 && described.Count == 12 && described.Year == "2022" && described.Seconds == 201, "album and track details become the song's tags");
    }

    // ---- Saving, merging and the download queue (real audio, so FFmpeg is needed) ----

    private static void AddMusicSaveTests()
    {
        string ffmpeg = Encoder.Find();
        if (ffmpeg == null) { Console.WriteLine("SKIP add music saving: no FFmpeg"); return; }
        string source = Path.Combine(NewFolder("add-music-source"), "stream.m4a");
        Encoder.Run(ffmpeg, new[] { "-v", "error", "-nostdin", "-y", "-f", "lavfi", "-i", "sine=frequency=440:duration=4", "-ac", "2", "-c:a", "aac", "-b:a", "128k", source }, CancellationToken.None, timeoutSeconds: 60);
        string folder = NewFolder("add-music-save");
        var track = new NewTrack { Title = "Pocky Boy", Album = "Glitch Princess", AlbumArtist = "yeule", Artists = new[] { "yeule" }, Number = 4, Count = 12, Year = "2022", Seconds = 4 };
        string target = AddMusic.TargetPath(folder, track);
        AddMusic.Save(ffmpeg, source, target, track, MakeCover(SkiaSharp.SKColors.HotPink, 1400), CancellationToken.None);
        using (var file = TagLib.File.Create(target))
            Assert(file.Properties.AudioBitrate >= 318 && file.Properties.AudioBitrate <= 322 && file.Tag.Title == "Pocky Boy" && file.Tag.Album == "Glitch Princess" && file.Tag.FirstAlbumArtist == "yeule" &&
                file.Tag.Track == 4 && file.Tag.TrackCount == 12 && file.Tag.Year == 2022 && file.Tag.Pictures.Length == 1 && file.Tag.Pictures[0].Type == TagLib.PictureType.FrontCover &&
                Size(file.Tag.Pictures[0].Data.Data).Width == Artwork.EmbedSide && Id3Standard.Verify(file), "a new song is MP3 320 with its tags and one 600 px front cover, as ID3v2.3");
        Assert(!Directory.EnumerateFiles(folder, ".aat-*", SearchOption.AllDirectories).Any(), "no temporary files are left behind");
        var wrong = new NewTrack { Title = "Too long", Album = "A", AlbumArtist = "B", Seconds = 60 };
        bool refused = false;
        try { AddMusic.Save(ffmpeg, source, AddMusic.TargetPath(folder, wrong), wrong, null, CancellationToken.None); } catch (IOException) { refused = true; }
        Assert(refused && !File.Exists(Path.Combine(folder, "B", "A", "Too long.mp3")), "a download of the wrong length is refused");

        // Merging New music into the library
        string library = NewFolder("add-music-library"), staging = NewFolder("add-music-staging");
        Directory.CreateDirectory(Path.Combine(library, "yeule", "Glitch Princess"));
        string staged = Path.Combine(staging, "yeule", "Glitch Princess", "04 - Pocky Boy.mp3"); Directory.CreateDirectory(Path.GetDirectoryName(staged)); File.Copy(target, staged);
        string same = Path.Combine(staging, "yeule", "Glitch Princess", "05 - Same.mp3"); File.Copy(target, same);
        File.Copy(target, Path.Combine(library, "yeule", "Glitch Princess", "05 - Same.mp3"));
        string clash = Path.Combine(staging, "yeule", "Glitch Princess", "06 - Clash.mp3"); File.Copy(target, clash);
        File.WriteAllText(Path.Combine(library, "yeule", "Glitch Princess", "06 - Clash.mp3"), "a different file");
        var plan = LibraryMerge.Plan(staging, library);
        Assert(plan.Count == 3 && plan.Single(m => m.Source == same).AlreadyInLibrary && plan.Single(m => m.Source == clash).Target.EndsWith("06 - Clash (2).mp3") &&
            plan.Single(m => m.Source == staged).Target == Path.Combine(library, "yeule", "Glitch Princess", "04 - Pocky Boy.mp3"), "merging keeps Artist \\ Album, never overwrites, and skips identical songs");
        bool inside = false;
        try { LibraryMerge.Plan(Path.Combine(library, "yeule"), library); } catch (IOException) { inside = true; }
        Assert(inside, "New music inside the music folder is refused");
        Assert(LibraryMerge.Run(plan, staging, library, CancellationToken.None) == 2 && File.Exists(Path.Combine(library, "yeule", "Glitch Princess", "06 - Clash (2).mp3")) &&
            File.ReadAllText(Path.Combine(library, "yeule", "Glitch Princess", "06 - Clash.mp3")) == "a different file" && File.Exists(same) && !File.Exists(staged), "the new songs move into the library");
        Assert(LibraryMerge.CanUndo && LibraryMerge.Undo() == 2 && File.Exists(staged) && File.Exists(clash) && !File.Exists(Path.Combine(library, "yeule", "Glitch Princess", "04 - Pocky Boy.mp3")) &&
            File.Exists(Path.Combine(library, "yeule", "Glitch Princess", "05 - Same.mp3")) && !LibraryMerge.CanUndo, "Undo moves the added songs back to New music");

        // The queue: albums download in order; music-video-only songs are skipped unless asked for.
        string queueFolder = NewFolder("add-music-queue");
        var requested = new List<string>();
        var queue = new DownloadQueue(() => queueFolder, () => ffmpeg, FakeDownload(url => 3, requested), (url, token) => Task.FromResult(MakeCover(SkiaSharp.SKColors.Teal, 800)));
        var album = new YtmAlbum { BrowseId = "MPREb_a", Title = "First album", Artist = "Test Artist", Year = "2020", TrackCount = 3, ThumbnailUrl = "https://lh3.googleusercontent.com/a=w544-h544" };
        album.Tracks = new List<YtmTrack> { new YtmTrack { Index = 1, Title = "One", VideoId = "v1aaaaaaaaa", Duration = TimeSpan.FromSeconds(3) }, new YtmTrack { Index = 2, Title = "Two", VideoId = "v2aaaaaaaaa", Duration = TimeSpan.FromSeconds(3), VideoOnly = true },
            new YtmTrack { Index = 3, Title = "Three", VideoId = "v3aaaaaaaaa", Duration = TimeSpan.FromSeconds(3) } };
        queue.Limit = () => 2;
        var group = queue.Add(album.BrowseId, album.Title, "Album", album.ThumbnailUrl, null);
        Assert(queue.Contains("MPREb_a") && group.Status == "Finding the songs…", "an added album shows at once, while its songs are looked up");
        queue.Ready(group, album.Tracks.Select(t => new DownloadItem { Album = album, Track = t }));
        var single = queue.Add("MPREb_a#v2aaaaaaaaa", "Two", "", album.ThumbnailUrl, null);
        queue.Ready(single, new[] { new DownloadItem { Album = album, Track = album.Tracks[1], FromVideo = true } });
        Assert(queue.IsRunning && queue.Waiting == 3 && queue.Active == 2, "downloads start as soon as songs are added, two at a time here");
        queue.Completion.GetAwaiter().GetResult();
        Assert(group.Done == 2 && group.Skipped == 1 && group.Status == "2 songs ready · 1 music video only, skipped" && single.Done == 1, "a ticked music-video-only song is downloaded; otherwise it is skipped: " + group.Status + " / " + string.Join(", ", group.Items.Select(i => i.Error)));
        Assert(queue.MostAtOnce == 2 && !queue.IsRunning && requested.Take(2).OrderBy(u => u).SequenceEqual(new[] { album.Tracks[0].Url, album.Tracks[2].Url }.OrderBy(u => u)) && AddMusic.Waiting(queueFolder).Count == 3 &&
            File.Exists(Path.Combine(queueFolder, "Test Artist", "First album", "03 - Three.mp3")), "songs download in order into New music \\ Artist \\ Album");
        using (var file = TagLib.File.Create(Path.Combine(queueFolder, "Test Artist", "First album", "01 - One.mp3")))
            Assert(file.Tag.Title == "One" && file.Tag.TrackCount == 3 && file.Tag.Pictures.Length == 1, "queued songs get their album's tags and cover");
        queue.ClearFinished();
        Assert(queue.Groups.Count == 0 && AddMusic.Waiting(queueFolder).Count == 3, "clearing finished items keeps their songs");
    }

    // ---- The page ----

    private static async Task AddMusicGui()
    {
        if (Encoder.Find() == null) { Console.WriteLine("SKIP add music GUI: no FFmpeg"); return; }
        string root = NewFolder("add-music-gui");
        CopyTrack(Directory.CreateDirectory(Path.Combine(root, "Someone", "Older")).FullName, "one.mp3", "Older", "Someone");
        var fake = new FakeYouTubeMusic();
        YtmAlbum Album(string id, string title, string kind, string year, params (string Title, bool VideoOnly)[] tracks) => new YtmAlbum
        {
            BrowseId = id, Title = title, Artist = "yeule", Kind = kind, Year = year, ThumbnailUrl = "https://lh3.googleusercontent.com/" + id + "=w544-h544", TrackCount = tracks.Length,
            Tracks = tracks.Select((t, i) => new YtmTrack { Index = i + 1, Title = t.Title, Artists = "yeule", VideoId = (id.Substring(6, 4) + i + "xxxxxxxxxxx").Substring(0, 11), Duration = TimeSpan.FromSeconds(2 + i), VideoOnly = t.VideoOnly }).ToList()
        };
        var glitch = Album("MPREb_glitch", "Glitch Princess", "Album", "2022", ("My Name is Nat Cmiel", false), ("Electric", false), ("Bites on My Neck", true));
        var softscars = Album("MPREb_soft", "softscars", "Album", "2023", ("x w x", false), ("software update", false));
        var eyes = Album("MPREb_eyes", "Eyes", "EP", "2024", ("Eyes", false));
        var artist = new YtmArtist { BrowseId = "UCyeule", Name = "yeule", Subtitle = "Artist · 412K monthly audience", ThumbnailUrl = "https://lh3.googleusercontent.com/face" };
        fake.Artists = q => new List<YtmArtist> { artist };
        fake.Results = q => new List<YtmAlbum> { glitch, softscars };
        fake.Songs = q => new List<YtmTrack> { new YtmTrack { Title = "Electric", Artists = "yeule", VideoId = glitch.Tracks[1].VideoId, AlbumBrowseId = glitch.BrowseId, AlbumTitle = "Glitch Princess", Duration = TimeSpan.FromSeconds(3) } };
        fake.FillArtist = a => a.Releases ??= new List<YtmAlbum> { softscars, glitch, eyes };
        var requested = new List<string>();
        var window = new MainWindow(root, false, new FakeCoverSearch(Cover)) { YouTubeMusicForTest = fake, DownloadForTest = FakeDownload(url => 3, requested) };
        window.Show();
        try
        {
            await window.ScanAsync();
            Click(Named<Button>(window, "Add music"));
            var page = window.AddMusicView;
            Assert(window.CurrentPage == MainWindow.Page.AddMusic && Named<TextBlock>(page, "Add music title").Text.StartsWith("Find music"), "Add music opens on an inviting empty search");
            Named<TextBox>(page, "Search YouTube Music").Text = "yeule";
            await page.SearchAsync("yeule"); await Settle();
            Assert(page.Results.Artists.Count == 1 && page.Results.Albums.Count == 2 && page.Results.Songs.Count == 1 && Controls(page).OfType<Border>().Any(b => b.AccessibleName() == "yeule artist"),
                "a search shows artists, albums and songs together");
            Shot(window, "add-music-search.png");
            await page.OpenArtistAsync(artist); await Settle();
            Assert(Named<TextBlock>(page, "Artist name").Text == "yeule" && page.Cards.Count() == 3, "the artist page lists every release");
            Click(Named<Button>(page, "Show Single"));
            Assert(page.Filter == "Single" && page.Cards.Count() == 1, "Singles & EPs shows only those");
            Click(Named<Button>(page, "Show All"));
            Shot(window, "add-music-artist.png");
            Click(Named<Button>(page, "Add softscars"));
            Assert(page.Queue.Contains("MPREb_soft") && !Named<Button>(page, "Add softscars").IsEnabled, "one click adds a whole album and the card shows it is added");
            await page.OpenReleaseAsync(glitch); await Settle();
            Assert(Named<TextBlock>(page, "Release title").Text == "Glitch Princess" && Named<TextBlock>(page, "Audio availability").Text.StartsWith("1 song is only on YouTube Music as a music video"),
                "a release shows when a song only exists as a music video");
            Shot(window, "add-music-release.png");
            Click(Named<Button>(page, "Add this release"));
            await Until(() => !page.Queue.IsRunning && page.Queue.Groups.All(g => g.Finished), "the queued albums download one after another", 60000);
            await Settle(100);
            var waiting = AddMusic.Waiting(AddMusic.Folder);
            Assert(waiting.Count == 4 && !requested.Contains(glitch.Tracks[2].Url) && Named<TextBlock>(page, "Songs in New music").Text == "4 songs ready to add",
                "both albums are in New music, without the music-video-only song: " + string.Join(", ", page.Queue.Groups.Select(g => g.Status)) + " · " + waiting.Count + " files · " + Named<TextBlock>(page, "Songs in New music").Text);
            Assert(window.Badge(MainWindow.Page.AddMusic) == "", "the sidebar count clears when downloads finish");
            Shot(window, "add-music-queue.png");
            Click(Named<Button>(page, "Tidy up in Organize"));
            Assert(window.CurrentPage == MainWindow.Page.Organize && window.Organize.Root == AddMusic.Folder && Named<Button>(window, "Organize New music").Classes.Contains("selected") &&
                Named<TextBlock>(window, "Page title").Text == "Organize" && Named<TextBlock>(window, "Organize folder").Text.Length > 0, "Tidy up opens Organize on New music, and says so");
            Shot(window, "organize-new-music.png");
            window.Navigate(MainWindow.Page.Albums); window.Navigate(MainWindow.Page.Organize);
            Assert(window.Organize.Root == AddMusic.Folder, "Organize stays on New music when you come back to it");
            Click(Named<Button>(window, "Organize the music folder"));
            Assert(window.Organize.Root == root && Named<Button>(window, "Organize the music folder").Classes.Contains("selected"), "Organize can switch to the music folder");
            Click(Named<Button>(window, "Organize New music"));
            Assert(window.Organize.Root == AddMusic.Folder, "and back to New music");
            window.Navigate(MainWindow.Page.AddMusic);
            await page.MergeAsync();
            await Until(() => window.Library.Albums.Any(a => a.Title == "Glitch Princess"), "the added albums appear in the library", 15000);
            Assert(File.Exists(Path.Combine(root, "yeule", "softscars", "01 - x w x.mp3")) && AddMusic.Waiting(AddMusic.Folder).Count == 0 && Named<TextBlock>(page, "Add to library status").Text.StartsWith("4 files added"),
                "Add to library moves New music into the music folder and rescans");
            await page.UndoMergeAsync();
            Assert(AddMusic.Waiting(AddMusic.Folder).Count == 4 && !File.Exists(Path.Combine(root, "yeule", "softscars", "01 - x w x.mp3")), "the last add can be undone");
        }
        finally { window.Close(); }
    }
}
