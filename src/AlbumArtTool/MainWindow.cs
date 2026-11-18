using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AlbumArtTool.Core;
using AlbumArtTool.Online;
using AlbumArtTool.Ui;

namespace AlbumArtTool
{
    internal sealed partial class MainWindow : Window
    {
        private void UpdateIssues()
        {
            issuesButton.IsVisible = issues.Count > 0;
            issuesButton.Content = issues.Count == 1 ? "1 issue" : issues.Count + " issues";
            SyncIssues();
        }

        internal Action<ProcessStartInfo> StartExplorer = info => { using (Process.Start(info)) { } };
        // The music folder is shown as a quiet path button; clicking it chooses another folder.
        private readonly Button browse = Kit.IconButton(Icons.Folder, " ", "ghost");
        private string root = "";
        private readonly TextBox search = new TextBox { Watermark = "Search albums, artists or folders" };
        private readonly Button scan = Kit.IconButton(Icons.Refresh, "", "ghost");
        // Chips filter the album list; "Track artists" switches the details pane to artist cleanup.
        private readonly Button missingTab = Kit.Button("Missing art", "chip");
        private readonly Button existingTab = Kit.Button("Has art", "chip");
        private readonly Button allTab = Kit.Button("All", "chip");
        private readonly Button choose = Kit.IconButton(Icons.Image, "Choose image…");
        private readonly Button discard = Kit.Button("Discard", "link");
        private readonly Pill coverage = new Pill();
        private readonly DisplayWindow display = new DisplayWindow();
        private readonly CoverWash wash = new CoverWash();
        private readonly StackPanel figures = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 20 };
        private Control statusBar;
        internal DisplayWindow Display => display;
        private readonly Button apply = Kit.Button("Apply cover", "primary");
        private readonly Button undo = Kit.IconButton(Icons.Undo, "Undo", "ghost");
        private readonly Button openFolder = Kit.IconButton(Icons.Folder, "Open album folder");
        // Only shown when something went wrong, labelled with how many problems there are.
        private readonly Button issuesButton = Kit.Button("Issues");
        private readonly AlbumList albums = new AlbumList();
        private readonly ICoverSearch coverSearch;
        private readonly CoverSearchPanel online;
        private readonly Image cover = new Image { Stretch = Stretch.Uniform };
        private readonly Border coverFrame;
        private readonly TextBlock selectedTitle = Kit.Label("Select an album", 17.33, true);
        private readonly TextBlock selectedArtist = Kit.Label("", Kit.Body, cls: "muted");
        private readonly TextBlock coverCaption = Kit.Label("Drop a cover image here", 11.33, cls: "muted");
        private readonly TextBlock detail = Kit.Label("", Kit.Tiny, cls: "muted");
        private readonly TextBlock selectedPath = Kit.Label("", 11.33, cls: "muted");
        private readonly EmptyState empty = new EmptyState();
        private readonly TextBlock status = Kit.Label("Ready", Kit.Small, cls: "muted");
        private readonly ProgressBar progress = new ProgressBar { Minimum = 0, Maximum = 100, Margin = new Thickness(6, 0, 8, 0) };
        private readonly TextBlock progressPercent = Kit.Label("", Kit.Small);
        private readonly CheckBox onlyMissing = Kit.Check("Only fill tracks missing covers", true);
        private readonly CheckBox folderCover = Kit.Check("Update folder cover images too", true);
        private readonly Scanner scanner = new Scanner();
        private readonly CoverEditor editor = new CoverEditor();
        private readonly List<string> issues = new List<string>();
        private ScanResult library = new ScanResult();
        private CancellationTokenSource cancellation;
        private EditResult lastEdit;
        private string lastEditedFolder;
        private byte[] pending;
        private int previewVersion, progressVersion;
        private enum View { Missing, HasArt, All }
        private View view = View.Missing;
        private bool busy, writing, refreshing, hasScanned;

        /// <summary>The music folder being shown.</summary>
        internal string Root
        {
            get => root;
            set
            {
                root = value ?? "";
                browse.SetText(Kit.MiddleEllipsis(root, 26));
                browse.Tip(root + "\n\nClick to choose a different music folder.");
            }
        }

        internal MainWindow(string root, bool autoScan = true, ICoverSearch onlineService = null, bool firstRun = false)
        {
            coverSearch = onlineService ?? new CoverSearchService();
            online = new CoverSearchPanel(coverSearch) { UseCover = ApplyOnlineCoverAsync };
            Title = "Muzikette";
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Width = 1320; Height = 840; MinWidth = 1140; MinHeight = 720;
            Root = root;
            browse.Named("Music folder"); search.Named("Search albums, artists or folders");
            coverFrame = new Border { CornerRadius = new CornerRadius(10), ClipToBounds = true, Margin = new Thickness(4, 0, 8, 2), Cursor = new Cursor(StandardCursorType.Hand), Child = cover };
            coverFrame.Brush(Border.BackgroundProperty, "Input");
            coverFrame.Named("Album cover preview and image drop target");
            Kit.Prepare(this, BuildLayout());
            closePage.Named("Close YouTube Music").Tip("Close the YouTube Music page. Replacements already made stay, and can be undone from Organize › Preview Undo.");
            closePage.Click += (s, e) => CloseYouTube();
            albums.WorkStatus = album => queuedAlbums.Contains(album.Key) ? (currentJob?.Album.Key == album.Key ? (currentJob.Artist == null ? "Writing cover" : "Writing artists") : "Queued", "Accent") :
                showingArtists ? (album.ArtistChangeCount > 0 ? (album.ArtistChangeCount + " to credit", "Warning") : ("Credited", "Positive")) : ((string, string)?)null;
            browse.Click += async (s, e) => await ChangeFolderAsync();
            SetScanButton(false);
            scan.Click += async (s, e) =>
            {
                bool shift = (lastModifiers & KeyModifiers.Shift) != 0;
                if (busy) cancellation?.Cancel(); else await ScanAsync(shift);
            };
            missingTab.Click += (s, e) => SwitchView(View.Missing);
            existingTab.Click += (s, e) => SwitchView(View.HasArt);
            allTab.Click += (s, e) => SwitchView(View.All);
            missingTab.Tip("Albums where at least one track has no embedded cover.");
            existingTab.Tip("Albums that already have a cover, to replace or standardize it.");
            allTab.Tip("Every album in the music folder.");
            search.TextChanged += (s, e) => { if (!busy) ScheduleSearch(); };
            albums.SelectionChanged += (s, e) => { if (!refreshing) ShowSelection(); };
            albums.CoverDropped += (album, path) => { if (!busy && !showingArtists) StageImage(path); };
            choose.Click += async (s, e) =>
            {
                string path = await PickImageAsync();
                if (path != null) StageImage(path);
            };
            apply.Click += async (s, e) => await ApplyAsync();
            undo.Click += async (s, e) => await UndoAsync();
            onlyMissing.IsCheckedChanged += (s, e) => UpdateApplyButton();
            folderCover.IsCheckedChanged += (s, e) => UpdateApplyButton();
            openFolder.Click += (s, e) => OpenSelectedFolder();
            issuesButton.Click += (s, e) => Navigate(Page.Activity);
            issuesButton.Named("Show issues").Tip("Open Activity to see the files that could not be read or changed during the last scan or edit, with the reason for each.");
            issuesButton.IsVisible = false; issuesButton.Margin = new Thickness(12, 0, 0, 0); issuesButton.Padding = new Thickness(12, 3, 12, 4);
            DragDrop.SetAllowDrop(coverFrame, true);
            coverFrame.AddHandler(DragDrop.DragOverEvent, (s, e) => e.DragEffects = !busy && albums.SelectedAlbum != null && AlbumList.SingleImagePath(e.Data) != null ? DragDropEffects.Copy : DragDropEffects.None);
            coverFrame.AddHandler(DragDrop.DropEvent, (s, e) => { string path = AlbumList.SingleImagePath(e.Data); if (!busy && path != null) StageImage(path); });
            coverFrame.PointerPressed += (s, e) => { if (choose.IsEnabled) choose.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); };
            Closing += (s, e) =>
            {
                if (installingUpdate && !restartForUpdate) { e.Cancel = true; return; }
                if (maintenanceRunning || organizePage != null && organizePage.IsWorking || youtubePage != null && youtubePage.IsWorking)
                {
                    e.Cancel = true; status.Text = "A library task is running. Stop it on its page before closing.";
                    Navigate(youtubePage != null && youtubePage.IsWorking ? Page.YouTube : Page.Organize); return;
                }
                if (writing || queueRunning) { e.Cancel = true; status.Text = "Work is still running. Open Activity to clear waiting jobs; the current update will finish safely."; Navigate(Page.Activity); }
                else cancellation?.Cancel();
            };
            Closed += (s, e) => Shutdown();
            // Once per launch the name scrolls across the display before the readouts light up (click to skip).
            Opened += (s, e) => display.Boot("Muzikette");
            AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
            AddHandler(PointerPressedEvent, (s, e) => lastModifiers = e.KeyModifiers, RoutingStrategies.Tunnel);
            AddHandler(KeyUpEvent, (s, e) => lastModifiers = e.KeyModifiers, RoutingStrategies.Tunnel);
            SetBusy(false);
            SwitchView(View.Missing);
            if (firstRun) ShowWelcome();
            else if (autoScan) Opened += async (s, e) => await ScanAsync();
        }

        private KeyModifiers lastModifiers;
        internal AlbumList Albums => albums;
        internal CoverSearchPanel Online => online;
        internal bool HasStagedCover => pending != null;
        internal void DiscardStaged() { if (pending != null && !busy && !queueRunning) status.Text = "Ready"; ShowSelection(); }

        private void OnKey(object sender, KeyEventArgs e)
        {
            lastModifiers = e.KeyModifiers;
            bool control = (e.KeyModifiers & KeyModifiers.Control) != 0;
            if (control && e.Key == Key.F) { if (current != Page.Albums && current != Page.Artists) Navigate(Page.Albums); search.Focus(); e.Handled = true; }
            else if (control && e.Key >= Key.D1 && e.Key <= Key.D6) { Navigate(new[] { Page.Albums, Page.Artists, Page.AddMusic, Page.Health, Page.Organize, Page.Activity }[e.Key - Key.D1]); e.Handled = true; }
            else if (control && e.Key == Key.OemComma) { Navigate(Page.Settings); e.Handled = true; }
            else if (e.Key == Key.F1) { ShowShortcuts(); e.Handled = true; }
            else if (control && e.Key == Key.O && !busy) { _ = ChangeFolderAsync(); e.Handled = true; }
            else if (control && e.Key == Key.Enter && !(e.Source is TextBox) && (current == Page.Albums || current == Page.Artists))
            {
                if (showingArtists) { if (applyArtist.IsEnabled) _ = ApplyArtistAsync(); }
                else if (apply.IsEnabled) _ = ApplyAsync();
                e.Handled = true;
            }
            else if (e.Key == Key.F5 && !busy) { _ = ScanAsync((e.KeyModifiers & KeyModifiers.Shift) != 0); e.Handled = true; }
            else if (control && e.Key == Key.Z && undo.IsEnabled && !(e.Source is TextBox)) { _ = UndoAsync(); e.Handled = true; }
            else if (e.Key == Key.Escape && pending != null && !busy) { DiscardStaged(); e.Handled = true; }
        }

        private Control BuildLayout()
        {
            // Albums / Artists: filter chips and search, the album list, and the details pane.
            var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            foreach (var tab in new[] { missingTab, existingTab, allTab })
            {
                Avalonia.Automation.AutomationProperties.SetControlTypeOverride(tab, Avalonia.Automation.Peers.AutomationControlType.TabItem);
                chips.Children.Add(tab);
            }
            viewChips = chips;
            artistChips = BuildArtistChips();
            var chipHost = new Panel(); chipHost.Children.Add(chips); chipHost.Children.Add(artistChips);
            search.Width = 300; search.Margin = new Thickness(3, 0, 0, 0);
            var tabs = Kit.Grid("*,Auto", "Auto").Add(chipHost.Cell(0, 0), search.Cell(0, 1));
            tabs.Margin = new Thickness(0, 0, 0, 12);
            empty.Margin = new Thickness(30);
            empty.Show(Icons.Refresh, "Scanning your music…", "Albums appear when the scan finishes.");
            var listHost = new Panel(); listHost.Children.Add(albums); listHost.Children.Add(empty);
            var listLayout = Kit.Grid("*", "Auto,*").Add(BuildLibraryToolbar().Cell(0, 0), listHost.Cell(1, 0));
            var listPanel = Kit.Frost(listLayout, new Thickness(8, 8, 8, 10)); listPanel.Margin = new Thickness(0, 0, 14, 0);

            // The cover you have, or the new one you are about to apply, is the first thing in the details pane.
            coverFrame.Width = coverFrame.Height = 168; coverFrame.Margin = new Thickness(0); coverFrame.CornerRadius = new CornerRadius(3);
            coverCaption.HorizontalAlignment = HorizontalAlignment.Center; coverCaption.TextAlignment = TextAlignment.Center;
            coverCaption.Width = 168; coverCaption.Margin = new Thickness(0, 7, 0, 0); coverCaption.FontSize = Kit.Small;
            discard.Named("Discard new cover").Tip("Forget the image you chose and show the album's current cover again (Esc). Nothing was written.");
            discard.HorizontalAlignment = HorizontalAlignment.Center; discard.IsVisible = false; discard.FontSize = 10; discard.Margin = new Thickness(0, 2, 0, 0);
            discard.Click += (s, e) => { if (pending != null && !busy) DiscardStaged(); };
            var coverStack = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
            coverStack.Children.Add(coverFrame); coverStack.Children.Add(coverCaption); coverStack.Children.Add(discard);
            var kicker = Kit.Legend("Album", 8.5); kicker.Margin = new Thickness(1, 2, 0, 8);
            selectedTitle.FontFamily = Skin.Display; selectedTitle.FontWeight = FontWeight.ExtraBold; selectedTitle.FontSize = 25;
            selectedTitle.TextWrapping = TextWrapping.Wrap; selectedTitle.MaxLines = 2; selectedTitle.LineHeight = 28;
            selectedArtist.FontSize = 15; selectedArtist.Margin = new Thickness(0, 4, 0, 0);
            detail.FontSize = Kit.Small; detail.TextWrapping = TextWrapping.Wrap; selectedPath.FontSize = 11;
            figures.Margin = new Thickness(0, 14, 0, 0);
            coverage.Margin = new Thickness(0, 12, 0, 4);
            openFolder.Classes.Add("ghost"); openFolder.Margin = new Thickness(-10, 4, 0, 0); openFolder.HorizontalAlignment = HorizontalAlignment.Left; openFolder.Height = 30;
            var info = new StackPanel { Spacing = 0, Margin = new Thickness(20, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
            foreach (var c in new Control[] { kicker, selectedTitle, selectedArtist, detail, figures, coverage, selectedPath, openFolder }) info.Children.Add(c);
            var head = Kit.Grid("Auto,*", "Auto").Add(coverStack.Cell(0, 0), info.Cell(0, 1));
            online.Margin = new Thickness(0, 18, 0, 0);
            // One action bar: where the cover comes from, what it changes, and Apply.
            choose.Height = 38; choose.Padding = new Thickness(14, 2); choose.Margin = new Thickness(0); choose.VerticalAlignment = VerticalAlignment.Center;
            choose.Tip("Pick an image file (JPG, PNG, BMP or GIF) for this album. You can also drop one on the cover above or on the album in the list.");
            // What the cover changes sits above the keys that choose and apply it.
            var options = new WrapPanel { Margin = new Thickness(0, 0, 0, 12), VerticalAlignment = VerticalAlignment.Center };
            foreach (var check in new[] { onlyMissing, folderCover }) { check.Margin = new Thickness(0, 0, 22, 2); options.Children.Add(check); }
            apply.Height = 42; apply.MinWidth = 200; apply.Padding = new Thickness(18, 2); apply.Margin = new Thickness(0); apply.VerticalAlignment = VerticalAlignment.Center; apply.FontSize = 11.5;
            var actionBar = Kit.Grid("Auto,*,Auto", "Auto,Auto").Add(options.Cell(0, 0, 3), choose.Cell(1, 0), apply.Cell(1, 2));
            var bar = new Border { Child = actionBar, Classes = { "actionbar" }, Margin = new Thickness(0, 14, 0, 0) };
            var sidebar = Kit.Grid("*", "Auto,*,Auto").Add(head.Cell(0, 0), online.Cell(1, 0), bar.Cell(2, 0));
            coverPanel = sidebar; artistPanel = BuildArtistPanel(); artistPanel.IsVisible = false;
            var detailHost = new Panel(); detailHost.Children.Add(sidebar); detailHost.Children.Add(artistPanel);
            // The selected album lends its details a faint wash of its cover's colors.
            var washed = new Panel(); washed.Children.Add(wash); washed.Children.Add(new Border { Child = detailHost, Padding = new Thickness(20, 18, 20, 18) });
            var details = Kit.Frost(washed, new Thickness(0)); details.ClipToBounds = true;
            var body = Kit.Grid("*,560", "*").Add(listPanel.Cell(0, 0), details.Cell(0, 1));
            libraryView = Kit.Grid("*", "Auto,*").Add(tabs.Cell(0, 0), body.Cell(1, 0));
            pageHost.Children.Add(libraryView);
            activityPage = BuildActivityPage(); AddPage(activityPage);

            // The display window: the page and its counts glow on the left, whatever is running on the right.
            // Page keys (Undo, Rename artists, Close) sit beside it on the faceplate.
            pageTitle.Named("Page title");
            status.Named("Operation status and track counter");
            status.PropertyChanged += (s, e) => { if (e.Property == TextBlock.TextProperty) UpdateStatusBar(); };
            progress.Named("Scan and update progress"); progress.IsVisible = false; progress.Classes.Add("display"); progress.Width = 230;
            progressPercent.Named("Progress percentage"); progressPercent.FontFamily = Skin.Readout; progressPercent.FontWeight = FontWeight.Bold; progressPercent.FontSize = 15;
            progressPercent.Foreground = new SolidColorBrush(Skin.Amber); progressPercent.Effect = Skin.Glow(Skin.Amber, 6, 0.5); progressPercent.MinWidth = 40; progressPercent.TextAlignment = TextAlignment.Right;
            display.SetMeter(progress, progressPercent);
            display.Named("Display");
            // The page's name and status line stay as text for screen readers and tests; the display shows them.
            var hidden = new Panel { IsVisible = false }; hidden.Children.Add(pageTitle); hidden.Children.Add(pageSubtitle); hidden.Children.Add(status);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            var help = Kit.IconButton(Icons.Keyboard, "", "ghost").Named("Keyboard shortcuts").Tip("Keyboard shortcuts (F1).");
            help.Click += (s, e) => ShowShortcuts();
            renameArtist.Named("Rename an artist").Tip("Change one or more artists to a single name in every song of your library (Artist and Album Artist tags), for example an artist who released under several names. You preview every change first.");
            renameArtist.Click += (s, e) => ShowRenameArtist();
            issuesButton.Classes.Add("ghost"); issuesButton.Margin = new Thickness(0); issuesButton.Padding = new Thickness(12, 2);
            foreach (var b in new[] { issuesButton, renameArtist, closePage, undo, help }) { b.Height = 34; actions.Children.Add(b); }
            var top = Kit.Grid("*,Auto,0", "Auto").Add(display.Cell(0, 0), actions.Cell(0, 1), hidden.Cell(0, 2));
            top.Margin = new Thickness(0, 0, 0, 16);
            statusBar = display;
            var main = Kit.Grid("*", "Auto,*").Add(top.Cell(0, 0), pageHost.Cell(1, 0));
            main.Margin = new Thickness(24, 20, 24, 20);
            var shell = Kit.Grid("232,*", "*").Add(BuildSidebar().Cell(0, 0), main.Cell(0, 1));
            onlyMissing.Tip("Keeps existing embedded front covers. Uncheck to replace the cover on every track in this album.");
            folderCover.Tip("Updates recognized cover/folder/front/album/albumart images, or creates cover.jpg. Disabled when multiple albums share a folder.");
            undo.Named("Undo last change").Tip("Restore the files from your most recent cover or artist edit in this session (Ctrl+Z). Organize runs are undone in Organize.");
            return shell;
        }

        private void SetScanButton(bool scanning)
        {
            Kit.SetIcon(scan, scanning ? Icons.Stop : Icons.Refresh);
            scan.Named(scanning ? "Stop scan" : "Rescan library");
            scan.Tip(scanning ? "Stop the scan. Albums found so far are kept." : "Scan the music folder again (F5). Hold Shift to read every file again instead of using the scan cache.");
        }

        private async Task ChangeFolderAsync()
        {
            if (busy || queueRunning || maintenanceRunning) return;
            var picked = await PickFolderAsync("Choose the top folder containing your music", Root);
            if (picked != null) { Root = picked; await ScanAsync(); }
        }

        private void SwitchView(View next)
        {
            if (busy) return;
            view = next; showingArtists = false;
            SetViewFilter(0);
            onlyMissing.IsChecked = next == View.Missing;
            StyleTabs();
            RefreshAlbums();
            ShowPage(Page.Albums);
        }

        private DispatcherTimer searchTimer;
        // Typing filters after a short pause instead of rebuilding the list on every key.
        private void ScheduleSearch()
        {
            searchTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background, (s, e) => { searchTimer.Stop(); if (!busy) RefreshAlbums(); });
            searchTimer.Stop(); searchTimer.Start();
        }

        private void RefreshAlbums(string key = null, bool preserveStaged = false)
        {
            searchTimer?.Stop();
            string previousKey = albums.SelectedAlbum?.Key;
            byte[] previousImage = preserveStaged ? pending : null;
            key = key ?? albums.SelectedAlbum?.Key;
            string query = (search.Text ?? "").Trim();
            var culture = System.Globalization.CultureInfo.CurrentCulture.CompareInfo;
            var visible = library.Albums.Where(a => showingArtists || view == View.All || (view == View.Missing ? a.MissingCount > 0 : a.HasAnyCover))
                .Where(a => albumFilter.SelectedIndex != 1 || a.ArtistChangeCount > 0)
                .Where(a => query.Length == 0 || culture.IndexOf(a.Title + " " + a.Artist + " " + a.Folder, query, System.Globalization.CompareOptions.IgnoreCase) >= 0).ToList();
            var comparer = StringComparer.CurrentCultureIgnoreCase;
            visible = (albumSort.SelectedIndex == 2 ? visible.OrderByDescending(a => a.ArtistChangeCount).ThenBy(a => a.Artist, comparer).ThenBy(a => a.Title, comparer) :
                albumSort.SelectedIndex == 1 ? visible.OrderBy(a => a.Title, comparer).ThenBy(a => a.Artist, comparer) :
                visible.OrderBy(a => a.Artist, comparer).ThenBy(a => a.Title, comparer)).ToList();
            refreshing = true;
            try { albums.SetAlbums(visible, key); }
            finally { refreshing = false; }
            int missingCount = 0, existingCount = 0, artistCount = 0;
            foreach (var album in library.Albums) { if (album.MissingCount > 0) missingCount++; if (album.HasAnyCover) existingCount++; if (album.ArtistChangeCount > 0) artistCount++; }
            missingTab.Content = "Missing art (" + missingCount + ")";
            existingTab.Content = "Has art (" + existingCount + ")";
            allTab.Content = "All (" + library.Albums.Count + ")";
            cleanupTab.Content = "Needs cleanup (" + artistCount + ")";
            everyTab.Content = "All albums (" + library.Albums.Count + ")";
            listCount.Text = !hasScanned ? "" : Count(visible.Count, "album") + (query.Length > 0 ? " match “" + query + "”" : "");
            UpdateBadges();
            if (current == Page.Albums) UpdateHeader();
            empty.IsVisible = visible.Count == 0;
            albums.IsVisible = !empty.IsVisible;
            if (query.Length > 0) empty.Show(Icons.Search, "No albums match “" + query + "”", "Try part of a title, an artist or a folder name.", "Clear search", () => search.Text = "");
            else if (!hasScanned) empty.Show(Icons.Album, "Scan a folder to find your albums", "Albums appear here once the music folder has been scanned.", "Choose music folder…", () => _ = ChangeFolderAsync());
            else if (library.Albums.Count == 0) empty.Show(Icons.Folder, "No music found in this folder", "Choose the folder that holds your albums. MP3, FLAC, M4A, Ogg, Opus, WMA, WAV and AIFF files are found in every subfolder.", "Choose music folder…", () => _ = ChangeFolderAsync());
            else if (showingArtists || albumFilter.SelectedIndex == 1) empty.Show(Icons.Artist, "No albums match this filter", "Every album's track artists already match its main artist.", "Show all albums", () => albumFilter.SelectedIndex = 0);
            else if (view == View.Missing) empty.Show(Icons.Check, "Every album has cover art", "Open Has art to replace or standardize a cover.", "Show albums with art", () => SwitchView(View.HasArt));
            else empty.Show(Icons.Album, "No album has cover art yet", "Start with the albums that are missing art.", "Show missing art", () => SwitchView(View.Missing));
            ShowSelection();
            if (previousImage != null && albums.SelectedAlbum?.Key == previousKey) SetPendingImage(previousImage);
        }

        private void ShowSelection()
        {
            int version = ++previewVersion;
            pending = null;
            var album = albums.SelectedAlbum;
            if (showingArtists)
            {
                online.SetAlbum(null, false); wash.Show(album?.Key, album?.Thumbnail); ShowArtistSelection(); UpdateLibraryTools(); return;
            }
            selectedTitle.Text = album?.Title ?? "Select an album";
            selectedArtist.Text = album?.Artist ?? "";
            selectedPath.Text = album == null ? "" : Kit.MiddleEllipsis(album.Folder, 64);
            selectedTitle.Tip(album?.Title); selectedPath.Tip(album?.Folder); openFolder.Tip(album == null ? "Open this album's folder in File Explorer." : "Open " + album.Folder + " in File Explorer.");
            detail.Text = album == null ? "Pick an album on the left to see its cover and find a new one." : "";
            detail.IsVisible = album == null;
            ShowFigures(album);
            wash.Show(album?.Key, album?.Thumbnail);
            coverCaption.Text = album?.Thumbnail != null ? (album.PreviewTrack != null ? "Current cover" : "Current folder image") : album != null ? "Drop an image here" : "";
            coverage.Set(album == null ? null : album.Coverage, album == null ? null : album.MissingCount > 0 ? "Warning" : "Positive");
            discard.IsVisible = false; openFolder.IsVisible = selectedPath.IsVisible = album != null;
            SetPreview(album?.Thumbnail);
            if (album?.Thumbnail != null) _ = LoadPreviewAsync(album, version);
            bool shared = album != null && SharedFolder(album);
            folderCover.IsEnabled = !busy && !maintenanceRunning && album != null && !shared;
            folderCover.Content = shared ? "Folder shared by multiple albums" : "Update folder cover images too";
            choose.IsEnabled = onlyMissing.IsEnabled = album != null && !busy && !maintenanceRunning && !queuedAlbums.Contains(album.Key);
            openFolder.IsEnabled = album != null && !busy;
            folderCover.IsEnabled &= album != null && !queuedAlbums.Contains(album.Key);
            UpdateApplyButton();
            online.SetAlbum(album, !busy && !maintenanceRunning && (album == null || !queuedAlbums.Contains(album.Key)));
            UpdateLibraryTools();
        }

        // The album's vital numbers, printed on the panel: tracks, running time, and format with its bitrate.
        private void ShowFigures(Album album)
        {
            figures.Children.Clear();
            figures.IsVisible = album != null && album.Tracks.Count > 0;
            if (!figures.IsVisible) return;
            var minutes = TimeSpan.FromTicks(album.Tracks.Sum(t => t.Duration.Ticks));
            var formats = album.Tracks.Select(t => Path.GetExtension(t.Path).TrimStart('.').ToUpperInvariant()).Where(f => f.Length > 0).Distinct().ToList();
            bool lossless = album.Tracks.All(t => t.Lossless);
            var rates = album.Tracks.Where(t => t.Bitrate > 0).Select(t => t.Bitrate).ToList();
            string quality = lossless ? "Lossless" : rates.Count > 0 ? (int)Math.Round(rates.Average()) + " kbps" : "";
            figures.Children.Add(Kit.Figure(album.Tracks.Count.ToString(), album.Tracks.Count == 1 ? "Track" : "Tracks", out _));
            if (minutes.TotalSeconds >= 1) figures.Children.Add(Kit.Figure(minutes.TotalHours >= 1 ? (int)minutes.TotalHours + ":" + minutes.Minutes.ToString("00") : ((int)minutes.TotalMinutes).ToString(), minutes.TotalHours >= 1 ? "Hours" : "Min", out _));
            if (formats.Count > 0) figures.Children.Add(Kit.Figure(formats.Count == 1 ? formats[0] : "Mixed", quality, out _).Tip(string.Join(", ", formats) + (rates.Count > 0 && !lossless ? " · average " + quality : "")));
        }

        private static string Count(int n, string noun) => n + " " + noun + (n == 1 ? "" : "s");

        private bool SharedFolder(Album album)
        {
            int count = 0;
            foreach (var other in library.Albums) if (string.Equals(other.Folder, album.Folder, StringComparison.OrdinalIgnoreCase) && ++count > 1) return true;
            return false;
        }

        private async Task LoadPreviewAsync(Album album, int version)
        {
            try
            {
                byte[] bytes = await Task.Run(() =>
                {
                    byte[] original;
                    if (album.PreviewTrack != null)
                    { using (var audio = TagLib.File.Create(album.PreviewTrack, TagLib.ReadStyle.None)) original = Artwork.GetCover(audio.Tag); }
                    else original = album.FolderCovers.Count > 0 ? Artwork.LoadFile(album.FolderCovers[0]) : null;
                    return original == null ? null : Artwork.Normalize(original, 480);
                });
                if (version == previewVersion && pending == null && bytes != null) SetPreview(bytes);
            }
            catch (Exception e) when (Scanner.IsFileError(e) || Artwork.IsImageError(e) || e is TagLib.CorruptFileException || e is TagLib.UnsupportedFormatException)
            { /* Retain the scan thumbnail if the source has become unavailable. */ }
        }

        private void SetPreview(byte[] bytes)
        {
            var old = cover.Source as Bitmap;
            Bitmap next = null;
            try { next = Kit.Decode(bytes); } catch (Exception) { }
            cover.Source = next;
            if (old != null && !ReferenceEquals(old, next)) old.Dispose();
        }

        internal void StageImage(string path)
        {
            if (busy || maintenanceRunning || albums.SelectedAlbum == null || queuedAlbums.Contains(albums.SelectedAlbum.Key)) return;
            try
            {
                SetPendingImage(Artwork.Normalize(Artwork.LoadFile(path)));
                status.Text = "Review the cover, then click Apply. Press Esc to discard the preview.";
            }
            catch (Exception e) when (Scanner.IsFileError(e) || Artwork.IsImageError(e))
            { _ = Dialogs.ShowMessage(this, "Choose another image", "That image could not be opened. Use a JPG, PNG, BMP or GIF.\n\n" + e.Message); }
        }

        private void SetPendingImage(byte[] image)
        {
            ++previewVersion; pending = image; SetPreview(image);
            var size = Artwork.ReadInfo(image);
            coverCaption.Text = "New cover · " + size.Width + " × " + size.Height;
            coverage.Set("New cover ready to apply", "Accent"); discard.IsVisible = true;
            Motion.Drop(coverFrame.Child as Visual);
            wash.Show("new " + previewVersion, image);
            UpdateApplyButton();
        }

        private void UpdateApplyButton()
        {
            var album = albums.SelectedAlbum;
            int count = album == null ? 0 : onlyMissing.On() ? album.MissingCount : album.Tracks.Count;
            bool queued = album != null && queuedAlbums.Contains(album.Key);
            apply.Content = queued ? "Cover queued" : count > 0 ? "Apply cover to " + count + (count == 1 ? " track" : " tracks") : "Apply cover";
            bool hasImage = pending != null || album?.PreviewTrack != null || album?.FolderCovers.Count > 0;
            apply.IsEnabled = !busy && !maintenanceRunning && !queued && hasImage && album != null && (count > 0 || (folderCover.IsEnabled && folderCover.On()));
            apply.Tip(queued ? "This album is already in the queue. You can work on another album." :
                pending != null ? "Add the chosen cover to the work queue." : "Apply the displayed cover using the full-size image already in your album.");
        }

        internal void SetBusy(bool value, bool edit = false)
        {
            progressVersion++;
            busy = value; writing = value && edit;
            browse.IsEnabled = missingTab.IsEnabled = existingTab.IsEnabled = allTab.IsEnabled = albums.IsEnabled = !value;
            search.IsReadOnly = value;
            scan.IsEnabled = !writing;
            SetScanButton(value && !edit);
            undo.IsEnabled = !value && lastEdit != null && lastEdit.Entries.Count > 0;
            progress.IsVisible = value;
            progressPercent.IsVisible = value; progressPercent.Text = "";
            progress.IsIndeterminate = value && !edit;
            progress.Value = 0;
            choose.IsEnabled = openFolder.IsEnabled = onlyMissing.IsEnabled = !value && albums.SelectedAlbum != null;
            if (value) folderCover.IsEnabled = false;
            online.SetActive(!value && !showingArtists);
            if (value && !edit)
            {
                empty.Show(Icons.Refresh, "Scanning your music…", "Albums appear when the scan finishes.");
                albums.IsVisible = false; empty.IsVisible = true;
            }
            UpdateIssues(); UpdateStatusBar();
            UpdateApplyButton(); UpdateLibraryTools();
        }

        internal IProgress<ProgressInfo> Reporter()
        {
            int operation = progressVersion;
            return new Throttled<ProgressInfo>(p =>
            {
                if (!busy || operation != progressVersion) return;
                status.Text = p.Message;
                if (p.Total > 0)
                {
                    progress.IsIndeterminate = false;
                    progress.Value = Math.Max(0, Math.Min(100, (int)(100.0 * p.Completed / p.Total)));
                    progressPercent.Text = (int)progress.Value + "%";
                }
                else { progress.IsIndeterminate = true; progressPercent.Text = ""; }
            });
        }

        internal async Task ScanAsync(bool force = false)
        {
            if (busy || queueRunning || maintenanceRunning) return;
            string root = Root;
            cancellation?.Dispose(); cancellation = new CancellationTokenSource();
            var token = cancellation.Token;
            issues.Clear(); SetBusy(true); status.Text = "Scanning music folders…";
            var reporter = Reporter();
            try
            {
                var result = await Task.Run(() => scanner.Scan(root, token, reporter, useCache: !force));
                library = result; hasScanned = true; libraryVersion++; issues.AddRange(result.Issues);
                if (!result.Cancelled) AppSettings.LastFolder = root;
                status.Text = (result.Cancelled ? "Scan stopped · " : "") + result.Albums.Count + (result.Albums.Count == 1 ? " album · " : " albums · ") + result.FilesRead + (result.FilesRead == 1 ? " track" : " tracks") +
                    (issues.Count > 0 ? " · " + issues.Count + (issues.Count == 1 ? " issue" : " issues") : " · Drop an image onto an album to get started.");
                RefreshAlbums();
            }
            catch (Exception e) { issues.Add(e.Message); status.Text = "Scan could not finish. See Issues."; RefreshAlbums(); }
            finally { SetBusy(false); ShowSelection(); FinishFirstRun(); }
        }

        internal Task ApplyAsync()
        {
            var album = albums.SelectedAlbum;
            if (busy || maintenanceRunning || album == null || queuedAlbums.Contains(album.Key)) return Task.CompletedTask;
            try
            {
                if (pending != null)
                {
                    byte[] image = pending;
                    return EnqueueCover(album, "Chosen image", () => Task.FromResult(image));
                }
                // The button applies exactly the cover being previewed, at full resolution.
                bool embedded = album.PreviewTrack != null;
                string path = embedded ? album.PreviewTrack : album.FolderCovers.FirstOrDefault();
                if (path == null) return Task.CompletedTask;
                var stamp = FileStamp.Read(path);
                return EnqueueCover(album, embedded ? "Existing embedded cover" : "Existing folder cover", () => Task.Run(() =>
                {
                    if (!stamp.Matches(path)) throw new IOException("The source cover changed after it was queued. Select the album again and retry.");
                    byte[] original;
                    if (embedded)
                    { using (var audio = TagLib.File.Create(path, TagLib.ReadStyle.None)) original = Artwork.GetCover(audio.Tag); }
                    else original = Artwork.LoadFile(path);
                    if (!stamp.Matches(path)) throw new IOException("The source cover changed while it was being read. Retry the album.");
                    return Artwork.Normalize(original);
                }));
            }
            catch (Exception e) { issues.Add(e.Message); UpdateIssues(); status.Text = "Could not queue this cover. See Issues."; return Task.CompletedTask; }
        }

        internal Task ApplyOnlineCoverAsync(string albumKey, CoverCandidate candidate)
        {
            var album = albums.SelectedAlbum;
            if (busy || album == null || album.Key != albumKey || queuedAlbums.Contains(albumKey)) return Task.CompletedTask;
            var source = new CoverCandidate { Source = candidate.Source, ImageUrl = candidate.ImageUrl };
            return EnqueueCover(album, source.Source, () => coverSearch.DownloadAsync(source, CancellationToken.None));
        }

        private async Task UndoAsync()
        {
            if (busy || queueRunning || maintenanceRunning || lastEdit == null) return;
            SetBusy(true, true); issues.Clear();
            var reporter = Reporter();
            try
            {
                var result = await Task.Run(() => editor.Undo(lastEdit, reporter));
                issues.AddRange(result.Issues);
                await RefreshFolderAsync(lastEditedFolder, null);
                status.Text = result.Restored + " original files restored" + (issues.Count > 0 ? " · Some files could not be restored. See Issues." : ".");
            }
            catch (Exception e) { issues.Add(e.Message); status.Text = "Undo could not finish. See Issues."; }
            finally { SetBusy(false); ShowSelection(); }
        }

        private async Task RefreshFolderAsync(string path, string key, bool preserveStaged = false)
        {
            var updated = await Task.Run(() => scanner.Scan(path, CancellationToken.None, recursive: false, useCache: false));
            library.Albums.RemoveAll(a => string.Equals(a.Folder, path, StringComparison.OrdinalIgnoreCase));
            library.Albums.AddRange(updated.Albums); issues.AddRange(updated.Issues); libraryVersion++;
            var comparer = StringComparer.CurrentCultureIgnoreCase;
            library.Albums.Sort((a, b) => comparer.Compare(a.Artist + a.Title + a.Folder, b.Artist + b.Title + b.Folder));
            RefreshAlbums(key, preserveStaged);
        }

        private void OpenSelectedFolder()
        {
            if (albums.SelectedAlbum == null) return;
            OpenLocation(albums.SelectedAlbum.Folder, false);
        }

        private void OpenLocation(string path, bool selectFile)
        {
            try
            {
                StartExplorer(selectFile ? new ProcessStartInfo("explorer.exe", "/select," + Updates.UpdateInstaller.QuoteArgument(path)) { UseShellExecute = true } :
                    new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception e) { _ = Dialogs.ShowMessage(this, "Could not open folder", e.Message); }
        }

        private async Task<string> PickFolderAsync(string title, string start)
        {
            var storage = StorageProvider;
            var options = new FolderPickerOpenOptions { Title = title, AllowMultiple = false };
            try { if (!string.IsNullOrEmpty(start) && Directory.Exists(start)) options.SuggestedStartLocation = await storage.TryGetFolderFromPathAsync(start); } catch { }
            var result = await storage.OpenFolderPickerAsync(options);
            return result.Count > 0 ? result[0].TryGetLocalPath() : null;
        }

        private async Task<string> PickImageAsync()
        {
            var result = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose an album cover", AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType("Cover images") { Patterns = new[] { "*.jpg", "*.jpeg", "*.png", "*.bmp", "*.gif" } }, FilePickerFileTypes.All }
            });
            return result.Count > 0 ? result[0].TryGetLocalPath() : null;
        }

        private void Shutdown()
        {
            var source = cancellation; cancellation = null;
            source?.Cancel(); source?.Dispose();
            var image = cover.Source as Bitmap; cover.Source = null; image?.Dispose();
            online.Shutdown(); coverSearch.Dispose();
            healthPage?.Stop(); youtubePage?.Shutdown(force: true); addMusicPage?.Shutdown(); addMusicService?.Dispose();
            foreach (var window in OwnedWindows.ToArray()) window.Close();
        }
    }
}
