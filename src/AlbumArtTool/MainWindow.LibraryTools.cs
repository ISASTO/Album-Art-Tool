using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using AlbumArtTool.Core;
using AlbumArtTool.Ui;

namespace AlbumArtTool
{
    internal sealed partial class MainWindow
    {
        private bool maintenanceRunning;
        private readonly ComboBox albumFilter = new ComboBox(), albumSort = new ComboBox();
        private readonly TextBox mainArtist = new TextBox();
        private readonly Button applyArtist = Kit.Button("Set track artists", "primary");
        private readonly Button openArtistAlbum = Kit.IconButton(Icons.Folder, "Open folder", "ghost"), showArtistTrack = Kit.IconButton(Icons.Open, "Show track", "ghost");
        private readonly TextBlock artistHeading = Kit.Heading("Select an album", 22), artistSummary = Kit.Label("", Kit.Body, cls: "muted");
        private readonly ListBox artistPreview = new ListBox { Classes = { "grid" } };
        private List<ArtistRow> artistRows = new List<ArtistRow>();
        private Control artistPanel, coverPanel;
        private bool showingArtists, refreshingArtist, changingAlbumView;

        internal sealed class ArtistRow
        {
            internal Track Track;
            internal string After;
            internal bool Changes;
            internal event Action Changed;
            internal void Set(string after, bool changes) { if (after == After && changes == Changes) return; After = after; Changes = changes; Changed?.Invoke(); }
        }

        // Above the list: how many albums it shows and how they are sorted. Which albums it shows is chosen by the chips above it.
        private Control BuildLibraryToolbar()
        {
            albumFilter.Named("Album filter"); albumSort.Named("Album sort order");
            albumFilter.Items.Add("All albums in this view"); albumFilter.Items.Add("Artist cleanup available");
            albumSort.Items.Add("Artist and album"); albumSort.Items.Add("Album title"); albumSort.Items.Add("Artist changes first");
            albumFilter.SelectedIndex = albumSort.SelectedIndex = 0;
            // The filter is the model behind the Artists chips; it is not shown on its own.
            albumFilter.IsVisible = false;
            albumFilter.SelectionChanged += (s, e) => { StyleArtistChips(); if (!busy && !changingAlbumView) RefreshAlbums(); };
            albumSort.SelectionChanged += (s, e) => { if (!busy && !changingAlbumView) RefreshAlbums(); };
            albumSort.Width = 170; albumSort.Margin = new Thickness(0); albumSort.MinHeight = 28;
            albumSort.Tip("Sort the album list by artist, by album title, or with the albums that have artist changes first.");
            listCount.Margin = new Thickness(8, 0, 0, 0);
            var sortLabel = Kit.Label("Sort", Kit.Small, cls: "muted"); sortLabel.Margin = new Thickness(0, 0, 8, 0);
            var bar = Kit.Grid("*,Auto,Auto,0", "36").Add(listCount.Cell(0, 0), sortLabel.Cell(0, 1), albumSort.Cell(0, 2), albumFilter.Cell(0, 3));
            bar.Margin = new Thickness(0, 0, 4, 6);
            return bar;
        }

        private readonly TextBlock listCount = Kit.Label("", Kit.Small, true, "muted");
        private readonly Button cleanupTab = Kit.Button("Needs cleanup", "chip"), everyTab = Kit.Button("All albums", "chip");
        private Control artistChips;

        private Control BuildArtistChips()
        {
            cleanupTab.Named("Albums that need artist cleanup").Tip("Albums where some tracks credit guests or a different spelling of the main artist.");
            everyTab.Named("Every album for artist cleanup").Tip("Every album, to set the track artists by hand, even when nothing is suggested.");
            cleanupTab.Click += (s, e) => { if (!busy) albumFilter.SelectedIndex = 1; };
            everyTab.Click += (s, e) => { if (!busy) albumFilter.SelectedIndex = 0; };
            var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
            foreach (var tab in new[] { cleanupTab, everyTab })
            {
                Avalonia.Automation.AutomationProperties.SetControlTypeOverride(tab, Avalonia.Automation.Peers.AutomationControlType.TabItem);
                chips.Children.Add(tab);
            }
            return chips;
        }

        private void StyleArtistChips()
        {
            cleanupTab.Classes.Set("selected", albumFilter.SelectedIndex == 1);
            everyTab.Classes.Set("selected", albumFilter.SelectedIndex != 1);
        }

        private readonly Image artistCover = new Image { Stretch = Stretch.UniformToFill };
        private readonly TextBlock artistAlbumArtist = Kit.Label("", 14, cls: "muted");

        private Control BuildArtistPanel()
        {
            artistHeading.Named("Selected album for artist cleanup");
            artistHeading.FontSize = 19; artistHeading.TextWrapping = TextWrapping.Wrap; artistHeading.MaxLines = 2;
            var help = Kit.Label("Guest credits are collapsed into the main artist, so players file every song under one name. Tracks that already match are left alone.", Kit.Small, cls: "muted");
            help.TextWrapping = TextWrapping.Wrap; help.TextTrimming = TextTrimming.None; help.Margin = new Thickness(0, 4, 0, 0);
            var coverBox = new Border { Width = 84, Height = 84, CornerRadius = new CornerRadius(10), ClipToBounds = true, Child = artistCover, VerticalAlignment = VerticalAlignment.Top };
            coverBox.Brush(Border.BackgroundProperty, "CoverPlaceholder");
            var titles = new StackPanel { Spacing = 2, Margin = new Thickness(16, 0, 0, 0) };
            titles.Children.Add(artistHeading); titles.Children.Add(artistAlbumArtist); titles.Children.Add(help);
            var head = Kit.Grid("Auto,*", "Auto").Add(coverBox.Cell(0, 0), titles.Cell(0, 1));
            mainArtist.Named("Main artist for this album");
            mainArtist.Margin = new Thickness(0, 4, 0, 4); mainArtist.MinHeight = 34;
            mainArtist.Tip("The name every track should credit. Edit it if the suggestion is wrong; a generic name like \"Various artists\" cannot be used.");
            mainArtist.TextChanged += (s, e) => { if (!refreshingArtist) UpdateArtistPreview(); };
            var nameLabel = Kit.Label("Main artist", Kit.Body, true); nameLabel.Margin = new Thickness(0, 16, 0, 0);
            artistSummary.FontSize = Kit.Small; artistSummary.Margin = new Thickness(0, 0, 0, 8);
            artistPreview.Named("Current track artists");
            artistPreview.ItemTemplate = new FuncDataTemplate<ArtistRow>((row, scope) => new ArtistRowView(), true);
            artistPreview.SelectionChanged += (s, e) => UpdateArtistLocations();
            var header = Kit.Grid("36*,34*,30*", "28").Add(Kit.Label("Track", Kit.Small, cls: "muted").Cell(0, 0), Kit.Label("Now", Kit.Small, cls: "muted").Cell(0, 1), Kit.Label("After cleanup", Kit.Small, cls: "muted").Cell(0, 2));
            header.Margin = new Thickness(10, 0, 6, 0);
            var headerBar = new Border { Child = header, BorderThickness = new Thickness(0, 0, 0, 1) };
            headerBar.Brush(Border.BorderBrushProperty, "Border"); headerBar.Brush(Border.BackgroundProperty, "InsetHeader");
            var table = Kit.Grid("*", "Auto,*").Add(headerBar.Cell(0, 0), artistPreview.Cell(1, 0));
            var trackSection = Kit.Inset(table).Named("Track artist preview section");
            trackSection.Padding = new Thickness(0);
            openArtistAlbum.Named("Open artist-tab album folder"); showArtistTrack.Named("Reveal artist-tab selected track");
            openArtistAlbum.Click += (s, e) => OpenSelectedFolder();
            showArtistTrack.Click += (s, e) => { if (artistPreview.SelectedItem is ArtistRow row) OpenLocation(row.Track.Path, true); };
            openArtistAlbum.Tip("Open this album's folder in File Explorer.");
            showArtistTrack.Tip("Select a track above, then open its folder with that song highlighted in File Explorer.");
            applyArtist.Click += async (s, e) => await ApplyArtistAsync();
            foreach (var b in new[] { openArtistAlbum, showArtistTrack }) { b.Height = 34; b.Margin = new Thickness(0, 0, 4, 0); b.HorizontalAlignment = HorizontalAlignment.Left; }
            applyArtist.Height = 40; applyArtist.MinWidth = 196; applyArtist.Padding = new Thickness(18, 2); applyArtist.Margin = new Thickness(0); applyArtist.FontSize = Kit.Body;
            var actions = Kit.Grid("Auto,Auto,*,Auto", "Auto").Add(openArtistAlbum.Cell(0, 0), showArtistTrack.Cell(0, 1), applyArtist.Cell(0, 3));
            var bar = new Border { Child = actions, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 12, 0, 0), Margin = new Thickness(0, 12, 0, 0) };
            bar.Brush(Border.BorderBrushProperty, "Border");
            var panel = Kit.Grid("*", "Auto,Auto,Auto,Auto,*,Auto").Add(head.Cell(0, 0), nameLabel.Cell(1, 0), mainArtist.Cell(2, 0), artistSummary.Cell(3, 0), trackSection.Cell(4, 0), bar.Cell(5, 0));
            return panel;
        }

        private sealed class ArtistRowView : Border
        {
            private readonly TextBlock title = Cell(), current = Cell(), after = Cell();
            private ArtistRow row;
            private static TextBlock Cell() => new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            internal ArtistRowView()
            {
                Height = 30; Padding = new Thickness(10, 0, 6, 0); Background = Brushes.Transparent;
                Child = Kit.Grid("36*,34*,30*", "*").Add(title.Cell(0, 0), current.Cell(0, 1), after.Cell(0, 2));
                DataContextChanged += (s, e) =>
                {
                    if (row != null) row.Changed -= Update;
                    row = DataContext as ArtistRow;
                    if (row != null) row.Changed += Update;
                    Update();
                };
            }
            private void Update()
            {
                if (row == null) return;
                title.Text = row.Track.Title; current.Text = row.Track.ArtistText; after.Text = row.After;
                foreach (var cell in new[] { title, current, after }) cell.Classes.Set("muted", !row.Changes);
                ToolTip.SetTip(this, row.Track.Title + "\nCurrent artist: " + row.Track.ArtistText + "\nAfter cleanup: " + (row.Changes ? row.After : "Unchanged") +
                    (row.Changes && row.Track.AlbumArtists.Length > 0 ? "\nAlbum artist will also be set to " + row.After : "") + "\n" + row.Track.Path);
            }
        }

        private void SetViewFilter(int index)
        {
            changingAlbumView = true;
            try { albumFilter.SelectedIndex = index; }
            finally { changingAlbumView = false; }
        }

        private void SwitchArtists()
        {
            if (busy) return;
            showingArtists = true;
            SetViewFilter(1);
            StyleTabs(); RefreshAlbums();
            ShowPage(Page.Artists);
        }

        private void StyleTabs()
        {
            foreach (var tab in new[] { missingTab, existingTab, allTab })
            {
                bool selected = tab == missingTab ? view == View.Missing : tab == existingTab ? view == View.HasArt : view == View.All;
                tab.Classes.Set("selected", selected);
            }
            StyleArtistChips();
            if (viewChips != null) viewChips.IsVisible = !showingArtists;
            if (artistChips != null) artistChips.IsVisible = showingArtists;
            if (coverPanel != null) coverPanel.IsVisible = !showingArtists;
            if (artistPanel != null) artistPanel.IsVisible = showingArtists;
        }

        private void ShowArtistSelection()
        {
            var album = albums.SelectedAlbum;
            refreshingArtist = true;
            try
            {
                artistHeading.Text = album?.Title ?? "Select an album";
                artistAlbumArtist.Text = album == null ? "" : album.Artist + " · " + album.Tracks.Count + (album.Tracks.Count == 1 ? " track" : " tracks");
                var old = artistCover.Source as Avalonia.Media.Imaging.Bitmap; Avalonia.Media.Imaging.Bitmap next = null;
                try { next = Kit.Decode(album?.Thumbnail); } catch (Exception) { }
                artistCover.Source = next; if (old != null && !ReferenceEquals(old, next)) old.Dispose();
                mainArtist.Text = album?.MainArtist ?? "";
                artistRows = album == null ? new List<ArtistRow>() : album.Tracks.Select(t => new ArtistRow { Track = t, After = "" }).ToList();
                artistPreview.ItemsSource = artistRows;
            }
            finally { refreshingArtist = false; }
        }

        private void UpdateArtistPreview()
        {
            var album = albums.SelectedAlbum; string name = (mainArtist.Text ?? "").Trim();
            bool valid = ArtistRules.Specific(name) && name.Length <= 300 && !name.Any(char.IsControl);
            string normalized = ArtistRules.Normalize(name);
            int count = valid && album != null ? album.Tracks.Count(t => ArtistRules.NeedsChangeNormalized(t, normalized)) : 0;
            bool queued = album != null && queuedAlbums.Contains(album.Key);
            mainArtist.IsReadOnly = busy || maintenanceRunning || queued || album == null;
            applyArtist.IsEnabled = !busy && !maintenanceRunning && !queued && count > 0;
            applyArtist.Content = queued ? "Album queued" : count > 0 ? "Set artists on " + count + (count == 1 ? " track" : " tracks") : "Set track artists";
            artistSummary.Text = album == null ? "Select an album to review its track artists." : !valid ? "No single main artist was found. Enter the name to use." :
                count == 0 ? "Every track already uses this artist. No cleanup needed." : count + " of " + album.Tracks.Count + " tracks will change to " + name;
            artistSummary.Tip(artistSummary.Text);
            foreach (var row in artistRows)
            {
                bool changes = valid && ArtistRules.NeedsChangeNormalized(row.Track, normalized);
                row.Set(changes ? name : valid ? "Unchanged" : "Enter an artist", changes);
            }
            UpdateArtistLocations();
        }

        internal IReadOnlyList<ArtistRow> ArtistRows => artistRows;

        private void UpdateArtistLocations()
        {
            openArtistAlbum.IsEnabled = albums.SelectedAlbum != null;
            showArtistTrack.IsEnabled = artistPreview.SelectedItem is ArtistRow;
        }

        internal Task ApplyArtistAsync()
        {
            var album = albums.SelectedAlbum; string name = (mainArtist.Text ?? "").Trim();
            if (busy || maintenanceRunning || album == null || queuedAlbums.Contains(album.Key) || !ArtistRules.Specific(name) || name.Length > 300 || name.Any(char.IsControl)) return Task.CompletedTask;
            if (!album.Tracks.Any(t => ArtistRules.NeedsChange(t, name))) return Task.CompletedTask;
            return EnqueueJob(new CoverJob { Album = album, Artist = name, Source = "Track artists" });
        }

        private void UpdateLibraryTools()
        {
            albumFilter.IsEnabled = albumSort.IsEnabled = cleanupTab.IsEnabled = everyTab.IsEnabled = !busy;
            UpdateBadges();
            if (showingArtists) UpdateArtistPreview();
        }

        internal ScanResult Library => library;
        internal YouTubeMusicPage YouTube => youtubePage;
        internal Online.IYouTubeMusic YouTubeMusicForTest;
        internal Func<string, string, System.Threading.CancellationToken, Action<double>, string> DownloadForTest;

        /// <summary>Opens the YouTube Music page for the low-bitrate albums of a library check (or returns to the open one).</summary>
        internal YouTubeMusicPage ShowUpgrade(LibraryCheckResult result)
        {
            if (youtubePage != null) { ShowPage(Page.YouTube); return youtubePage; }
            youtubePage = new YouTubeMusicPage(Root, library.Albums.ToArray(), result,
                () => !busy && !queueRunning && !maintenanceRunning && pending == null && (healthPage == null || !healthPage.IsScanning),
                value => { maintenanceRunning = value; UpdateQueueControls(); },
                async root => { lastEdit = null; Root = root; await ScanAsync(); },
                info => StartExplorer(info), YouTubeMusicForTest, DownloadForTest);
            AddPage(youtubePage);
            nav[Page.YouTube].Button.IsVisible = true;
            ShowPage(Page.YouTube);
            return youtubePage;
        }

        /// <summary>Shows Library health (checking again when the library changed). Null until a scan has found albums.</summary>
        internal HealthPage CheckTags()
        {
            if (busy || !EnsureHealth()) return null;
            ShowPage(Page.Health);
            return healthPage;
        }

        internal async Task FindDuplicatesAsync()
        {
            if (busy || maintenanceRunning || library.Albums.Count == 0 || !EnsureHealth()) return;
            ShowPage(Page.Health);
            await healthPage.FindDuplicatesAsync();
        }
    }
}
