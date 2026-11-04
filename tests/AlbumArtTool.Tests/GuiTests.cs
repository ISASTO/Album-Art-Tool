using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using AlbumArtTool;
using AlbumArtTool.Core;
using AlbumArtTool.Management;
using AlbumArtTool.Online;
using AlbumArtTool.Ui;
using AlbumArtTool.Updates;
using SkiaSharp;

// Every window runs on Avalonia's headless platform with real Skia rendering,
// so the same checks run on Windows and Linux CI and can save screenshots.
internal static partial class Tests
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    private static void GuiAll()
    {
        using (var session = HeadlessUnitTestSession.StartNew(typeof(Tests)))
            session.Dispatch(async () =>
            {
                await Gui("main window and online covers", MainWindowGui);
                await Gui("queue", QueueGui);
                await Gui("track artists and duplicates", LibraryToolsGui);
                await Gui("tag checks", TagCheckGui);
                await Gui("library manager", ManagerGui);
                await Gui("album cleanup manager", AlbumCleanupGui);
                await Gui("update handoff", UpdateHandoffGui);
                await Gui("YouTube Music replacement", UpgradeGui);
                return 0;
            }, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static async Task Gui(string name, Func<Task> test)
    {
        stage = "GUI " + name; var clock = Stopwatch.StartNew();
        await test();
        Console.WriteLine("PASS GUI " + name + " (" + clock.ElapsedMilliseconds + " ms)");
    }

    private static IEnumerable<Control> Controls(Control root) => root.GetLogicalDescendants().OfType<Control>();
    private static Button ButtonStarting(Control root, string text) => Controls(root).OfType<Button>().Single(b => b.Text().StartsWith(text, StringComparison.Ordinal));
    private static T Named<T>(Control root, string name) where T : Control => Controls(root).OfType<T>().Single(c => c.AccessibleName() == name);
    private static void Click(Button button) { Assert(button.IsEnabled, "button is enabled before clicking: " + button.Text()); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }

    private static async Task Settle(int milliseconds = 30)
    {
        await Task.Delay(milliseconds);
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task Until(Func<bool> condition, string message, int timeout = 8000)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.ElapsedMilliseconds > timeout) Assert(false, message + " (timed out)");
            await Settle(20);
        }
        Assert(true, message);
    }

    private static void Shot(Window window, string name)
    {
        try { AvaloniaHeadlessPlatform.ForceRenderTimerTick(); window.CaptureRenderedFrame()?.Save(Path.Combine(output, name)); }
        catch (Exception e) { Console.WriteLine("Screenshot " + name + " skipped: " + e.Message); }
    }

    private static async Task MainWindowGui()
    {
        string root = NewFolder("gui");
        string first = Path.Combine(root, "Morning Miles"), second = Path.Combine(root, "Quiet Hours"), third = Path.Combine(root, "The Long Way Home");
        CopyTrack(Directory.CreateDirectory(first).FullName, "one.mp3", "Morning Miles", "Atlas Sessions"); CopyTrack(first, "two.mp3", "Morning Miles", "Atlas Sessions");
        CopyTrack(Directory.CreateDirectory(second).FullName, "one.flac", "Quiet Hours", "Paper Lanterns");
        CopyTrack(Directory.CreateDirectory(third).FullName, "one.m4a", "The Long Way Home", "Sunday Club");
        Editor.Apply(ScanAlbum(third), MakeCover(SKColors.DarkSalmon), false, true);
        string imagePath = Path.Combine(workspace, "cover.jpg"); File.WriteAllBytes(imagePath, Cover);
        var window = new MainWindow(root, false, new FakeCoverSearch(Cover));
        window.Show();
        try
        {
            await UpdateGui(window);
            await ScanningGui(window);
            await window.ScanAsync();
            var list = window.Albums;
            Assert(Icons.All.All(icon => Kit.Glyph(icon).Data != null), "every icon's vector path parses");
            Assert(list.AlbumCount == 2, "missing tab lists only incomplete albums");
            Assert(ButtonStarting(window, "Missing art (").Text() == "Missing art (2)" && ButtonStarting(window, "Has art (").Text() == "Has art (1)" && ButtonStarting(window, "All (").Text() == "All (3)", "view chips count their albums");
            Click(ButtonStarting(window, "Has art"));
            Assert(list.AlbumCount == 1 && list.SelectedAlbum.Thumbnail != null, "existing tab shows cover thumbnails");
            Click(ButtonStarting(window, "All ("));
            Assert(list.AlbumCount == 3 && ButtonStarting(window, "All (").Classes.Contains("selected"), "All lists every album and is highlighted");
            Click(ButtonStarting(window, "Missing art"));
            Assert(AlbumList.SingleImagePath(imagePath) == imagePath && AlbumList.SingleImagePath(Path.Combine(root, "Morning Miles", "one.mp3")) == null, "drops accept a single image only");
            var search = Named<TextBox>(window, "Search albums, artists or folders");
            search.Text = "quiet"; await Settle(200);
            Assert(list.AlbumCount == 1 && list.SelectedAlbum.Title == "Quiet Hours", "search filters albums by title, artist and folder");
            search.Text = ""; await Settle(200);
            Assert(list.AlbumCount == 2, "clearing the search restores every album in the tab");
            window.StageImage(imagePath);
            var apply = ButtonStarting(window, "Apply cover");
            Assert(apply.IsEnabled && window.HasStagedCover, "a dropped/chosen image is staged and enables Apply");
            Assert(Named<TextBlock>(window, "Operation status and track counter").Text.StartsWith("Review the cover"), "staging explains how to apply or discard");
            Assert(!window.CanInstallUpdate, "a staged cover prevents an update restart");
            Shot(window, "album-art-tool.png");
            await ThemeGui(window);
            await window.ApplyAsync(); await window.QueueCompletion;
            Assert(list.AlbumCount == 1, "completed album disappears from the missing tab");
            Click(ButtonStarting(window, "Has art"));
            Assert(list.AlbumCount == 2, "completed album appears in the existing tab");
            await OnlineGui(window, list);
        }
        finally { window.Close(); }
    }

    private static async Task ThemeGui(MainWindow window)
    {
        bool dark = Appearance.IsDark;
        var surface = Appearance.Brush("Surface");
        Assert(Appearance.SetDark(!dark, animate: true), "appearance preference is saved");
        Assert(Appearance.IsDark == !dark && !ReferenceEquals(surface, Appearance.Brush("Surface")), "switching appearance swaps every themed brush");
        Assert(AppSettings.ReadDark() == !dark, "the saved appearance is read back on the next start");
        Assert(window.HasStagedCover && window.IsVisible, "switching appearance keeps open windows and a staged cover");
        await Until(() => Appearance.TransitionsRunning == 0, "the appearance wipe finishes and removes its overlay", 2000);
        Shot(window, dark ? "light-mode.png" : "dark-mode.png");
        Assert(Appearance.SetMode(ThemeMode.System) && Appearance.Mode == ThemeMode.System && AppSettings.ReadTheme() == ThemeMode.System, "Match Windows can be chosen and is saved");
        var settings = window.ShowSettings(); await Settle();
        Click(Named<Button>(settings, "Appearance: Light"));
        await Until(() => Appearance.TransitionsRunning == 0, "the settings appearance change finishes", 2000);
        Assert(!Appearance.IsDark && Appearance.Mode == ThemeMode.Light && Named<Button>(settings, "Appearance: Light").Classes.Contains("selected") &&
            !Named<Button>(settings, "Appearance: Match Windows").Classes.Contains("selected"), "Settings switches the appearance and shows the choice");
        Assert(ReferenceEquals(window.ShowSettings(), settings) && window.CurrentPage == MainWindow.Page.Settings, "Settings is one page, reused");
        Shot(window, "settings.png");
        window.Navigate(MainWindow.Page.Albums);
        Appearance.SetDark(dark);
        Assert(Appearance.IsDark == dark && AppSettings.ReadDark() == dark, "switching back restores the original appearance");
    }

    private static async Task ScanningGui(MainWindow window)
    {
        window.SetBusy(true, false);
        var reporter = window.Reporter();
        var bar = Named<ProgressBar>(window, "Scan and update progress");
        Assert(bar.IsVisible && bar.IsIndeterminate, "discovery uses an indeterminate progress bar");
        reporter.Report(new ProgressInfo { Completed = 2751, Total = 3867, Message = "2751/3867 tracks scanned · Music" });
        await Until(() => !bar.IsIndeterminate, "scan progress becomes determinate");
        Assert(bar.Value == 71 && Named<TextBlock>(window, "Progress percentage").Text == "71%", "the scan bar displays checked files as a percentage");
        Assert(Named<TextBlock>(window, "Operation status and track counter").Text.StartsWith("2751/3867"), "numeric tracker is shown during scanning");
        Assert(Controls(window).OfType<TextBlock>().Any(l => l.IsVisible && (l.Text ?? "").StartsWith("Scanning your music")), "scanning does not show a false no-files message");
        Assert(Controls(window).OfType<CheckBox>().All(c => !c.IsEnabled), "options stay noninteractive during scanning");
        Assert(!window.CanInstallUpdate, "active scan prevents an update restart");
        Shot(window, "scanning-progress.png");
        window.SetBusy(false, false);
    }

    private static async Task OnlineGui(MainWindow window, AlbumList albums)
    {
        var panel = window.Online;
        await Until(() => panel.DisplayedCandidates.Count == 2, "selecting an album automatically displays online suggestions");
        Assert(panel.Cards.Count() == 2 && panel.Cards.All(c => c.Resolution.Text == "600 × 600 px"), "source resolutions are visible on the suggested cover cards");
        var selected = albums.SelectedAlbum;
        string before = CoverEditor.Hash(selected.Tracks[0].Path);
        await window.ApplyOnlineCoverAsync("another-album", panel.DisplayedCandidates[0]);
        Assert(CoverEditor.Hash(selected.Tracks[0].Path) == before, "a stale artwork result cannot edit a different album");
        string opened = null; panel.OpenUrl = url => opened = url;
        Named<TextBox>(window, "Online artwork search query").Text = "query & more";
        Click(Controls(panel).OfType<Button>().Single(b => b.Text() == "Bandcamp ↗"));
        Assert(opened == "https://bandcamp.com/search?q=query%20%26%20more&item_type=a", "the Bandcamp button opens the same search in the browser");
        Shot(window, "online-cover-search.png");
        await window.ApplyOnlineCoverAsync(selected.Key, new CoverCandidate { Source = "Broken source", ImageUrl = "invalid" });
        await window.QueueCompletion;
        Assert(CoverEditor.Hash(selected.Tracks[0].Path) == before, "failed image downloads leave album files unchanged");
        await Until(() => panel.Cards.Any(), "results are shown again after the refresh");
        Click(panel.Cards.First().UseButton); await panel.CurrentApply; await window.QueueCompletion;
        Assert(CoverEditor.Hash(selected.Tracks[0].Path) != before, "one click on a result applies its artwork");
        Assert(Named<Button>(window, "Undo last change").IsEnabled, "online edits participate in Undo");
    }

    private static async Task QueueGui()
    {
        string root = NewFolder("queue-gui");
        string[] names = { "A Folder cover", "B Local cover", "C Online cover", "D Preparing", "E Cancelled" };
        var paths = names.Select(name => Path.Combine(root, name)).ToArray();
        foreach (string path in paths) { Directory.CreateDirectory(path); CopyTrack(path, "one.mp3", Path.GetFileName(path)); }
        CopyTrack(paths[0], "two.mp3", names[0]);
        string sourceCover = Path.Combine(paths[0], "cover.jpg"); File.WriteAllBytes(sourceCover, Cover);
        string localImage = Path.Combine(root, "local.jpg"), nextImage = Path.Combine(root, "next.jpg");
        File.WriteAllBytes(localImage, MakeCover(SKColors.Coral)); File.WriteAllBytes(nextImage, MakeCover(SKColors.MediumPurple));
        var service = new GatedCoverSearch();
        var window = new MainWindow(root, false, service);
        window.Show();
        try
        {
            await window.ScanAsync();
            var list = window.Albums;
            var apply = ButtonStarting(window, "Apply cover");
            var sidecar = Controls(window).OfType<CheckBox>().Single(c => c.Content as string == "Update folder cover images too");
            var online = window.ApplyOnlineCoverAsync(SelectAt(list, 2).Key, new CoverCandidate { Source = "Test source", ImageUrl = "gated" });
            await service.Started.Task;
            Assert(!online.IsCompleted && list.IsEnabled, "album list stays interactive during a pending cover download");
            Assert(ButtonStarting(window, "Has art").IsEnabled, "tabs stay interactive while artwork is being applied");
            SelectAt(list, 0);
            Assert(apply.IsEnabled && apply.Text() == "Apply cover to 2 tracks", "a detected folder cover can be applied without choosing or dragging an image");
            sidecar.IsChecked = false;
            var folderJob = window.ApplyAsync();
            SelectAt(list, 1); window.StageImage(localImage);
            var localJob = window.ApplyAsync();
            Assert(window.QueuedCount == 3 && !folderJob.IsCompleted && !localJob.IsCompleted, "multiple albums queue immediately behind active work");
            Assert(window.Badge(MainWindow.Page.Activity) == "3", "the Activity page shows how much work is queued");
            window.Navigate(MainWindow.Page.Activity);
            Assert(Named<ListBox>(window, "Queued albums and recent results").ItemCount >= 3 && ButtonStarting(window, "Clear waiting jobs").IsEnabled, "Activity lists the queue and can clear waiting jobs");
            Shot(window, "activity.png");
            window.Navigate(MainWindow.Page.Albums);
            await window.ApplyOnlineCoverAsync(SelectAt(list, 2).Key, new CoverCandidate { Source = "Duplicate", ImageUrl = "gated" });
            Assert(window.QueuedCount == 3, "duplicate clicks cannot queue the same album twice");
            SelectAt(list, 3); window.StageImage(nextImage);
            sidecar.IsChecked = true;
            Shot(window, "artwork-queue.png");
            Assert(!window.CanInstallUpdate, "active artwork queue prevents an update restart");
            service.Release.TrySetResult(true);
            await Task.WhenAll(online, folderJob, localJob); await window.QueueCompletion;
            Assert(list.SelectedAlbum.Title == names[3] && window.HasStagedCover && apply.IsEnabled, "completed jobs preserve another album's selection and staged cover");
            foreach (string track in Directory.GetFiles(paths[0], "*.mp3"))
                using (var audio = TagLib.File.Create(track)) Assert(Size(Artwork.GetCover(audio.Tag)).Width == 600, "folder-cover shortcut embeds the full image, not the scan thumbnail");
            Assert(File.ReadAllBytes(sourceCover).SequenceEqual(Cover), "queued checkbox settings do not change when the user changes options on another album");
            using (var audio = TagLib.File.Create(Path.Combine(paths[1], "one.mp3")))
                Assert(Artwork.GetCover(audio.Tag).SequenceEqual(Artwork.EmbedCover(Artwork.Normalize(File.ReadAllBytes(localImage)))), "queued local cover remains bound to its original album and image");
            Assert(ScanAlbum(paths[2]).MissingCount == 0 && ScanAlbum(paths[3]).MissingCount == 1, "online work updates its captured target and leaves unqueued albums alone");
            Assert(Named<Button>(window, "Undo last change").IsEnabled, "Undo becomes available when the queue finishes");

            // A failed download must not stop a later local-cover job.
            var failed = window.ApplyOnlineCoverAsync(list.SelectedAlbum.Key, new CoverCandidate { Source = "Broken", ImageUrl = "invalid" });
            SelectAt(list, 1); window.StageImage(localImage); var following = window.ApplyAsync();
            await Task.WhenAll(failed, following); await window.QueueCompletion;
            Assert(ScanAlbum(paths[3]).MissingCount == 1 && ScanAlbum(paths[4]).MissingCount == 0, "failed queue jobs leave their files unchanged and later jobs continue");

            // Clear waiting work while an active job is paused, without cancelling the active edit.
            Click(ButtonStarting(window, "Has art"));
            service.ResetGate();
            var active = window.ApplyOnlineCoverAsync(SelectAt(list, 0).Key, new CoverCandidate { Source = "Test source", ImageUrl = "gated" });
            await service.Started.Task;
            SelectAt(list, 1); window.StageImage(nextImage); string unchanged = CoverEditor.Hash(list.SelectedAlbum.Tracks[0].Path);
            var waiting = window.ApplyAsync(); window.ClearWaitingJobs();
            Assert(window.QueuedCount == 1 && waiting.IsCompleted && !active.IsCompleted, "clearing the waiting queue preserves the active job");
            service.Release.TrySetResult(true); await active; await window.QueueCompletion;
            Assert(CoverEditor.Hash(list.SelectedAlbum.Tracks[0].Path) == unchanged, "cancelled waiting jobs do not change their target files");
        }
        finally { service.Release.TrySetResult(true); window.ClearWaitingJobs(); await window.QueueCompletion; window.Close(); }
    }

    private static Album SelectAt(AlbumList list, int index) { list.Select(index); return list.SelectedAlbum; }

    private static async Task LibraryToolsGui()
    {
        string root = NewFolder("library-tools-gui"), first = Path.Combine(root, "BENEE"), second = Path.Combine(root, "Clean album"), copy = Path.Combine(root, "BENEE copy");
        Directory.CreateDirectory(first); Directory.CreateDirectory(second); Directory.CreateDirectory(copy);
        string one = CopyTrack(first, "one.mp3", "Hey u x", "BENEE"), two = CopyTrack(first, "two.mp3", "Hey u x", "BENEE");
        SetArtists(one, new[] { "BENEE" }, new[] { "BENEE" }, "Happen to Me");
        SetArtists(two, new[] { "BENEE", "Featured Artist" }, new[] { "BENEE" }, "A collaboration");
        File.Copy(one, Path.Combine(copy, "one.mp3")); File.Copy(two, Path.Combine(copy, "two.mp3"));
        SetArtists(Path.Combine(copy, "one.mp3"), new[] { "BENEE" }, new[] { "BENEE" }, "A different opener");
        Editor.Apply(ScanAlbum(first), Cover, false, false);
        string cleanFile = CopyTrack(second, "one.flac", "A clean album", "Another Artist"); SetArtists(cleanFile, new[] { "Another Artist" }, new string[0]);
        var service = new GatedCoverSearch();
        var window = new MainWindow(root, false, service);
        window.Show();
        try
        {
            await window.ScanAsync();
            var list = window.Albums;
            Click(Named<Button>(window, "Artists"));
            Assert(list.AlbumCount == 2, "artist cleanup tab finds eligible albums regardless of whether they have covers");
            var filter = Named<ComboBox>(window, "Album filter"); var sort = Named<ComboBox>(window, "Album sort order");
            Click(ButtonStarting(window, "Has art ("));
            Assert(filter.SelectedIndex == 0 && list.AlbumCount > 0, "the existing-art tab starts on All albums instead of inheriting artist cleanup");
            Click(Named<Button>(window, "Artists"));
            Click(ButtonStarting(window, "Missing art ("));
            Assert(filter.SelectedIndex == 0, "the missing-art tab also resets the inherited artist filter");
            Click(Named<Button>(window, "Artists"));
            filter.SelectedIndex = 0; Assert(list.AlbumCount == 3, "all albums can be reviewed including manually specified artist names");
            sort.SelectedIndex = 2;
            Assert(list.AlbumAt(0).ArtistChangeCount > 0 && list.AlbumAt(2).ArtistChangeCount == 0, "artist changes first sort promotes eligible albums");
            filter.SelectedIndex = 1;
            SelectAt(list, 0);
            var name = Named<TextBox>(window, "Main artist for this album");
            Assert(name.IsEffectivelyVisible && name.Text == "BENEE", "suggested main artist is editable and visible");
            var heading = Named<TextBlock>(window, "Selected album for artist cleanup");
            bool blanked = false;
            heading.PropertyChanged += (s, e) => { if (e.Property == TextBlock.TextProperty && heading.Text == "Select an album") blanked = true; };
            for (int n = 0; n < 12; n++) SelectAt(list, n % 2);
            SelectAt(list, 0); await Settle();
            Assert(!blanked && name.Text == "BENEE", "switching artist albums does not expose an empty intermediate details pane");
            Assert(window.ArtistRows.Count(r => r.After == "Unchanged") == 1 && window.ArtistRows.Count(r => r.After == "BENEE") == 1,
                "artist preview distinguishes unchanged tracks from the actual proposed credits");
            Assert(ButtonStarting(window, "Set artists on 1 track").IsEnabled, "the apply button names the number of changed tracks");
            name.Text = "Various artists"; await Settle();
            Assert(window.ArtistRows.All(r => r.After == "Enter an artist") && !ButtonStarting(window, "Set track artists").IsEnabled, "a generic name cannot be applied");
            name.Text = "BENEE";
            ProcessStartInfo opened = null; window.StartExplorer = info => opened = info;
            var albumFolder = Named<Button>(window, "Open artist-tab album folder");
            var showTrack = Named<Button>(window, "Reveal artist-tab selected track");
            Assert(albumFolder.IsEnabled && !showTrack.IsEnabled, "artist tab offers album folder access and waits for a selected track before revealing a file");
            Click(albumFolder); Assert(opened.FileName == list.SelectedAlbum.Folder && opened.UseShellExecute, "artist-tab album button opens the currently selected album folder");
            Named<ListBox>(window, "Current track artists").SelectedIndex = 1; Click(showTrack);
            Assert(opened.FileName == "explorer.exe" && opened.Arguments == "/select," + UpdateInstaller.QuoteArgument(window.ArtistRows[1].Track.Path), "selected-track action reveals that exact file in Explorer with a quoted path");
            Shot(window, "track-artists.png");
            string firstKey = list.SelectedAlbum.Key;
            // A paused artwork job shares the same queue with artist edits.
            var artwork = window.ApplyOnlineCoverAsync(firstKey, new CoverCandidate { Source = "Test", ImageUrl = "gated" });
            await service.Started.Task;
            SelectAt(list, 1); name.Text = "BENEE";
            string target = list.SelectedAlbum.Folder;
            var artist = window.ApplyArtistAsync();
            Assert(!artist.IsCompleted && window.QueuedCount == 2 && list.IsEnabled, "artist jobs queue behind artwork while browsing stays responsive");
            name.Text = "An edit after enqueueing";
            service.Release.TrySetResult(true); await Task.WhenAll(artwork, artist); await window.QueueCompletion;
            Assert(ScanAlbum(target).Tracks.All(t => ArtistRules.Single(t.Performers, "BENEE")), "queued artist job keeps the captured name and album");
            Assert(list.AlbumCount == 1, "completed cleanup disappears from the eligible-album filter");
            Click(Named<Button>(window, "Undo last change"));
            await Until(() => list.AlbumCount == 2, "Undo returns the album to the cleanup filter");
            Assert(ScanAlbum(target).ArtistChangeCount == 1, "Undo restores guest credits");
            await window.FindDuplicatesAsync();
            var dialog = window.Duplicates;
            Assert(window.CurrentPage == MainWindow.Page.Health && window.Health.ShowingDuplicates, "Find duplicates opens Health on the duplicates view");
            Assert(dialog.Result.Songs.Any(g => g.Confirmed) && dialog.Result.Albums.Any(g => g.Confirmed), "duplicate window shows confirmed songs and album copies");
            Assert(dialog.Nodes.Any(n => n.Text.StartsWith("Confirmed album copies") && n.Children.All(c => c.Path != null)), "duplicate groups list the folders to open");
            Shot(window, "duplicates.png");
        }
        finally { service.Release.TrySetResult(true); window.ClearWaitingJobs(); await window.QueueCompletion; window.Close(); }
    }

    private static async Task TagCheckGui()
    {
        string root = LibraryCheckFolder("library-checks-gui");
        var window = new MainWindow(root, false, new FakeCoverSearch(Cover));
        window.Show();
        try
        {
            var button = Named<Button>(window, "Health");
            Assert(!button.IsEnabled, "tag checks wait for a library scan");
            await window.ScanAsync();
            Assert(!Named<Button>(window, "Show issues").IsVisible, "the issues button only appears when there are issues");
            Click(button); await Settle();
            var dialog = window.Health;
            Assert(window.CurrentPage == MainWindow.Page.Health && dialog.IsVisible, "the Health page opens with both checks");
            Assert(dialog.Result.MissingTitles.Count == 2 && dialog.Result.MultiplePictures.Count == 2, "the tag check window opens with both checks");
            Assert(Named<TextBlock>(dialog, "Tag check summary").Text.StartsWith("2 missing a Title tag · 2 with several embedded pictures (1 with several covers)"), "the summary counts each finding");
            Assert(dialog.Rows.Count(r => r.Header) == 3 && dialog.Rows.Count(r => r.Path != null) == 4 + dialog.Result.LowBitrateAlbums().Count, "results are grouped under the three checks");
            var threshold = Named<ComboBox>(dialog, "Low bitrate threshold");
            threshold.SelectedIndex = Array.IndexOf(LibraryChecks.BitrateChoices, 320);
            Assert(dialog.Result.LowBitrateLimit == 320 && dialog.Result.LowBitrate.Count == 4, "changing the threshold rechecks low-bitrate songs");
            await dialog.Verification; await Settle();
            var albumRows = dialog.Rows.Where(r => r.Folder).ToArray();
            Assert(dialog.Result.Verified == (AlbumArtTool.Management.Encoder.Find() != null) && albumRows.Length == dialog.Result.LowBitrateAlbums().Count && albumRows.Length == 1 &&
                albumRows[0].Detail.StartsWith("4 songs · ") && albumRows[0].Path == root, "low-bitrate songs are measured and grouped into one row per album: " + string.Join(" | ", albumRows.Select(a => a.Text + " / " + a.Detail)));
            ProcessStartInfo opened = null; window.StartExplorer = info => opened = info;
            var show = ButtonStarting(dialog, "Show selected");
            Assert(!show.IsEnabled, "a song must be selected before it can be shown");
            int index = dialog.Rows.ToList().FindIndex(r => r.Path != null && r.Path.EndsWith("05 - two covers.mp3"));
            dialog.Select(index); Click(show);
            Assert(opened.FileName == "explorer.exe" && opened.Arguments == "/select," + UpdateInstaller.QuoteArgument(dialog.Rows[index].Path), "the selected song is highlighted in File Explorer");
            var fill = Named<Button>(dialog, "Fill titles from filenames");
            Assert(fill.IsEnabled, "songs missing titles can be filled from their filenames");
            Click(fill);
            OrganizePage manager = null;
            for (int i = 0; i < 300 && (manager == null || manager.IsWorking || manager.CurrentPlan == null); i++)
            { await Settle(30); manager = window.CurrentPage == MainWindow.Page.Organize ? window.Organize : null; }
            var filled = manager.CurrentPlan.Actions.Where(a => a.TitleFromName).OrderBy(a => a.NewTitle).Select(a => a.NewTitle).ToArray();
            Assert(filled.SequenceEqual(new[] { "blank", "no title" }) && manager.CurrentPlan.Actions.Where(a => a.HasWork).All(a => a.TitleFromName && !a.Convert && !a.Square && !a.Archive && !a.Move),
                "the fill button previews only filename titles for the songs missing one");
            Assert(Named<CheckBox>(manager, "Fill missing Title tags from filenames").IsChecked == true && Controls(manager.OptionsPanel).OfType<CheckBox>().Count(c => c.IsChecked == true) == 1,
                "the manager opens with just the title fill selected");
            window.Navigate(MainWindow.Page.Health); await Settle();
            Click(Named<Button>(dialog, "Fix embedded pictures"));
            manager = null;
            for (int i = 0; i < 300 && (manager == null || manager.IsWorking || manager.CurrentPlan == null); i++)
            { await Settle(30); manager = window.CurrentPage == MainWindow.Page.Organize ? window.Organize : null; }
            var fixes = manager.CurrentPlan.Actions.Where(a => a.HasWork).ToArray();
            Assert(fixes.Count(a => a.SinglePicture) == dialog.Result.MultiplePictures.Count && fixes.All(a => (a.SinglePicture || a.Shrink) && a.NewTitle == null && !a.Convert && !a.Move),
                "the picture button previews keeping one picture for exactly the songs with several");
            window.Navigate(MainWindow.Page.Health); await Settle();
            SetFilenameTitle(Path.Combine(root, "03 - no title.mp3"), "Now titled");
            await window.ScanAsync();
            Click(ButtonStarting(dialog, "Check again"));
            Assert(dialog.Result.MissingTitles.Count == 1, "checking again uses the latest scan");
            Shot(window, "tag-checks.png");
            Assert(ReferenceEquals(window.CheckTags(), dialog), "Health is one page, reused");
        }
        finally { window.Close(); }
    }

    private static async Task UpdateGui(MainWindow window)
    {
        var settings = window.ShowSettings(); await Settle();
        Assert(Named<Button>(settings, "Check for updates").IsEnabled && Named<Button>(window, "Settings").IsVisible, "updates are checked from Settings, opened from the sidebar");
        window.Navigate(MainWindow.Page.Albums);
        window.CreateUpdateService = () => new UpdateService(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ReleaseJson("v1.0.0", new byte[32])) }));
        await window.GetUpdatesAsync();
        var dialog = window.UpdateWindow;
        Assert(Named<TextBlock>(dialog, "App update status").Text.StartsWith("You are up to date."), "update button displays current-version feedback");
        Shot(dialog, "app-updates.png");
        dialog.Close();
        Assert(window.CanInstallUpdate && window.UpdateWindow == null, "idle app can hand off an update");
        window.CreateUpdateService = () => new UpdateService(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        await window.GetUpdatesAsync();
        dialog = window.UpdateWindow;
        Assert(window.IsEnabled && Named<TextBlock>(dialog, "App update status").Text.StartsWith("Update could not finish."), "offline update error keeps the app usable");
        dialog.Close();
        var gate = new UpdateGateHandler();
        window.CreateUpdateService = () => new UpdateService(gate);
        var checking = window.GetUpdatesAsync();
        await Settle();
        Assert(!checking.IsCompleted && window.IsEnabled, "update request runs without blocking album browsing");
        window.UpdateWindow.Close();
        await checking;
        Assert(gate.Cancelled && window.IsEnabled, "closing the update window cancels the active network request");
    }

    private static async Task UpdateHandoffGui()
    {
        string root = NewFolder("update app & 100% élite") + Path.DirectorySeparatorChar;
        byte[] installer = File.ReadAllBytes(InstallerStandIn);
        var window = new MainWindow(root, false, new FakeCoverSearch(Cover)) { InstalledVersion = new Version(2, 5, 2) };
        string launchedFolder = null, launchedDirectory = null;
        window.LaunchInstaller = (directory, folder) => { launchedDirectory = directory; launchedFolder = folder; return null; };
        window.CreateUpdateService = () => new UpdateService(new FakeHandler(request => request.RequestUri.Host == "api.github.com"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ReleaseJson("v" + UpdateService.DisplayVersion, installer)) }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(installer) }));
        bool closed = false; window.Closed += (s, e) => closed = true;
        CopyTrack(root, "one.mp3", "Handoff album");
        window.Show();
        await window.ScanAsync();
        window.StageImage(Path.Combine(workspace, "cover.jpg"));
        Assert(window.HasStagedCover, "a cover is staged before the update finishes downloading");
        var update = window.GetUpdatesAsync();
        await Until(() => Named<TextBlock>(window.UpdateWindow, "App update status").Text.StartsWith("Update downloaded. Apply or discard"), "a downloaded update waits for a staged cover");
        Assert(launchedDirectory == null && window.IsEnabled, "nothing is installed while work is staged");
        window.DiscardStaged();
        await update;
        Assert(launchedFolder == root && UpdateService.Hash(Path.Combine(launchedDirectory, UpdateInstaller.SetupFile)) == UpdateService.Hash(InstallerStandIn),
            "the verified installer is started with the current music folder");
        Assert(closed, "the app closes so the installer can replace it");
    }

    private sealed class UpdateGateHandler : HttpMessageHandler
    {
        internal bool Cancelled;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            try { await Task.Delay(30000, token); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }
    }

    private static async Task ManagerGui()
    {
        string root = NewFolder("manager-gui"), backups = NewFolder("manager-gui-backups"), folder = Path.Combine(root, "BENEE", "BENEE", "2020", "Hey u x"); Directory.CreateDirectory(folder);
        string file = CopyTrack(folder, "01 - Happen to Me.flac", "Hey u x", "BENEE"); AddRectangleArt(file); SetFilenameTitle(file, "Happen to Me");
        File.Copy(file, Path.Combine(folder, "01 - Happen to Me (1).flac"));
        var form = new OrganizePage(root, () => true, _ => { }, _ => Task.CompletedTask);
        var host = Host(form);
        try
        {
            BackupSettings.Root = backups;
            var options = Controls(form.OptionsPanel).OfType<CheckBox>().ToArray();
            Assert(options.Length == 12 && options.All(c => c.IsChecked == false) && !ButtonStarting(form, "Preview changes").IsEnabled, "Organize starts with nothing selected and nothing to preview");
            var chips = Controls(form).OfType<Button>().Where(b => b.Classes.Contains("chip")).ToArray();
            Assert(chips.Select(b => b.Text()).SequenceEqual(new[] { "Tidy tags", "Standardize artwork", "Full clean-up" }), "the built-in presets are offered");
            Click(Named<Button>(form, "Preset Tidy tags"));
            Assert(form.SelectedOptions.SequenceEqual(new[] { OrganizePresets.TitleFromName, OrganizePresets.CleanTitles, OrganizePresets.Id3v23 }) && Named<Button>(form, "Preset Tidy tags").Classes.Contains("selected"),
                "a preset selects exactly its options and is highlighted");
            Assert(!Named<Button>(form, "Save as preset").IsEnabled, "a built-in preset is already saved");
            Named<CheckBox>(form, "Combine multi-disc albums").IsChecked = true;
            Assert(!Named<Button>(form, "Preset Tidy tags").Classes.Contains("selected") && Named<Button>(form, "Save as preset").IsEnabled, "changing an option clears the highlight and offers saving");
            Click(Named<Button>(form, "Save as preset"));
            Named<TextBox>(form, "Preset name").Text = "  My\tphone ";
            Click(ButtonStarting(form, "Save preset"));
            Assert(form.CustomPresets.Single().Name == "My phone" && Named<Button>(form, "Preset My phone").Classes.Contains("selected") && Named<Button>(form, "Delete preset").IsVisible,
                "the selected options are saved as a custom preset");
            Assert(!form.SaveCurrentAsPreset("Full clean-up") && form.CustomPresets.Count == 1, "built-in preset names are reserved");
            Assert(OrganizePresets.LoadCustom().Single().Options.SetEquals(form.SelectedOptions), "custom presets are kept on this PC");
            Click(Named<Button>(form, "Preset Standardize artwork"));
            Assert(form.SelectedOptions.SequenceEqual(new[] { OrganizePresets.Square, OrganizePresets.Shrink }) && !Named<Button>(form, "Delete preset").IsVisible, "presets switch the selection");
            Click(Named<Button>(form, "Preset My phone"));
            Click(Named<Button>(form, "Delete preset"));
            Assert(form.CustomPresets.Count == 0 && OrganizePresets.LoadCustom().Count == 0 && form.SelectedOptions.Count() == 4, "a custom preset can be deleted, keeping the selection");
            foreach (var box in options) box.IsChecked = false;
            foreach (string name in new[] { "Convert lossless to MP3 320", "Crop artwork to squares", "Remove confirmed duplicates", "Organize into Artist / Album folders", "Move old tool backups off the player" })
                Named<CheckBox>(form, name).IsChecked = true;
            form.SetWorkingForTest(true);
            var reporter = form.Reporter();
            var checks = Controls(form.OptionsPanel).OfType<CheckBox>().ToArray();
            for (int n = 1; n <= 8; n++) reporter.Report(new ProgressInfo { Completed = n * 300, Total = 6589, Message = n * 300 + "/6589 tracks scanned · Artist/Album " + n });
            await Settle(60);
            Assert(checks.Length == 12 && checks.All(c => !c.IsEnabled), "options are locked while work runs");
            var tipped = Controls(form).Where(c => c is CheckBox || c is Button || c is TextBox).ToArray();
            Assert(tipped.Length >= 26 && tipped.All(c => ToolTip.GetTip(c) is string tip && tip.Length >= 40),
                "every option, button and field in Organize has detailed mouse-over text: " + string.Join(", ", tipped.Where(c => !(ToolTip.GetTip(c) is string t && t.Length >= 40)).Select(c => c.GetType().Name + " " + (c as ContentControl)?.Content)));
            Assert(Named<TextBlock>(form, "Library management status").Text.StartsWith("2400/6589") && Named<ListBox>(form, "Planned library changes").IsEnabled,
                "progress updates the status while the preview stays scrollable");
            form.SetWorkingForTest(false);
            await form.PreviewAsync();
            Assert(form.CurrentPlan != null && form.CurrentPlan.Actions.Any(a => a.Convert) && form.CurrentPlan.Actions.Any(a => a.Keeper != null), "manager previews conversion and duplicate removal together");
            var rename = Named<CheckBox>(form, "Rename song files from Title tags");
            Assert(rename.IsChecked == false && form.CurrentPlan.Actions.Where(a => !a.Archive).All(a => !a.Rename), "filename cleanup is an explicit optional setting");
            rename.IsChecked = true;
            Assert(form.CurrentPlan == null && form.Rows.Count == 0, "changing filename cleanup invalidates the previous preview");
            await form.PreviewAsync();
            Assert(form.CurrentPlan.Actions.Any(a => a.Rename && a.Convert && a.Target.EndsWith("Happen to Me.mp3")), "manager previews Title filenames together with conversion, cropping, duplicates and folder organization");
            Assert(ButtonStarting(form, "Apply selected changes").IsEnabled && !form.IsWorking, "manager exposes an explicit reviewable Apply action after preview");
            Shot(host, "library-manager.png");
            form.SetWorkingForTest(true);
            bool checkedBefore = form.Rows[0].IsChecked; form.SetRowChecked(0, !checkedBefore);
            Assert(form.Rows[0].IsChecked == checkedBefore, "running management keeps preview rows readable while locking selected changes");
            form.SetWorkingForTest(false);
            await form.ApplyAsync();
            Assert(File.Exists(Path.Combine(root, "BENEE", "Hey u x", "Happen to Me.mp3")), "manager applies conversion, canonical organization and the metadata filename");
            Click(ButtonStarting(form, "Preview Undo"));
            Assert(form.Rows.Count > 0 && form.Rows.All(r => r.IsChecked) && ButtonStarting(form, "Undo selected changes").IsEnabled, "Undo is previewed from the saved journal");
            await form.ApplyAsync();
            Assert(File.Exists(file) && !File.Exists(Path.Combine(root, "BENEE", "Hey u x", "Happen to Me.mp3")), "manager Undo restores the original library");
        }
        finally { if (form.IsWorking) form.SetWorkingForTest(false); host.Close(); }
    }

    private static async Task AlbumCleanupGui()
    {
        string root = NewFolder("album-cleanup-gui"), album = Path.Combine(root, "Example artist", "A multi-disc album");
        DiscTrack(Path.Combine(album, "Disc 1"), "one.mp3", 1, 1, "One (feat. Guest)"); DiscTrack(Path.Combine(album, "Disc 2"), "two.mp3", 2, 1, "Two");
        var form = new OrganizePage(root, () => true, _ => { }, _ => Task.CompletedTask);
        var host = Host(form);
        try
        {
            foreach (var box in Controls(form.OptionsPanel).OfType<CheckBox>()) box.IsChecked = false;
            BackupSettings.Root = NewFolder("album-cleanup-gui-backups");
            Named<CheckBox>(form, "Combine multi-disc albums").IsChecked = true;
            Named<CheckBox>(form, "Remove featured artists from titles").IsChecked = true;
            await form.PreviewAsync();
            Assert(form.CurrentPlan.Actions.Count(a => a.MergeDiscs) == 2 && form.CurrentPlan.Actions.Any(a => a.NewTitle == "One"), "the manager exposes both opt-in album operations in one preview");
            form.SetRowChecked(0, false); Assert(form.CheckedCount == 0 && !ButtonStarting(form, "Apply selected changes").IsEnabled, "clearing one merge row clears the complete album");
            form.SetRowChecked(0, true); Assert(form.CheckedCount == 2, "selecting a merge row selects the complete album");
            Shot(host, "album-cleanup-options.png");
            await form.ApplyAsync();
            using (var file = TagLib.File.Create(Path.Combine(album, "one.mp3")))
                Assert(file.Tag.Title == "One" && file.Tag.TrackCount == 2 && file.Tag.DiscCount == 1, "the manager applies its previewed title and disc tags");
        }
        finally { host.Close(); }
    }

    // Pages are tested on their own in a plain window.
    private static Window Host(Control page)
    {
        var window = new Window { Width = 1100, Height = 780 };
        Kit.Prepare(window, new Border { Child = page, Padding = new Thickness(20) });
        window.Show();
        return window;
    }
}
