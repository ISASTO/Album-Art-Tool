using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using AlbumArtTool.Core;
using AlbumArtTool.Online;

namespace AlbumArtTool
{
    internal sealed partial class MainForm : Form
    {
        internal static readonly Color Surface = Color.FromArgb(23, 24, 27);
        internal static readonly Color Ink = Color.FromArgb(237, 239, 241);
        internal static readonly Color Muted = Color.FromArgb(163, 169, 175);
        internal static readonly Color Accent = Color.FromArgb(138, 221, 196);
        private readonly TextBox folder = new TextBox();
        private readonly TextBox search = new TextBox();
        private readonly Button browse = Button("Choose folder…");
        private readonly Button scan = Button("Rescan");
        private readonly Button missingTab = Button("Add missing album art");
        private readonly Button existingTab = Button("Change existing album art");
        private readonly Button choose = Button("Choose image…");
        private readonly Button apply = Button("Apply cover");
        private readonly Button undo = Button("Undo last change");
        private readonly Button openFolder = Button("Open album folder");
        private readonly Button issuesButton = Button("Details");
        private readonly AlbumList albums = new AlbumList();
        private readonly ICoverSearch coverSearch;
        private readonly CoverSearchPanel online;
        private readonly PictureBox cover = new PictureBox();
        private readonly Label selectedTitle = Label("Select an album", 13, true);
        private readonly Label selectedArtist = Label("", 10);
        private readonly Label coverCaption = Label("Drop a cover image here", 10);
        private readonly Label detail = Label("", 9);
        private readonly Label selectedPath = Label("", 8.5f);
        private readonly Label empty = Label("Scanning your music…", 12);
        private readonly Label status = Label("Ready", 9);
        private readonly ProgressBar progress = new ProgressBar();
        private readonly Label progressPercent = Label("", 9);
        private readonly CheckBox onlyMissing = new ThemeCheckBox { Text = "Only fill tracks missing covers", Checked = true };
        private readonly CheckBox folderCover = new ThemeCheckBox { Text = "Update folder cover images too", Checked = true };
        private readonly ToolTip tips = new ToolTip();
        private readonly Scanner scanner = new Scanner();
        private readonly CoverEditor editor = new CoverEditor();
        private readonly List<string> issues = new List<string>();
        private ScanResult library = new ScanResult();
        private CancellationTokenSource cancellation;
        private EditResult lastEdit;
        private string lastEditedFolder;
        private byte[] pending;
        private int previewVersion;
        private int progressVersion;
        private bool showingMissing = true, busy, writing, refreshing, hasScanned;

        internal MainForm(string root, bool autoScan = true, ICoverSearch onlineService = null)
        {
            coverSearch = onlineService ?? new CoverSearchService();
            online = new CoverSearchPanel(coverSearch) { UseCover = ApplyOnlineCoverAsync };
            Text = "Album Art Tool";
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(15, 16, 18);
            ForeColor = Ink;
            Font = new Font("Segoe UI", 10f);
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(1200, 820);
            MinimumSize = new Size(1040, 760);
            folder.Text = root;
            folder.ReadOnly = true;
            folder.AccessibleName = "Music folder";
            search.AccessibleName = "Search albums, artists or folders";
            BuildLayout();
            albums.WorkStatus = album => queuedAlbums.Contains(album.Key) ? (currentJob?.Album.Key == album.Key ? "Applying cover…" : "Queued") : null;
            queueButton.Click += (s, e) => ShowQueue();
            browse.Click += async (s, e) =>
            {
                using (var dialog = new FolderBrowserDialog { Description = "Choose the top folder containing your music", SelectedPath = folder.Text, ShowNewFolderButton = false })
                    if (dialog.ShowDialog(this) == DialogResult.OK) { folder.Text = dialog.SelectedPath; await ScanAsync(); }
            };
            scan.Click += async (s, e) => { if (busy) cancellation?.Cancel(); else await ScanAsync(); };
            missingTab.Click += (s, e) => SwitchTab(true);
            existingTab.Click += (s, e) => SwitchTab(false);
            search.TextChanged += (s, e) => { if (!busy) RefreshAlbums(); };
            albums.SelectedIndexChanged += (s, e) => { if (!refreshing) ShowSelection(); };
            albums.CoverDropped += (album, path) => { if (!busy) StageImage(path); };
            choose.Click += (s, e) =>
            {
                using (var dialog = new OpenFileDialog { Filter = "Cover images|*.jpg;*.jpeg;*.png;*.bmp;*.gif|All files|*.*", Title = "Choose an album cover" })
                    if (dialog.ShowDialog(this) == DialogResult.OK) StageImage(dialog.FileName);
            };
            apply.Click += async (s, e) => await ApplyAsync();
            undo.Click += async (s, e) => await UndoAsync();
            onlyMissing.CheckedChanged += (s, e) => UpdateApplyButton();
            openFolder.Click += (s, e) => OpenSelectedFolder();
            issuesButton.Click += (s, e) => ShowIssues();
            foreach (Control target in new Control[] { cover, coverCaption })
            {
                target.AllowDrop = true;
                target.DragEnter += (s, e) => e.Effect = !busy && albums.SelectedAlbum != null && SingleImagePath(e.Data) != null ? DragDropEffects.Copy : DragDropEffects.None;
                target.DragDrop += (s, e) => { string path = SingleImagePath(e.Data); if (!busy && path != null) StageImage(path); };
            }
            cover.Click += (s, e) => { if (choose.Enabled) choose.PerformClick(); };
            FormClosing += (s, e) =>
            {
                if (writing || queueRunning) { e.Cancel = true; status.Text = "Work is still running. Open Queue to clear waiting jobs; the current update will finish safely."; }
                else cancellation?.Cancel();
            };
            KeyPreview = true;
            KeyDown += (s, e) =>
            {
                if (e.Control && e.KeyCode == Keys.F) { search.Focus(); e.SuppressKeyPress = true; }
                if (e.KeyCode == Keys.F5 && !busy) { scan.PerformClick(); e.SuppressKeyPress = true; }
                if (e.Control && e.KeyCode == Keys.Z && undo.Enabled) { undo.PerformClick(); e.SuppressKeyPress = true; }
                if (e.KeyCode == Keys.Escape && pending != null && !busy) { ShowSelection(); e.SuppressKeyPress = true; }
            };
            SetBusy(false);
            SwitchTab(true);
            if (autoScan) Shown += async (s, e) => await ScanAsync();
        }

        private void BuildLayout()
        {
            var outer = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22, 16, 22, 12), ColumnCount = 1, RowCount = 5 };
            outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
            header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            header.Controls.Add(Label("Album Art Tool", 23, true), 0, 0);
            header.Controls.Add(undo, 1, 0);
            header.Controls.Add(queueButton, 2, 0);
            var pathRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
            pathRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            StyleTextBox(folder); pathRow.Controls.Add(folder, 0, 0); pathRow.Controls.Add(browse, 1, 0); pathRow.Controls.Add(scan, 2, 0);
            var tabs = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Padding = new Padding(0, 6, 0, 6) };
            tabs.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            tabs.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 238));
            tabs.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 263));
            tabs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tabs.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 226));
            missingTab.AccessibleRole = existingTab.AccessibleRole = AccessibleRole.PageTab;
            tabs.Controls.Add(missingTab, 0, 0); tabs.Controls.Add(existingTab, 1, 0);
            var searchRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
            searchRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 55)); searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var searchLabel = Label("Search", 9); searchLabel.TextAlign = ContentAlignment.MiddleLeft;
            StyleTextBox(search); searchRow.Controls.Add(searchLabel, 0, 0); searchRow.Controls.Add(search, 1, 0); tabs.Controls.Add(searchRow, 3, 0);
            var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0), RowCount = 1 };
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 580));
            var listPanel = new Panel { Dock = DockStyle.Fill, BackColor = Surface, Margin = new Padding(0, 0, 16, 0) };
            albums.Dock = DockStyle.Fill; empty.Dock = DockStyle.Fill; empty.TextAlign = ContentAlignment.MiddleCenter;
            empty.ForeColor = Muted; empty.Padding = new Padding(35); empty.BackColor = Surface;
            listPanel.Controls.Add(albums); listPanel.Controls.Add(empty);
            var sidebar = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Surface, Padding = new Padding(12), ColumnCount = 1, RowCount = 5, Margin = Padding.Empty };
            sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 29));
            sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 23));
            sidebar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 141));
            sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            selectedTitle.AutoEllipsis = selectedArtist.AutoEllipsis = true;
            var manual = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 8, 0, 0) };
            manual.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            manual.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145)); manual.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var preview = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
            preview.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); preview.RowStyles.Add(new RowStyle(SizeType.Absolute, 19)); preview.RowStyles.Add(new RowStyle(SizeType.Absolute, 19));
            cover.Dock = DockStyle.Fill; cover.SizeMode = PictureBoxSizeMode.Zoom; cover.BackColor = Color.FromArgb(31, 33, 36);
            cover.Margin = new Padding(4, 0, 8, 2); cover.Cursor = Cursors.Hand; cover.AccessibleName = "Album cover preview and image drop target";
            coverCaption.TextAlign = ContentAlignment.MiddleCenter; coverCaption.ForeColor = Muted; coverCaption.Font = new Font("Segoe UI", 8); coverCaption.AutoEllipsis = true;
            detail.ForeColor = Muted; detail.TextAlign = ContentAlignment.MiddleCenter; detail.Font = new Font("Segoe UI", 8); detail.AutoEllipsis = true;
            preview.Controls.Add(cover, 0, 0); preview.Controls.Add(coverCaption, 0, 1); preview.Controls.Add(detail, 0, 2);
            var editing = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = Padding.Empty };
            foreach (int height in new[] { 32, 28, 28, 36 }) editing.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            foreach (var check in new[] { onlyMissing, folderCover })
            { check.Dock = DockStyle.Fill; check.Font = new Font("Segoe UI", 9); check.ForeColor = Ink; check.Margin = new Padding(5, 0, 0, 0); }
            editing.Controls.Add(choose, 0, 0); editing.Controls.Add(onlyMissing, 0, 1); editing.Controls.Add(folderCover, 0, 2); editing.Controls.Add(apply, 0, 3);
            manual.Controls.Add(preview, 0, 0); manual.Controls.Add(editing, 1, 0);
            var path = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            path.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); path.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); path.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145));
            selectedPath.ForeColor = Muted; selectedPath.AutoEllipsis = true; selectedPath.TextAlign = ContentAlignment.MiddleLeft;
            path.Controls.Add(selectedPath, 0, 0); path.Controls.Add(openFolder, 1, 0);
            sidebar.Controls.Add(selectedTitle, 0, 0); sidebar.Controls.Add(selectedArtist, 0, 1); sidebar.Controls.Add(online, 0, 2); sidebar.Controls.Add(manual, 0, 3); sidebar.Controls.Add(path, 0, 4);
            body.Controls.Add(listPanel, 0, 0); body.Controls.Add(sidebar, 1, 0);
            var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Padding = new Padding(0, 8, 0, 0) };
            bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190)); bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86));
            status.AutoEllipsis = true; status.TextAlign = ContentAlignment.MiddleLeft; status.ForeColor = Muted;
            status.AccessibleName = "Operation status and track counter";
            progress.Dock = DockStyle.Fill; progress.Margin = new Padding(6, 8, 8, 8); progress.Visible = false;
            progress.AccessibleName = "Scan and update progress";
            progressPercent.TextAlign = ContentAlignment.MiddleRight; progressPercent.AccessibleName = "Progress percentage";
            var progressArea = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            progressArea.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            progressArea.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); progressArea.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 47));
            progressArea.Controls.Add(progress, 0, 0); progressArea.Controls.Add(progressPercent, 1, 0);
            bottom.Controls.Add(status, 0, 0); bottom.Controls.Add(progressArea, 1, 0); bottom.Controls.Add(issuesButton, 2, 0);
            outer.Controls.Add(header, 0, 0); outer.Controls.Add(pathRow, 0, 1); outer.Controls.Add(tabs, 0, 2); outer.Controls.Add(body, 0, 3); outer.Controls.Add(bottom, 0, 4);
            Controls.Add(outer);
            tips.SetToolTip(onlyMissing, "Keeps existing embedded front covers. Uncheck to replace the cover on every track in this album.");
            tips.SetToolTip(folderCover, "Updates recognized cover/folder/front/album/albumart images, or creates cover.jpg. Disabled when multiple albums share a folder.");
            tips.SetToolTip(undo, "Restore files from your most recent edit in this session (Ctrl+Z). Backups remain in .album-art-backups after closing.");
        }

        private static Button Button(string text) => new ThemeButton { Text = text, Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat,
            BackColor = Surface, ForeColor = Ink, Cursor = Cursors.Hand, Margin = new Padding(3), Font = new Font("Segoe UI", 9f), UseVisualStyleBackColor = false };
        private static Label Label(string text, float size, bool bold = false) => new Label { Text = text, Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular), ForeColor = Ink, Margin = new Padding(0), TextAlign = ContentAlignment.MiddleLeft };
        private static void StyleTextBox(TextBox text)
        { text.Dock = DockStyle.Fill; text.BackColor = Surface; text.ForeColor = Ink; text.BorderStyle = BorderStyle.FixedSingle; text.Margin = new Padding(3, 7, 6, 3); }

        private void SwitchTab(bool missing)
        {
            if (busy) return;
            showingMissing = missing;
            onlyMissing.Checked = missing;
            missingTab.BackColor = missing ? Color.FromArgb(37, 65, 59) : Surface;
            existingTab.BackColor = !missing ? Color.FromArgb(37, 65, 59) : Surface;
            missingTab.ForeColor = missing ? Accent : Muted;
            existingTab.ForeColor = !missing ? Accent : Muted;
            RefreshAlbums();
        }

        private void RefreshAlbums(string key = null, bool preserveStaged = false)
        {
            if (IsDisposed) return;
            string previousKey = albums.SelectedAlbum?.Key;
            byte[] previousImage = preserveStaged ? pending : null;
            key = key ?? albums.SelectedAlbum?.Key;
            string query = search.Text.Trim();
            var visible = library.Albums.Where(a => showingMissing ? a.MissingCount > 0 : a.HasAnyCover)
                .Where(a => query.Length == 0 || (a.Title + " " + a.Artist + " " + a.Folder).IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0).ToList();
            refreshing = true;
            albums.SetAlbums(visible, key);
            refreshing = false;
            missingTab.Text = "Add missing album art (" + library.Albums.Count(a => a.MissingCount > 0) + ")";
            existingTab.Text = "Change existing album art (" + library.Albums.Count(a => a.HasAnyCover) + ")";
            empty.Visible = visible.Count == 0;
            albums.Visible = !empty.Visible;
            empty.Text = query.Length > 0 ? "No albums match your search." : !hasScanned ? "Scan a folder to find your albums." :
                library.Albums.Count == 0 ? "No supported music files found.\nChoose a folder containing your albums." : showingMissing ?
                "Every scanned track has cover art.\nUse the other tab to change a cover." : "No existing covers found yet.\nAdd your first cover in the other tab.";
            if (empty.Visible) empty.BringToFront();
            ShowSelection();
            if (previousImage != null && albums.SelectedAlbum?.Key == previousKey) SetPendingImage(previousImage);
        }

        private void ShowSelection()
        {
            int version = ++previewVersion;
            pending = null;
            var album = albums.SelectedAlbum;
            selectedTitle.Text = album?.Title ?? "Select an album";
            selectedArtist.Text = album?.Artist ?? "";
            selectedArtist.ForeColor = Muted;
            selectedPath.Text = album?.Folder ?? "";
            tips.SetToolTip(selectedTitle, album?.Title ?? ""); tips.SetToolTip(selectedPath, album?.Folder ?? "");
            detail.Text = album == null ? "" : album.Tracks.Count + " tracks · " + album.MissingCount + " missing covers";
            coverCaption.Text = album?.Thumbnail != null ? (album.PreviewTrack != null ? "Current embedded cover" : "Current folder cover") : "Drop a cover image here";
            SetPreview(album?.Thumbnail);
            if (album?.Thumbnail != null) _ = LoadPreviewAsync(album, version);
            bool shared = album != null && library.Albums.Count(a => string.Equals(a.Folder, album.Folder, StringComparison.OrdinalIgnoreCase)) > 1;
            folderCover.Enabled = !busy && album != null && !shared;
            folderCover.Text = shared ? "Folder shared by multiple albums" : "Update folder cover images too";
            choose.Enabled = onlyMissing.Enabled = album != null && !busy && !queuedAlbums.Contains(album.Key);
            openFolder.Enabled = album != null && !busy;
            folderCover.Enabled &= album != null && !queuedAlbums.Contains(album.Key);
            UpdateApplyButton();
            online.SetAlbum(album, !busy && (album == null || !queuedAlbums.Contains(album.Key)));
        }

        private async Task LoadPreviewAsync(Album album, int version)
        {
            try
            {
                byte[] bytes = await Task.Run(() =>
                {
                    byte[] original;
                    if (album.PreviewTrack != null)
                    {
                        using (var audio = TagLib.File.Create(album.PreviewTrack, TagLib.ReadStyle.None)) original = Artwork.GetCover(audio.Tag);
                    }
                    else original = album.FolderCovers.Count > 0 ? Artwork.LoadFile(album.FolderCovers[0]) : null;
                    return original == null ? null : Artwork.Normalize(original, 480);
                });
                if (!IsDisposed && version == previewVersion && pending == null && bytes != null) SetPreview(bytes);
            }
            catch (Exception e) when (Scanner.IsFileError(e) || Artwork.IsImageError(e) || e is TagLib.CorruptFileException || e is TagLib.UnsupportedFormatException)
            { /* Retain the scan thumbnail if the source has become unavailable. */ }
        }

        private void SetPreview(byte[] bytes)
        {
            var old = cover.Image;
            cover.Image = bytes == null ? null : Artwork.Decode(bytes);
            old?.Dispose();
        }

        internal void StageImage(string path)
        {
            if (busy || albums.SelectedAlbum == null || queuedAlbums.Contains(albums.SelectedAlbum.Key)) return;
            try
            {
                SetPendingImage(Artwork.Normalize(Artwork.LoadFile(path)));
                status.Text = "Review the cover, then click Apply. Press Esc to discard the preview.";
            }
            catch (Exception e) when (Scanner.IsFileError(e) || Artwork.IsImageError(e))
            { MessageBox.Show(this, "That image could not be opened. Use a JPG, PNG, BMP or GIF.\n\n" + e.Message, "Choose another image", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        }

        private void SetPendingImage(byte[] image)
        {
            ++previewVersion; pending = image; SetPreview(image);
            coverCaption.Text = "New cover · ready to apply";
            using (var decoded = Artwork.Decode(image)) detail.Text = decoded.Width + " × " + decoded.Height + " · " + image.Length / 1024 + " KB";
            UpdateApplyButton();
        }

        internal static string SingleImagePath(IDataObject data)
        {
            if (data == null || !data.GetDataPresent(DataFormats.FileDrop)) return null;
            var paths = data.GetData(DataFormats.FileDrop) as string[];
            if (paths == null || paths.Length != 1 || !System.IO.File.Exists(paths[0])) return null;
            string extension = Path.GetExtension(paths[0]).ToLowerInvariant();
            return new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif" }.Contains(extension) ? paths[0] : null;
        }

        private void UpdateApplyButton()
        {
            var album = albums.SelectedAlbum;
            int count = album == null ? 0 : onlyMissing.Checked ? album.MissingCount : album.Tracks.Count;
            bool queued = album != null && queuedAlbums.Contains(album.Key);
            apply.Text = queued ? "Cover queued" : count > 0 ? "Apply cover to " + count + (count == 1 ? " track" : " tracks") : "Apply cover";
            bool hasImage = pending != null || album?.PreviewTrack != null || album?.FolderCovers.Count > 0;
            apply.Enabled = !busy && !queued && hasImage && album != null && (count > 0 || (folderCover.Enabled && folderCover.Checked));
            tips.SetToolTip(apply, queued ? "This album is already in the queue. You can work on another album." :
                pending != null ? "Add the chosen cover to the work queue." : "Apply the displayed cover using the full-size image already in your album.");
            apply.BackColor = apply.Enabled ? Accent : Color.FromArgb(39, 44, 44);
            apply.ForeColor = apply.Enabled ? Color.FromArgb(16, 33, 29) : Muted;
        }

        private void SetBusy(bool value, bool edit = false)
        {
            progressVersion++;
            busy = value; writing = value && edit;
            browse.Enabled = missingTab.Enabled = existingTab.Enabled = albums.Enabled = !value;
            search.ReadOnly = value; search.ForeColor = value ? Muted : Ink;
            scan.Enabled = !writing;
            scan.Text = value && !edit ? "Stop scan" : "Rescan";
            undo.Enabled = !value && lastEdit != null && lastEdit.Entries.Count > 0;
            progress.Visible = value;
            progressPercent.Visible = value; progressPercent.Text = "";
            progress.Style = value && !edit ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
            progress.Value = 0;
            choose.Enabled = openFolder.Enabled = onlyMissing.Enabled = !value && albums.SelectedAlbum != null;
            if (value) folderCover.Enabled = false;
            online.SetActive(!value);
            if (value && !edit)
            {
                empty.Text = "Scanning your music…\nAlbums appear when the scan finishes.";
                albums.Visible = false; empty.Visible = true; empty.BringToFront();
            }
            issuesButton.Enabled = issues.Count > 0;
            UpdateApplyButton();
        }

        private IProgress<ProgressInfo> Reporter()
        {
            int operation = progressVersion;
            return new Progress<ProgressInfo>(p =>
            {
                if (IsDisposed || !busy || operation != progressVersion) return;
                status.Text = p.Message;
                if (p.Total > 0)
                {
                    progress.Style = ProgressBarStyle.Continuous;
                    progress.Value = Math.Max(0, Math.Min(100, (int)(100.0 * p.Completed / p.Total)));
                    progressPercent.Text = progress.Value + "%";
                }
                else { progress.Style = ProgressBarStyle.Marquee; progressPercent.Text = ""; }
            });
        }

        internal async Task ScanAsync()
        {
            if (busy || queueRunning) return;
            string root = folder.Text;
            cancellation?.Dispose(); cancellation = new CancellationTokenSource();
            var token = cancellation.Token;
            issues.Clear(); SetBusy(true); status.Text = "Scanning music folders…";
            var reporter = Reporter();
            try
            {
                var result = await Task.Run(() => scanner.Scan(root, token, reporter));
                if (IsDisposed) return;
                library = result; hasScanned = true; issues.AddRange(result.Issues);
                status.Text = (result.Cancelled ? "Scan stopped · " : "") + result.Albums.Count + " albums · " + result.FilesRead + " tracks" +
                    (issues.Count > 0 ? " · " + issues.Count + " issues (Details)" : " · Drop an image onto an album to get started.");
                RefreshAlbums();
            }
            catch (Exception e) { if (!IsDisposed) { issues.Add(e.Message); status.Text = "Scan could not finish. See Details."; RefreshAlbums(); } }
            finally { if (!IsDisposed) { SetBusy(false); ShowSelection(); } }
        }

        internal Task ApplyAsync()
        {
            var album = albums.SelectedAlbum;
            if (busy || album == null || queuedAlbums.Contains(album.Key)) return Task.CompletedTask;
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
            catch (Exception e) { issues.Add(e.Message); issuesButton.Enabled = true; status.Text = "Could not queue this cover. See Details."; return Task.CompletedTask; }
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
            if (busy || queueRunning || lastEdit == null) return;
            SetBusy(true, true); issues.Clear();
            var reporter = Reporter();
            try
            {
                var result = await Task.Run(() => editor.Undo(lastEdit, reporter));
                issues.AddRange(result.Issues);
                await RefreshFolderAsync(lastEditedFolder, null);
                status.Text = result.Restored + " original files restored" + (issues.Count > 0 ? " · Some files could not be restored. See Details." : ".");
            }
            catch (Exception e) { issues.Add(e.Message); status.Text = "Undo could not finish. See Details."; }
            finally { SetBusy(false); ShowSelection(); }
        }

        private async Task RefreshFolderAsync(string path, string key, bool preserveStaged = false)
        {
            var updated = await Task.Run(() => scanner.Scan(path, CancellationToken.None, recursive: false));
            library.Albums.RemoveAll(a => string.Equals(a.Folder, path, StringComparison.OrdinalIgnoreCase));
            library.Albums.AddRange(updated.Albums); issues.AddRange(updated.Issues);
            library.Albums.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Artist + a.Title + a.Folder, b.Artist + b.Title + b.Folder));
            RefreshAlbums(key, preserveStaged);
        }

        private void OpenSelectedFolder()
        {
            if (albums.SelectedAlbum == null) return;
            try { Process.Start(new ProcessStartInfo(albums.SelectedAlbum.Folder) { UseShellExecute = true }); }
            catch (Exception e) { MessageBox.Show(this, e.Message, "Could not open folder"); }
        }

        private void ShowIssues()
        {
            using (var dialog = new Form { Text = "Scan and update details", Size = new Size(850, 450), StartPosition = FormStartPosition.CenterParent, BackColor = Surface, ForeColor = Ink })
            using (var text = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill,
                BackColor = Surface, ForeColor = Ink, Font = new Font("Consolas", 10), Text = string.Join(Environment.NewLine + Environment.NewLine, issues) })
            { dialog.Controls.Add(text); dialog.ShowDialog(this); }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                var source = cancellation;
                cancellation = null;
                source?.Cancel(); source?.Dispose();
                var image = cover.Image;
                cover.Image = null;
                image?.Dispose(); tips.Dispose();
                online.Dispose(); coverSearch.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
