using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AlbumArtTool;
using AlbumArtTool.Core;
using AlbumArtTool.Management;
using AlbumArtTool.Online;
using AlbumArtTool.Ui;

internal static partial class Tests
{
    // Shapes captured from music.youtube.com (search with the albums filter, and an album page), trimmed to the fields used.
    private const string SearchJson = @"{""contents"":{""tabbedSearchResultsRenderer"":{""tabs"":[{""tabRenderer"":{""content"":{""sectionListRenderer"":{""contents"":[{""musicShelfRenderer"":{""contents"":[
      {""musicResponsiveListItemRenderer"":{""thumbnail"":{""musicThumbnailRenderer"":{""thumbnail"":{""thumbnails"":[{""url"":""https://yt3.googleusercontent.com/abc=w120-h120-l90-rj"",""width"":120},{""url"":""https://yt3.googleusercontent.com/abc=w544-h544-l90-rj"",""width"":544}]}}},
        ""flexColumns"":[{""musicResponsiveListItemFlexColumnRenderer"":{""text"":{""runs"":[{""text"":""My Maudlin Career""}]}}},
                         {""musicResponsiveListItemFlexColumnRenderer"":{""text"":{""runs"":[{""text"":""Album""},{""text"":"" • ""},{""text"":""Camera Obscura""},{""text"":"" • ""},{""text"":""2009""}]}}}],
        ""navigationEndpoint"":{""browseEndpoint"":{""browseId"":""MPREb_LuoRFtkTpbo"",""browseEndpointContextSupportedConfigs"":{""browseEndpointContextMusicConfig"":{""pageType"":""MUSIC_PAGE_TYPE_ALBUM""}}}}}},
      {""musicResponsiveListItemRenderer"":{""flexColumns"":[{""musicResponsiveListItemFlexColumnRenderer"":{""text"":{""runs"":[{""text"":""DONDA 2""}]}}},
                         {""musicResponsiveListItemFlexColumnRenderer"":{""text"":{""runs"":[{""text"":""Album""},{""text"":"" • ""},{""text"":""DONDA""},{""text"":"", ""},{""text"":""Kanye West""},{""text"":"" & ""},{""text"":""Ye""},{""text"":"" • ""},{""text"":""2022""}]}}}],
        ""navigationEndpoint"":{""browseEndpoint"":{""browseId"":""MPREb_u1EPNUwWlZM"",""browseEndpointContextSupportedConfigs"":{""browseEndpointContextMusicConfig"":{""pageType"":""MUSIC_PAGE_TYPE_ALBUM""}}}}}},
      {""musicResponsiveListItemRenderer"":{""flexColumns"":[{""musicResponsiveListItemFlexColumnRenderer"":{""text"":{""runs"":[{""text"":""An artist""}]}}}],
        ""navigationEndpoint"":{""browseEndpoint"":{""browseId"":""UCxyz"",""browseEndpointContextSupportedConfigs"":{""browseEndpointContextMusicConfig"":{""pageType"":""MUSIC_PAGE_TYPE_ARTIST""}}}}}}
    ]}}]}}}}]}}}";

    private static string AlbumJson(params (string Title, string Id, string Length, string Type)[] tracks) =>
        @"{""contents"":{""twoColumnBrowseResultsRenderer"":{""tabs"":[{""tabRenderer"":{""content"":{""sectionListRenderer"":{""contents"":[{""musicResponsiveHeaderRenderer"":{
            ""thumbnail"":{""musicThumbnailRenderer"":{""thumbnail"":{""thumbnails"":[{""url"":""https://yt3.googleusercontent.com/cover=w544-h544-l90-rj"",""width"":544}]}}},
            ""title"":{""runs"":[{""text"":""My Maudlin Career""}]},""subtitle"":{""runs"":[{""text"":""Album""},{""text"":"" • ""},{""text"":""2009""}]},
            ""straplineTextOne"":{""runs"":[{""text"":""Camera Obscura""}]},""secondSubtitle"":{""runs"":[{""text"":""11 songs""}]}}}]}}}}],
          ""secondaryContents"":{""sectionListRenderer"":{""contents"":[{""musicShelfRenderer"":{""contents"":[" +
        string.Join(",", tracks.Select((t, i) => @"{""musicResponsiveListItemRenderer"":{""index"":{""runs"":[{""text"":""" + (i + 1) + @"""}]},
            ""overlay"":{""musicItemThumbnailOverlayRenderer"":{""content"":{""musicPlayButtonRenderer"":{""playNavigationEndpoint"":{""watchEndpoint"":{""videoId"":""" + t.Id + @""",
              ""watchEndpointMusicSupportedConfigs"":{""watchEndpointMusicConfig"":{""musicVideoType"":""MUSIC_VIDEO_TYPE_" + t.Type + @"""}}}}}}}},
            ""flexColumns"":[{""musicResponsiveListItemFlexColumnRenderer"":{""text"":{""runs"":[{""text"":""" + t.Title + @"""}]}}},{""musicResponsiveListItemFlexColumnRenderer"":{""text"":{}}}],
            ""fixedColumns"":[{""musicResponsiveListItemFixedColumnRenderer"":{""text"":{""runs"":[{""text"":""" + t.Length + @"""}]}}}],
            ""playlistItemData"":{""videoId"":""" + t.Id + @"""}}}")) +
        @"]}}]}}}},""microformat"":{""microformatDataRenderer"":{""urlCanonical"":""https://music.youtube.com/playlist?list=OLAK5uy_ndth9Ojy4IN5Zp0s7eI-12XSxAIp1w9Y4""}}}";

    private static void UpgradeTests()
    {
        using (var search = Json.Parse(SearchJson, 512))
        {
            var albums = YouTubeMusic.ParseSearch(search.RootElement);
            Assert(albums.Count == 2 && albums[0].Title == "My Maudlin Career" && albums[0].Artist == "Camera Obscura" && albums[0].Year == "2009" && albums[0].Kind == "Album" &&
                albums[0].BrowseId == "MPREb_LuoRFtkTpbo" && albums[0].ThumbnailUrl.EndsWith("w544-h544-l90-rj"), "YouTube Music album search results are parsed (artists are not albums)");
            Assert(albums[1].Artist == "DONDA, Kanye West & Ye" && albums[1].Year == "2022", "several artists stay one artist credit");
        }
        var album = new YtmAlbum { BrowseId = "MPREb_LuoRFtkTpbo" };
        using (var page = Json.Parse(AlbumJson(("French Navy", "O3CkfvYMCWM", "3:19", "OMV"), ("The Sweetest Thing", "nhSanTYPm2g", "4:23", "ATV"), ("Long One", "t68AO1DIbmw", "4:44:01", "ATV")), 512))
            YouTubeMusic.ParseAlbum(page.RootElement, album);
        Assert(album.Title == "My Maudlin Career" && album.Artist == "Camera Obscura" && album.Year == "2009" && album.PlaylistId == "OLAK5uy_ndth9Ojy4IN5Zp0s7eI-12XSxAIp1w9Y4" && album.TrackCount == 3,
            "album pages give title, artist, year and the playlist");
        Assert(album.Tracks[0].Title == "French Navy" && album.Tracks[0].Duration == TimeSpan.FromSeconds(199) && album.Tracks[0].MusicVideo && !album.Tracks[1].MusicVideo &&
            album.Tracks[2].Duration == new TimeSpan(4, 44, 1) && album.Tracks[1].Index == 2 && album.Tracks[0].Url == "https://music.youtube.com/watch?v=O3CkfvYMCWM",
            "tracks have numbers, lengths (including hours), YouTube Music links and whether they are music videos");

        // Album choice
        var exact = new YtmAlbum { Title = "My Maudlin Career", Artist = "Camera Obscura", TrackCount = 11 };
        var deluxe = new YtmAlbum { Title = "My Maudlin Career (Deluxe Edition)", Artist = "Camera Obscura" };
        var other = new YtmAlbum { Title = "Let's Get Out of This Country", Artist = "Camera Obscura" };
        Assert(AlbumUpgrade.AlbumScore("Camera Obscura", "My Maudlin Career", 11, exact) >= 95 && AlbumUpgrade.AlbumScore("Camera Obscura", "My Maudlin Career", 11, deluxe) >= 90 &&
            AlbumUpgrade.AlbumScore("Camera Obscura", "My Maudlin Career", 11, other) < 60, "albums are scored by title (edition labels ignored), artist and track count");
        Assert(AlbumUpgrade.Query("100 gecs", "Snake Eyes - EP") == "100 gecs Snake Eyes", "the search query is artist and album");

        // Track pairing
        Track T(string title, uint number, double seconds) => new Track { Path = "/m/" + number + ".mp3", Title = title, HasTitleTag = true, Number = number, Duration = TimeSpan.FromSeconds(seconds), Bitrate = 128 };
        var local = new List<Track> { T("French Navy", 1, 198.6), T("The Sweetest Thing (feat. Someone)", 2, 263), T("You Told a Lie", 3, 225), T("Bonus Demo", 12, 100) };
        var remote = new YtmAlbum { Tracks = new List<YtmTrack> {
            new YtmTrack { Index = 1, Title = "French Navy", Duration = TimeSpan.FromSeconds(199), VideoId = "a" },
            new YtmTrack { Index = 2, Title = "The Sweetest Thing", Duration = TimeSpan.FromSeconds(263), VideoId = "b" },
            new YtmTrack { Index = 3, Title = "You Told a Lie", Duration = TimeSpan.FromSeconds(245), VideoId = "c" } } };
        var low = new HashSet<string> { "/m/1.mp3", "/m/3.mp3", "/m/12.mp3" };
        var pairs = AlbumUpgrade.Pair(local, low, remote, t => t.Bitrate);
        Assert(pairs[0].Quality == PairQuality.Exact && pairs[0].Selected && pairs[0].Remote.VideoId == "a", "a low song with the same title and length is matched and checked");
        Assert(pairs[1].Quality == PairQuality.Exact && !pairs[1].Selected, "a song that is not low is matched but left unchecked (featured credits ignored)");
        Assert(pairs[2].Quality == PairQuality.Different && !pairs[2].Selected && Math.Abs(pairs[2].SecondsApart - 20) < 0.01, "a 20 s longer version is shown but never checked automatically");
        Assert(pairs[3].Quality == PairQuality.Missing && pairs[3].Remote == null && !pairs[3].Selected, "songs missing from the YouTube Music album have no match");
        Assert(AlbumUpgrade.TitleSimilarity("Don't Stop Me Now", "Dont Stop Me Now") > 0.9 && AlbumUpgrade.TitleSimilarity("Intro", "Outro") < 0.85, "titles compare tolerantly but not loosely");

        // yt-dlp: YouTube Music only, best audio, no playlists.
        var args = YtDlp.Arguments("https://music.youtube.com/watch?v=O3CkfvYMCWM", "/tmp/x", "/ff/ffmpeg", "/d/deno", null);
        Assert(args.Contains("--no-playlist") && args[args.IndexOf("-f") + 1].StartsWith("bestaudio[acodec=opus]") && args.Contains("--ignore-config") &&
            args[args.IndexOf("--js-runtimes") + 1] == "deno:/d/deno" && args.Last() == "https://music.youtube.com/watch?v=O3CkfvYMCWM", "yt-dlp downloads one track's best (Opus) audio stream");
        Assert(Throws(() => YtDlp.Arguments("https://www.youtube.com/watch?v=O3CkfvYMCWM", "/tmp", null, null, null)) && Throws(() => YtDlp.Arguments("https://music.youtube.com/playlist?list=x", "/tmp", null, null, null)),
            "only YouTube Music track links are accepted");
        Assert(YtDlp.Explain("ERROR: [youtube] x: Sign in to confirm you’re not a bot.").Contains("cookies file") && YtDlpSetup.Allowed("https://github.com/yt-dlp/yt-dlp/releases/download/2026.10.26/yt-dlp.exe") &&
            YtDlpSetup.Allowed("https://github.com/denoland/deno/releases/download/v2.5.0/deno-x86_64-pc-windows-msvc.zip") && !YtDlpSetup.Allowed("https://github.com/someone/else/releases/download/x/yt-dlp.exe") &&
            !YtDlpSetup.Allowed("http://github.com/yt-dlp/yt-dlp/releases/download/x/yt-dlp.exe"), "bot checks are explained and tool downloads only come from the official releases");

        ReplacementRun();
    }

    /// <summary>Makes a stand-in for a YouTube Music download: an AAC file of the given length.</summary>
    internal static Func<string, string, CancellationToken, Action<double>, string> FakeDownload(Func<string, double> seconds, List<string> requested = null) => (url, folder, token, progress) =>
    {
        requested?.Add(url);
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, url.Substring(url.LastIndexOf('=') + 1) + ".m4a");
        Encoder.Run(Encoder.Find(), new[] { "-v", "error", "-nostdin", "-y", "-f", "lavfi", "-i", "sine=frequency=523:duration=" + seconds(url).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-ac", "2", "-c:a", "aac", "-b:a", "160k", path }, token, timeoutSeconds: 120);
        progress?.Invoke(1);
        return path;
    };

    private static void ReplacementRun()
    {
        string ffmpeg = Encoder.Find();
        if (ffmpeg == null) { Console.WriteLine("SKIP replacement run: no FFmpeg"); return; }
        string root = NewFolder("upgrade-run"), folder = Path.Combine(root, "Camera Obscura", "My Maudlin Career"); Directory.CreateDirectory(folder);
        string mp3 = CopyTrack(folder, "French Navy.mp3", "My Maudlin Career", "Camera Obscura"), m4a = CopyTrack(folder, "Away With Murder.m4a", "My Maudlin Career", "Camera Obscura");
        string clash = CopyTrack(folder, "Honey in the Sun.m4a", "My Maudlin Career", "Camera Obscura"); CopyTrack(folder, "Honey in the Sun.mp3", "My Maudlin Career", "Camera Obscura");
        using (var file = TagLib.File.Create(mp3)) { file.Tag.Title = "French Navy"; file.Tag.Track = 1; file.Tag.Pictures = new[] { new TagLib.Picture(new TagLib.ByteVector(Artwork.EmbedCover(Cover))) { Type = TagLib.PictureType.FrontCover, MimeType = "image/jpeg" } }; file.Save(); }
        using (var file = TagLib.File.Create(m4a)) { file.Tag.Title = "Away with Murder"; file.Tag.Track = 4; file.Save(); }
        var scanned = Scanner.Scan(root, CancellationToken.None).Albums.SelectMany(a => a.Tracks).ToList();
        var hashes = new[] { mp3, m4a, clash }.ToDictionary(p => p, CoverEditor.Hash);
        string Remote(Track t) => Path.GetFileNameWithoutExtension(t.Path).Replace(" ", "");
        var pairings = scanned.Where(t => !t.Path.EndsWith("Honey in the Sun.mp3")).Select(t => new TrackPairing { Local = t, Low = true, Selected = true, LocalKbps = 96, Quality = PairQuality.Exact,
            Remote = new YtmTrack { Title = t.Title, VideoId = Remote(t), Duration = TimeSpan.FromSeconds(Math.Round(t.Duration.TotalSeconds)) } }).ToList();
        var seconds = pairings.ToDictionary(p => p.Remote.Url, p => p.Remote.Duration.TotalSeconds);
        var requested = new List<string>();
        var options = Options(root, NewFolder("upgrade-run-backups")); options.Download = FakeDownload(url => seconds[url], requested);
        var plan = AlbumUpgrade.Plan(pairings, options);
        Assert(plan.Actions.Count == 2 && plan.Issues.Single().Contains("Honey in the Sun.mp3 already exists") && plan.Actions.All(a => a.Description.StartsWith("YouTube Music → MP3 320")) &&
            plan.Actions.Single(a => a.Source == m4a).Target == Path.ChangeExtension(m4a, ".mp3"), "replacements keep the name (as .mp3) and never overwrite another file");
        var journal = LibraryRunner.Run(plan, CancellationToken.None);
        Assert(journal.Issues.Count == 0 && journal.Entries.All(e => e.State == "Committed") && requested.Count == 2, "both replacements download and commit: " + string.Join(" | ", journal.Issues));
        using (var file = TagLib.File.Create(mp3))
            Assert(file.Properties.AudioBitrate >= 318 && file.Properties.AudioBitrate <= 322 && file.Tag.Title == "French Navy" && file.Tag.Album == "My Maudlin Career" && file.Tag.FirstPerformer == "Camera Obscura" &&
                file.Tag.Track == 1 && file.Tag.Pictures.Length == 1 && file.Tag.Pictures[0].Data.Data.AsSpan().SequenceEqual(Artwork.EmbedCover(Cover)) && Id3Standard.Verify(file),
                "the new MP3 is 320 kbps with your title, album, artist, track number and cover, as ID3v2.3");
        string converted = Path.ChangeExtension(m4a, ".mp3");
        Assert(!File.Exists(m4a) && File.Exists(converted) && CoverEditor.Hash(clash) == hashes[clash], "a non-MP3 original is replaced by an .mp3 of the same name; the skipped song is untouched");
        using (var file = TagLib.File.Create(converted)) Assert(file.Tag.Title == "Away with Murder" && file.Tag.Track == 4, "tags move across formats too");
        Assert(!Directory.Exists(Path.Combine(options.BackupRoot, ".aat-downloads")) || !Directory.EnumerateFileSystemEntries(Path.Combine(options.BackupRoot, ".aat-downloads")).Any(), "downloads are cleaned up");
        Assert(LibraryRunner.Undo(journal, CancellationToken.None) == 2 && CoverEditor.Hash(mp3) == hashes[mp3] && CoverEditor.Hash(m4a) == hashes[m4a] && !File.Exists(converted), "Undo restores both originals exactly");

        // A download whose length does not match YouTube Music's listing is rejected and the original kept.
        var wrong = AlbumUpgrade.Plan(new[] { pairings[0] }, Options(root, NewFolder("upgrade-wrong-backups")));
        wrong.Options.Download = FakeDownload(url => seconds[url] + 30);
        var failed = LibraryRunner.Run(wrong, CancellationToken.None);
        Assert(failed.Entries.Single().State == "Failed" && failed.Issues.Single().Contains("not the") && CoverEditor.Hash(pairings[0].Local.Path) == hashes[pairings[0].Local.Path],
            "a download of the wrong length is refused and the original kept: " + string.Join(" | ", failed.Issues));
    }

    // Fake YouTube Music for the GUI test: one album whose tracks mirror the local ones.
    internal sealed class FakeYouTubeMusic : IYouTubeMusic
    {
        internal readonly List<string> Queries = new List<string>();
        internal Func<string, List<YtmAlbum>> Results;
        public Task<List<YtmAlbum>> SearchAlbumsAsync(string query, CancellationToken token) { Queries.Add(query); return Task.FromResult(Results(query)); }
        public Task LoadAlbumAsync(YtmAlbum album, CancellationToken token) => Task.CompletedTask;
        public Task<byte[]> ThumbnailAsync(string url, CancellationToken token) => Task.FromResult(MakeCover(SkiaSharp.SKColors.SteelBlue, 120));
        public void Dispose() { }
    }

    private static async Task UpgradeGui()
    {
        if (Encoder.Find() == null) { Console.WriteLine("SKIP upgrade GUI: no FFmpeg"); return; }
        string root = NewFolder("upgrade-gui"), folder = Path.Combine(root, "yeule", "Glitch Princess"); Directory.CreateDirectory(folder);
        var names = new[] { "My Name is Nat Cmiel", "Electric", "Flowers are Dead" };
        for (int i = 0; i < names.Length; i++)
        {
            string path = CopyTrack(folder, names[i] + ".mp3", "Glitch Princess", "yeule");
            using (var file = TagLib.File.Create(path)) { file.Tag.Title = names[i]; file.Tag.Track = (uint)(i + 1); file.Save(); }
        }
        var window = new MainWindow(root, false, new FakeCoverSearch(Cover));
        window.Show();
        try
        {
            await window.ScanAsync();
            var tracks = window.Library.Albums.SelectMany(a => a.Tracks).OrderBy(t => t.Number).ToList();
            var fake = new FakeYouTubeMusic();
            var listing = new YtmAlbum { BrowseId = "MPREb_LDR08NYXGuN", Title = "Glitch Princess", Artist = "yeule", Kind = "Album", Year = "2022", ThumbnailUrl = "https://yt3.googleusercontent.com/x=w544-h544-l90-rj",
                Tracks = tracks.Select((t, i) => new YtmTrack { Index = i + 1, Title = t.Title, VideoId = "v" + i, Duration = TimeSpan.FromSeconds(Math.Round(t.Duration.TotalSeconds) + (i == 2 ? 20 : 0)) }).ToList() };
            listing.TrackCount = listing.Tracks.Count;
            var decoy = new YtmAlbum { BrowseId = "MPREb_other", Title = "softscars", Artist = "yeule", Kind = "Album", Year = "2023", Tracks = new List<YtmTrack>() };
            fake.Results = q => new List<YtmAlbum> { decoy, listing };
            var seconds = listing.Tracks.ToDictionary(t => t.Url, t => t.Duration.TotalSeconds);
            var requested = new List<string>();
            window.YouTubeMusicForTest = fake; window.DownloadForTest = FakeDownload(url => seconds[url], requested);
            var result = LibraryChecks.Run(window.Library.Albums, 320);
            var upgrade = window.ShowUpgrade(result);
            await Settle(); await upgrade.Searching; await Settle();
            var state = upgrade.Albums.Single();
            Assert(fake.Queries.Single() == "yeule Glitch Princess" && ReferenceEquals(state.Chosen, listing), "the album is searched on YouTube Music automatically and the matching release is chosen over another album");
            Assert(state.Pairs.Count == 3 && state.Pairs.Take(2).All(p => p.Selected && p.Quality == PairQuality.Exact) && !state.Pairs[2].Selected && state.Pairs[2].Quality == PairQuality.Different,
                "low songs with an exact match are checked; the one whose length differs is not");
            Assert(state.Status.StartsWith("2 of 3 low songs matched"), "the album shows how many low songs are ready: " + state.Status);
            Assert(Named<Avalonia.Controls.TextBlock>(upgrade, "Selected album").Text == "yeule — Glitch Princess", "the selected album is shown");
            var replaceAlbum = Named<Avalonia.Controls.Button>(upgrade, "Replace checked songs in this album");
            Assert(replaceAlbum.Text() == "Replace 2 songs", "the button says how many songs will be replaced");
            Shot(upgrade, "youtube-music-upgrade.png");
            var before = tracks.ToDictionary(t => t.Path, t => CoverEditor.Hash(t.Path));
            Click(replaceAlbum);
            await Until(() => !upgrade.IsWorking && upgrade.LastJournal != null, "replacement finishes", 60000); await Settle(100);
            Assert(requested.Count == 2 && state.Pairs.Take(2).All(p => p.Replaced) && state.Pairs.Take(2).All(p => CoverEditor.Hash(p.Local.Path) != before[p.Local.Path]) &&
                CoverEditor.Hash(state.Pairs[2].Local.Path) == before[state.Pairs[2].Local.Path], "only the checked songs are downloaded and replaced: " + upgrade.StatusText);
            using (var file = TagLib.File.Create(state.Pairs[0].Local.Path)) Assert(file.Properties.AudioBitrate >= 318 && file.Tag.Title == names[0], "the replaced song is MP3 320 with its own title");
            Assert(upgrade.StatusText.StartsWith("Replaced 2 of 2 songs"), "the result is reported: " + upgrade.StatusText);
            var undo = ButtonStarting(upgrade, "Undo replacements");
            Click(undo);
            await Until(() => !upgrade.IsWorking && upgrade.LastJournal == null, "undo finishes", 30000); await Settle(100);
            Assert(before.All(p => CoverEditor.Hash(p.Key) == p.Value) && state.Pairs.Take(2).All(p => !p.Replaced && p.Selected), "Undo restores the originals and the songs can be replaced again");
            upgrade.Close();
        }
        finally { window.Close(); }
    }

    // Live: real YouTube Music search and album page (CI only; YouTube may change its layout).
    private static async Task<int> LiveYouTubeMusic()
    {
        using (var music = new YouTubeMusic())
        {
            foreach (var (artist, album) in new[] { ("Camera Obscura", "My Maudlin Career"), ("100 gecs", "Snake Eyes"), ("yeule", "Glitch Princess") })
            {
                var found = await music.SearchAlbumsAsync(AlbumUpgrade.Query(artist, album), CancellationToken.None);
                var best = found.OrderByDescending(a => AlbumUpgrade.AlbumScore(artist, album, 0, a)).FirstOrDefault();
                if (best == null) { Console.Error.WriteLine("FAIL: no YouTube Music result for " + album); return 1; }
                await music.LoadAlbumAsync(best, CancellationToken.None);
                var thumb = await music.ThumbnailAsync(best.ThumbnailUrl, CancellationToken.None);
                Console.WriteLine("YTM " + artist + " / " + album + " → " + best.Title + " · " + best.Summary + " · " + best.PlaylistId + " · thumb " + (thumb?.Length ?? 0) + " bytes");
                foreach (var t in best.Tracks.Take(3)) Console.WriteLine("   " + t.Index + ". " + t.Title + " " + t.Duration + (t.MusicVideo ? " (video)" : "") + " " + t.Url);
                if (best.Tracks.Count == 0 || best.Tracks.Any(t => t.Duration <= TimeSpan.Zero || t.VideoId.Length != 11) || AlbumUpgrade.AlbumScore(artist, album, 0, best) < 80)
                { Console.Error.WriteLine("FAIL: unexpected album data for " + album); return 1; }
            }
        }
        Console.WriteLine("PASS live YouTube Music search and album pages");
        return 0;
    }

    // Live: download yt-dlp and Deno from GitHub, verify checksums and that both start (Windows CI only).
    private static async Task<int> LiveYtDlpSetup()
    {
        using (var setup = new YtDlpSetup())
        {
            string exe = await setup.InstallAsync(true, new Progress<int>(_ => { }), CancellationToken.None);
            string deno = YtDlp.FindDeno(exe);
            string version = YtDlp.Version(exe, CancellationToken.None);
            string denoVersion = "";
            YtDlp.Run(deno, new[] { "--version" }, CancellationToken.None, line => { if (denoVersion.Length == 0) denoVersion = line; }, 60);
            Console.WriteLine("yt-dlp " + version + " · " + denoVersion + " · tag " + YtDlpSetup.InstalledTag);
            if (version.Length == 0 || !denoVersion.StartsWith("deno ")) { Console.Error.WriteLine("FAIL: tools did not start"); return 1; }
            int exit = YtDlp.Run(exe, new[] { "--ignore-config", "--simulate", "--no-playlist", "-f", "bestaudio", "--print", "%(format_id)s %(abr)s %(acodec)s", "https://music.youtube.com/watch?v=O3CkfvYMCWM" },
                CancellationToken.None, line => Console.WriteLine("   yt-dlp: " + line), 180);
            Console.WriteLine("format probe exit " + exit + " (YouTube often blocks data-center machines; that is not a failure here)");
        }
        Console.WriteLine("PASS live yt-dlp and Deno setup");
        return 0;
    }
}
