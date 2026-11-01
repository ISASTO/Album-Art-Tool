using System;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using AlbumArtTool.Core;

namespace AlbumArtTool
{
    internal sealed partial class MainForm
    {
        private readonly Button artistsTab = Button("Track artists");
        private readonly Button findDuplicates = Button("Find duplicates…");
        private readonly ComboBox albumFilter = new ComboBox(), albumSort = new ComboBox();
        private readonly TextBox mainArtist = new TextBox();
        private readonly Button applyArtist = Button("Set track artists");
        private readonly Label artistHeading = Label("Select an album", 13, true), artistSummary = Label("", 10);
        private readonly ListView artistPreview = new ListView();
        private Control artistPanel, coverPanel;
        private bool showingArtists;
        private DuplicateWindow duplicatesWindow;

        private Control BuildLibraryToolbar()
        {
            var bar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 6, RowCount = 1, Margin = Padding.Empty };
            foreach (int width in new[] { 175, 45, 245, 43, 210 }) bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, width));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); bar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            foreach (var combo in new[] { albumFilter, albumSort })
            { combo.DropDownStyle = ComboBoxStyle.DropDownList; combo.Dock = DockStyle.Fill; combo.BackColor = Surface; combo.ForeColor = Ink; combo.FlatStyle = FlatStyle.Flat; combo.Font = new Font("Segoe UI", 9); combo.Margin = new Padding(3, 8, 8, 3); }
            albumFilter.AccessibleName = "Album filter"; albumSort.AccessibleName = "Album sort order";
            albumFilter.Items.AddRange(new object[] { "All albums in this tab", "Artist cleanup available" });
            albumSort.Items.AddRange(new object[] { "Artist and album", "Album title", "Artist changes first" });
            albumFilter.SelectedIndex = albumSort.SelectedIndex = 0;
            albumFilter.SelectedIndexChanged += (s, e) => { if (!busy) RefreshAlbums(); };
            albumSort.SelectedIndexChanged += (s, e) => { if (!busy) RefreshAlbums(); };
            findDuplicates.Click += async (s, e) => await FindDuplicatesAsync();
            bar.Controls.Add(findDuplicates, 0, 0); bar.Controls.Add(Label("Show", 9), 1, 0); bar.Controls.Add(albumFilter, 2, 0);
            bar.Controls.Add(Label("Sort", 9), 3, 0); bar.Controls.Add(albumSort, 4, 0);
            return bar;
        }

        private Control BuildArtistPanel()
        {
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(15), BackColor = Surface, Margin = Padding.Empty };
            foreach (int height in new[] { 32, 60, 30, 38, 40 }) panel.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            panel.Controls.Add(artistHeading, 0, 0);
            var help = Label("Set every track's Artist and Album artist to the main artist. Review the name below; titles, artwork and audio stay as they are.", 10); help.ForeColor = Muted;
            panel.Controls.Add(help, 0, 1); panel.Controls.Add(Label("Main artist", 10, true), 0, 2);
            StyleTextBox(mainArtist); mainArtist.AccessibleName = "Main artist for this album";
            mainArtist.TextChanged += (s, e) => UpdateArtistPreview();
            panel.Controls.Add(mainArtist, 0, 3); artistSummary.ForeColor = Muted; panel.Controls.Add(artistSummary, 0, 4);
            artistPreview.Dock = DockStyle.Fill; artistPreview.View = View.Details; artistPreview.FullRowSelect = true; artistPreview.MultiSelect = false;
            artistPreview.BackColor = Surface; artistPreview.ForeColor = Ink; artistPreview.BorderStyle = BorderStyle.FixedSingle; artistPreview.HideSelection = false;
            artistPreview.Columns.Add("Track", 200); artistPreview.Columns.Add("Current artist", 235); artistPreview.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            artistPreview.AccessibleName = "Current track artists"; artistPreview.ShowItemToolTips = true;
            panel.Controls.Add(artistPreview, 0, 5); panel.Controls.Add(applyArtist, 0, 6);
            applyArtist.Click += async (s, e) => await ApplyArtistAsync();
            return panel;
        }

        private void SwitchArtists()
        {
            if (busy) return;
            showingArtists = true;
            if (albumFilter.SelectedIndex != 1) albumFilter.SelectedIndex = 1;
            StyleTabs(); RefreshAlbums();
        }
        private void StyleTabs()
        {
            foreach (var tab in new[] { missingTab, existingTab, artistsTab })
            {
                bool selected = tab == artistsTab ? showingArtists : !showingArtists && (tab == missingTab ? showingMissing : !showingMissing);
                tab.BackColor = selected ? Color.FromArgb(37, 65, 59) : Surface; tab.ForeColor = selected ? Accent : Muted;
            }
            if (coverPanel != null) coverPanel.Visible = !showingArtists;
            if (artistPanel != null) artistPanel.Visible = showingArtists;
        }
        private void ShowArtistSelection()
        {
            var album = albums.SelectedAlbum;
            artistHeading.Text = album?.Title ?? "Select an album"; artistHeading.AutoEllipsis = true;
            mainArtist.Text = album?.MainArtist ?? "";
            artistPreview.BeginUpdate(); artistPreview.Items.Clear();
            if (album != null)
                foreach (var track in album.Tracks)
                { var item = new ListViewItem(track.Title) { ToolTipText = track.Path + "\nAlbum artist: " + string.Join(", ", track.AlbumArtists) }; item.SubItems.Add(track.ArtistText); artistPreview.Items.Add(item); }
            artistPreview.EndUpdate(); UpdateArtistPreview();
        }
        private void UpdateArtistPreview()
        {
            var album = albums.SelectedAlbum; string name = mainArtist.Text.Trim();
            bool valid = ArtistRules.Specific(name) && name.Length <= 300 && !name.Any(char.IsControl);
            int count = valid && album != null ? album.Tracks.Count(t => ArtistRules.NeedsChange(t, name)) : 0;
            bool queued = album != null && queuedAlbums.Contains(album.Key);
            mainArtist.ReadOnly = busy || queued || album == null;
            applyArtist.Enabled = !busy && !queued && count > 0;
            applyArtist.Text = queued ? "Album queued" : count > 0 ? "Set artists on " + count + (count == 1 ? " track" : " tracks") : "Set track artists";
            artistSummary.Text = album == null ? "Select an album to review its track artists." : !valid ? "No single main artist was found. Enter the name to use." :
                count == 0 ? "Every track already uses this artist and album artist." : count + " of " + album.Tracks.Count + " tracks will change to " + name;
            tips.SetToolTip(artistSummary, artistSummary.Text); artistSummary.AutoEllipsis = true;
        }
        internal Task ApplyArtistAsync()
        {
            var album = albums.SelectedAlbum; string name = mainArtist.Text.Trim();
            if (busy || album == null || queuedAlbums.Contains(album.Key) || !ArtistRules.Specific(name) || name.Length > 300 || name.Any(char.IsControl)) return Task.CompletedTask;
            if (!album.Tracks.Any(t => ArtistRules.NeedsChange(t, name))) return Task.CompletedTask;
            return EnqueueJob(new CoverJob { Album = album, Artist = name, Source = "Track artists" });
        }
        private void UpdateLibraryTools()
        {
            artistsTab.Enabled = albumFilter.Enabled = albumSort.Enabled = !busy;
            findDuplicates.Enabled = !busy && hasScanned && library.Albums.Count > 0;
            UpdateArtistPreview();
        }
        internal async Task FindDuplicatesAsync()
        {
            if (duplicatesWindow != null && !duplicatesWindow.IsDisposed) { duplicatesWindow.Activate(); return; }
            if (busy || library.Albums.Count == 0) return;
            duplicatesWindow = new DuplicateWindow(library.Albums.ToArray());
            duplicatesWindow.Show(this);
            await duplicatesWindow.ScanAsync();
        }
    }
}
