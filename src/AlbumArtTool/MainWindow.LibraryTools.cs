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
        private readonly Button artistsTab = Kit.Button("Track artists", "tab");
        private readonly Button findDuplicates = Kit.Button("Find duplicates…");
        private readonly Button checkTags = Kit.Button("Check library…");
        private LibraryCheckWindow checkWindow;
        private readonly Button manageLibrary = Kit.Button("Manage library…", "primary");
        private LibraryManagerWindow managerWindow;
        private bool maintenanceRunning;
        private readonly ComboBox albumFilter = new ComboBox(), albumSort = new ComboBox();
        private readonly TextBox mainArtist = new TextBox();
        private readonly Button applyArtist = Kit.Button("Set track artists", "primary");
        private readonly Button openArtistAlbum = Kit.Button("Open album folder"), showArtistTrack = Kit.Button("Show selected track");
        private readonly TextBlock artistHeading = Kit.Label("Select an album", 17.33, true), artistSummary = Kit.Label("", Kit.Body, cls: "muted");
        private readonly ListBox artistPreview = new ListBox { Classes = { "grid" } };
        private List<ArtistRow> artistRows = new List<ArtistRow>();
        private Control artistPanel, coverPanel;
        private bool showingArtists, refreshingArtist, changingAlbumView;
        private DuplicateWindow duplicatesWindow;

        internal sealed class ArtistRow
        {
            internal Track Track;
            internal string After;
            internal bool Changes;
            internal event Action Changed;
            internal void Set(string after, bool changes) { if (after == After && changes == Changes) return; After = after; Changes = changes; Changed?.Invoke(); }
        }

        private Control BuildLibraryToolbar()
        {
            foreach (var combo in new[] { albumFilter, albumSort }) { combo.HorizontalAlignment = HorizontalAlignment.Stretch; }
            albumFilter.Named("Album filter"); albumSort.Named("Album sort order");
            albumFilter.Items.Add("All albums in this tab"); albumFilter.Items.Add("Artist cleanup available");
            albumSort.Items.Add("Artist and album"); albumSort.Items.Add("Album title"); albumSort.Items.Add("Artist changes first");
            albumFilter.SelectedIndex = albumSort.SelectedIndex = 0;
            albumFilter.SelectionChanged += (s, e) => { if (!busy && !changingAlbumView) RefreshAlbums(); };
            albumSort.SelectionChanged += (s, e) => { if (!busy && !changingAlbumView) RefreshAlbums(); };
            findDuplicates.Click += async (s, e) => await FindDuplicatesAsync();
            checkTags.Click += (s, e) => CheckTags();
            checkTags.Tip("List songs missing a Title tag, songs with more than one embedded picture, and low-bitrate songs.");
            manageLibrary.Click += (s, e) => ShowLibraryManager();
            var showLabel = Kit.Label("SHOW", cls: "caption"); var sortLabel = Kit.Label("SORT BY", cls: "caption");
            showLabel.Margin = sortLabel.Margin = new Thickness(8, 0, 0, 0);
            return Kit.Grid("*,*", "18,*").Add(showLabel.Cell(0, 0), sortLabel.Cell(0, 1), albumFilter.Cell(1, 0), albumSort.Cell(1, 1));
        }

        private Control BuildArtistPanel()
        {
            artistHeading.Named("Selected album for artist cleanup");
            var help = Kit.Label("Collapse guest credits into the main artist. Already-correct track artists are left alone; review the proposed changes below.", Kit.Body, cls: "muted");
            help.TextWrapping = TextWrapping.Wrap; help.TextTrimming = TextTrimming.None;
            mainArtist.Named("Main artist for this album");
            mainArtist.TextChanged += (s, e) => { if (!refreshingArtist) UpdateArtistPreview(); };
            artistPreview.Named("Current track artists");
            artistPreview.ItemTemplate = new FuncDataTemplate<ArtistRow>((row, scope) => new ArtistRowView(), true);
            artistPreview.SelectionChanged += (s, e) => UpdateArtistLocations();
            var header = Kit.Grid("36*,34*,30*", "28").Add(Kit.Label("Track", Kit.Small, cls: "muted").Cell(0, 0), Kit.Label("Current artist", Kit.Small, cls: "muted").Cell(0, 1), Kit.Label("After cleanup", Kit.Small, cls: "muted").Cell(0, 2));
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
            var locations = Kit.Grid("*,*", "*").Add(openArtistAlbum.Cell(0, 0), showArtistTrack.Cell(0, 1));
            applyArtist.Click += async (s, e) => await ApplyArtistAsync();
            var panel = Kit.Grid("*", "32,60,30,38,40,*,36,44").Add(artistHeading.Cell(0, 0), help.Cell(1, 0), Kit.Label("Main artist", Kit.Body, true).Cell(2, 0),
                mainArtist.Cell(3, 0), artistSummary.Cell(4, 0), trackSection.Cell(5, 0), locations.Cell(6, 0), applyArtist.Cell(7, 0));
            panel.Margin = new Thickness(15);
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
        }

        private void StyleTabs()
        {
            foreach (var tab in new[] { missingTab, existingTab, artistsTab })
            {
                bool selected = tab == artistsTab ? showingArtists : !showingArtists && (tab == missingTab ? showingMissing : !showingMissing);
                tab.Classes.Set("selected", selected);
            }
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
            artistsTab.IsEnabled = albumFilter.IsEnabled = albumSort.IsEnabled = !busy;
            findDuplicates.IsEnabled = !busy && !maintenanceRunning && hasScanned && library.Albums.Count > 0;
            checkTags.IsEnabled = !busy && hasScanned && library.Albums.Count > 0;
            if (showingArtists) UpdateArtistPreview();
        }

        private void ShowLibraryManager()
        {
            if (managerWindow != null) { managerWindow.Activate(); return; }
            managerWindow = new LibraryManagerWindow(folder.Text,
                () => !busy && !queueRunning && !maintenanceRunning && pending == null && (duplicatesWindow == null || !duplicatesWindow.IsScanning),
                value => { maintenanceRunning = value; UpdateQueueControls(); },
                async root => { lastEdit = null; folder.Text = root; await ScanAsync(); });
            managerWindow.Closed += (s, e) => managerWindow = null;
            managerWindow.Show(this);
        }

        internal LibraryCheckWindow CheckTags()
        {
            if (checkWindow != null) { checkWindow.Check(); checkWindow.Activate(); return checkWindow; }
            if (busy || library.Albums.Count == 0) return null;
            var window = checkWindow = new LibraryCheckWindow(() => library.Albums.ToArray(), info => StartExplorer(info));
            window.Closed += (s, e) => { if (checkWindow == window) checkWindow = null; };
            window.Show(this);
            return window;
        }

        internal async Task FindDuplicatesAsync()
        {
            if (duplicatesWindow != null) { duplicatesWindow.Activate(); return; }
            if (busy || maintenanceRunning || library.Albums.Count == 0) return;
            var window = new DuplicateWindow(library.Albums.ToArray(), () => library.Albums.ToArray(), info => StartExplorer(info));
            duplicatesWindow = window;
            window.Closed += (s, e) => { if (duplicatesWindow == window) duplicatesWindow = null; };
            window.Show(this);
            await window.ScanAsync();
        }
    }
}
