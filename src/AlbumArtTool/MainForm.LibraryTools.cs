using System;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using AlbumArtTool.Design;
using AlbumArtTool.Core;

namespace AlbumArtTool
{
    internal sealed partial class MainForm
    {
        private readonly Button artistsTab = Button("Track artists");
        private readonly Button findDuplicates = Button("Find duplicates…");
        private readonly Button manageLibrary = Button("Manage library…");
        private LibraryManagerWindow managerWindow;
        private bool maintenanceRunning;
        private readonly ComboBox albumFilter = new ThemeComboBox(), albumSort = new ThemeComboBox();
        private readonly TextBox mainArtist = new TextBox();
        private readonly Button applyArtist = Button("Set track artists");
        private readonly Button openArtistAlbum = Button("Open album folder"), showArtistTrack = Button("Show selected track");
        private readonly Label artistHeading = Label("Select an album", 13, true), artistSummary = Label("", 10);
        private readonly ListView artistPreview = new ListView();
        private Control artistPanel, coverPanel;
        private bool showingArtists;
        private bool refreshingArtist;
        private DuplicateWindow duplicatesWindow;

        private Control BuildLibraryToolbar()
        {
            var bar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = Padding.Empty };
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            bar.RowStyles.Add(new RowStyle(SizeType.Absolute, 18)); bar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            foreach (var combo in new[] { albumFilter, albumSort })
            { combo.DropDownStyle = ComboBoxStyle.DropDownList; combo.Dock = DockStyle.Fill; combo.BackColor = Surface; combo.ForeColor = Ink; combo.FlatStyle = FlatStyle.Flat; combo.Font = new Font("Segoe UI", 9); combo.Margin = new Padding(3, 0, 4, 6); }
            albumFilter.AccessibleName = "Album filter"; albumSort.AccessibleName = "Album sort order";
            albumFilter.Items.AddRange(new object[] { "All albums in this tab", "Artist cleanup available" });
            albumSort.Items.AddRange(new object[] { "Artist and album", "Album title", "Artist changes first" });
            albumFilter.SelectedIndex = albumSort.SelectedIndex = 0;
            albumFilter.SelectedIndexChanged += (s, e) => { if (!busy && !changingAlbumView) RefreshAlbums(); };
            albumSort.SelectedIndexChanged += (s, e) => { if (!busy && !changingAlbumView) RefreshAlbums(); };
            findDuplicates.Click += async (s, e) => await FindDuplicatesAsync();
            manageLibrary.Click += (s, e) => ShowLibraryManager();
            ((ThemeButton)manageLibrary).Primary = true;
            var showLabel = Label("SHOW", 7.8f, true); showLabel.ForeColor = Muted;
            var sortLabel = Label("SORT BY", 7.8f, true); sortLabel.ForeColor = Muted;
            showLabel.Margin = sortLabel.Margin = new Padding(8, 0, 0, 0);
            bar.Controls.Add(showLabel, 0, 0); bar.Controls.Add(sortLabel, 1, 0); bar.Controls.Add(albumFilter, 0, 1); bar.Controls.Add(albumSort, 1, 1);
            return bar;
        }

        private Control BuildArtistPanel()
        {
            ((ThemeButton)applyArtist).Primary = true;
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 8, Padding = new Padding(15), BackColor = Surface, Margin = Padding.Empty };
            foreach (int height in new[] { 32, 60, 30, 38, 40 }) panel.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 36)); panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            panel.Controls.Add(artistHeading, 0, 0);
            artistHeading.AccessibleName = "Selected album for artist cleanup";
            var help = Label("Collapse guest credits into the main artist. Already-correct track artists are left alone; review the proposed changes below.", 10); help.ForeColor = Muted;
            panel.Controls.Add(help, 0, 1); panel.Controls.Add(Label("Main artist", 10, true), 0, 2);
            StyleTextBox(mainArtist); mainArtist.AccessibleName = "Main artist for this album";
            mainArtist.TextChanged += (s, e) => { if (!refreshingArtist) UpdateArtistPreview(); };
            panel.Controls.Add(mainArtist, 0, 3); artistSummary.ForeColor = Muted; panel.Controls.Add(artistSummary, 0, 4);
            artistPreview.Dock = DockStyle.Fill; artistPreview.View = View.Details; artistPreview.FullRowSelect = true; artistPreview.MultiSelect = false;
            artistPreview.BackColor = Surface; artistPreview.ForeColor = Ink; artistPreview.BorderStyle = BorderStyle.FixedSingle; artistPreview.HideSelection = false;
            artistPreview.Columns.Add("Track", 180); artistPreview.Columns.Add("Current artist", 180); artistPreview.Columns.Add("After cleanup", 150); artistPreview.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            artistPreview.Resize += (s, e) =>
            {
                int width = Math.Max(120, artistPreview.ClientSize.Width);
                artistPreview.Columns[0].Width = width * 36 / 100; artistPreview.Columns[1].Width = width * 34 / 100;
                artistPreview.Columns[2].Width = width - artistPreview.Columns[0].Width - artistPreview.Columns[1].Width;
            };
            artistPreview.AccessibleName = "Current track artists"; artistPreview.ShowItemToolTips = true;
            artistPreview.SelectedIndexChanged += (s, e) => UpdateArtistLocations();
            var locations = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            locations.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); locations.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            openArtistAlbum.AccessibleName = "Open artist-tab album folder"; showArtistTrack.AccessibleName = "Reveal artist-tab selected track";
            openArtistAlbum.Click += (s, e) => OpenSelectedFolder();
            showArtistTrack.Click += (s, e) => { if (artistPreview.SelectedItems.Count > 0 && artistPreview.SelectedItems[0].Tag is Track track) OpenLocation(track.Path, true); };
            tips.SetToolTip(openArtistAlbum, "Open this album's folder in File Explorer.");
            tips.SetToolTip(showArtistTrack, "Select a track above, then open its folder with that song highlighted in File Explorer.");
            locations.Controls.Add(openArtistAlbum, 0, 0); locations.Controls.Add(showArtistTrack, 1, 0);
            var trackSection = new InsetPanel { AccessibleName = "Track artist preview section" }; trackSection.Controls.Add(artistPreview);
            panel.Controls.Add(trackSection, 0, 5); panel.Controls.Add(locations, 0, 6); panel.Controls.Add(applyArtist, 0, 7);
            applyArtist.Click += async (s, e) => await ApplyArtistAsync();
            return panel;
        }

        private bool changingAlbumView;
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
                ((ThemeButton)tab).Selected = selected; ((ThemeButton)tab).Chrome = !selected; tab.BackColor = selected ? Surface : AppTheme.Input; tab.ForeColor = selected ? Accent : Muted; tab.Invalidate();
            }
            if (coverPanel != null) coverPanel.Visible = !showingArtists;
            if (artistPanel != null) artistPanel.Visible = showingArtists;
        }
        private void ShowArtistSelection()
        {
            var album = albums.SelectedAlbum;
            refreshingArtist = true; artistPreview.BeginUpdate();
            try
            {
                artistHeading.Text = album?.Title ?? "Select an album"; artistHeading.AutoEllipsis = true;
                mainArtist.Text = album?.MainArtist ?? ""; artistPreview.Items.Clear();
                if (album != null)
                    foreach (var track in album.Tracks)
                    { var item = new ListViewItem(track.Title) { Tag = track }; item.SubItems.Add(track.ArtistText); item.SubItems.Add(""); artistPreview.Items.Add(item); }
            }
            finally { artistPreview.EndUpdate(); refreshingArtist = false; }
        }
        private void UpdateArtistPreview()
        {
            var album = albums.SelectedAlbum; string name = mainArtist.Text.Trim();
            bool valid = ArtistRules.Specific(name) && name.Length <= 300 && !name.Any(char.IsControl);
            int count = valid && album != null ? album.Tracks.Count(t => ArtistRules.NeedsChange(t, name)) : 0;
            bool queued = album != null && queuedAlbums.Contains(album.Key);
            mainArtist.ReadOnly = busy || maintenanceRunning || queued || album == null;
            applyArtist.Enabled = !busy && !maintenanceRunning && !queued && count > 0;
            applyArtist.Text = queued ? "Album queued" : count > 0 ? "Set artists on " + count + (count == 1 ? " track" : " tracks") : "Set track artists";
            artistSummary.Text = album == null ? "Select an album to review its track artists." : !valid ? "No single main artist was found. Enter the name to use." :
                count == 0 ? "Every track already uses this artist. No cleanup needed." : count + " of " + album.Tracks.Count + " tracks will change to " + name;
            tips.SetToolTip(artistSummary, artistSummary.Text); artistSummary.AutoEllipsis = true;
            artistPreview.BeginUpdate();
            foreach (ListViewItem item in artistPreview.Items)
            {
                var track = (Track)item.Tag; bool changes = valid && ArtistRules.NeedsChange(track, name);
                item.SubItems[2].Text = changes ? name : valid ? "Unchanged" : "Enter an artist";
                item.ForeColor = changes ? Ink : Muted;
                item.ToolTipText = track.Title + "\nCurrent artist: " + track.ArtistText + "\nAfter cleanup: " + (changes ? name : "Unchanged") +
                    (changes && track.AlbumArtists.Length > 0 ? "\nAlbum artist will also be set to " + name : "") + "\n" + track.Path;
            }
            artistPreview.EndUpdate();
            UpdateArtistLocations();
        }
        private void UpdateArtistLocations()
        {
            openArtistAlbum.Enabled = albums.SelectedAlbum != null;
            showArtistTrack.Enabled = artistPreview.SelectedItems.Count > 0 && artistPreview.SelectedItems[0].Tag is Track;
        }
        internal Task ApplyArtistAsync()
        {
            var album = albums.SelectedAlbum; string name = mainArtist.Text.Trim();
            if (busy || maintenanceRunning || album == null || queuedAlbums.Contains(album.Key) || !ArtistRules.Specific(name) || name.Length > 300 || name.Any(char.IsControl)) return Task.CompletedTask;
            if (!album.Tracks.Any(t => ArtistRules.NeedsChange(t, name))) return Task.CompletedTask;
            return EnqueueJob(new CoverJob { Album = album, Artist = name, Source = "Track artists" });
        }
        private void UpdateLibraryTools()
        {
            artistsTab.Enabled = albumFilter.Enabled = albumSort.Enabled = !busy;
            findDuplicates.Enabled = !busy && !maintenanceRunning && hasScanned && library.Albums.Count > 0;
            if (showingArtists) UpdateArtistPreview();
        }
        private void ShowLibraryManager()
        {
            if (managerWindow != null && !managerWindow.IsDisposed) { managerWindow.Activate(); return; }
            managerWindow = new LibraryManagerWindow(folder.Text,
                () => !busy && !queueRunning && !maintenanceRunning && pending == null && (duplicatesWindow == null || !duplicatesWindow.IsScanning),
                value => { maintenanceRunning = value; UpdateQueueControls(); },
                async root => { lastEdit = null; folder.Text = root; await ScanAsync(); });
            managerWindow.FormClosed += (s, e) => managerWindow = null;
            managerWindow.Show(this);
        }
        internal async Task FindDuplicatesAsync()
        {
            if (duplicatesWindow != null && !duplicatesWindow.IsDisposed) { duplicatesWindow.Activate(); return; }
            if (busy || maintenanceRunning || library.Albums.Count == 0) return;
            var window = new DuplicateWindow(library.Albums.ToArray(), () => library.Albums.ToArray());
            duplicatesWindow = window;
            window.FormClosed += (s, e) => { if (duplicatesWindow == window) duplicatesWindow = null; };
            window.Show(this);
            await window.ScanAsync();
        }
    }
}
