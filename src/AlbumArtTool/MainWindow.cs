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
        private readonly Button appearance = Kit.Button("Light mode", "chrome");
        internal Action<ProcessStartInfo> StartExplorer = info => { using (Process.Start(info)) { } };
        private readonly TextBox folder = new TextBox { IsReadOnly = true };
        private readonly TextBox search = new TextBox();
        private readonly Button browse = Kit.Button("Choose folder…");
        private readonly Button scan = Kit.Button("Rescan");
        private readonly Button missingTab = Kit.Button("Add missing album art", "tab");
        private readonly Button existingTab = Kit.Button("Change existing album art", "tab");
        private readonly Button choose = Kit.Button("Choose image…");
        private readonly Button apply = Kit.Button("Apply cover", "primary");
        private readonly Button undo = Kit.Button("Undo last change", "chrome");
        private readonly Button openFolder = Kit.Button("Open album folder");
        private readonly Button issuesButton = Kit.Button("Details");
        private readonly AlbumList albums = new AlbumList();
        private readonly ICoverSearch coverSearch;
        private readonly CoverSearchPanel online;
        private readonly Image cover = new Image { Stretch = Stretch.Uniform };
        private readonly Border coverFrame;
        private readonly TextBlock selectedTitle = Kit.Label("Select an album", 17.33, true);
        private readonly TextBlock selectedArtist = Kit.Label("", Kit.Body, cls: "muted");
        private readonly TextBlock coverCaption = Kit.Label("Drop a cover image here", Kit.Tiny, cls: "muted");
        private readonly TextBlock detail = Kit.Label("", Kit.Tiny, cls: "muted");
        private readonly TextBlock selectedPath = Kit.Label("", 11.33, cls: "muted");
        private readonly TextBlock empty = Kit.Label("Scanning your music…", 16, cls: "muted");
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
        private bool showingMissing = true, busy, writing, refreshing, hasScanned;

        internal MainWindow(string root, bool autoScan = true, ICoverSearch onlineService = null)
        {
            coverSearch = onlineService ?? new CoverSearchService();
            online = new CoverSearchPanel(coverSearch) { UseCover = ApplyOnlineCoverAsync };
            Title = "Music Library Manager";
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Width = 1200; Height = 820; MinWidth = 1040; MinHeight = 760;
            folder.Text = root;
            folder.Named("Music folder"); search.Named("Search albums, artists or folders");
            coverFrame = new Border { CornerRadius = new CornerRadius(10), ClipToBounds = true, Margin = new Thickness(4, 0, 8, 2), Cursor = new Cursor(StandardCursorType.Hand), Child = cover };
            coverFrame.Brush(Border.BackgroundProperty, "Input");
            coverFrame.Named("Album cover preview and image drop target");
            Kit.Prepare(this, BuildLayout());
            appearance.Named("Toggle light and dark mode");
            Appearance.Changed += UpdateAppearance;
            UpdateAppearance();
            appearance.Click += (s, e) =>
            {
                ToolTip.SetIsOpen(appearance, false);
                if (!Appearance.SetDark(!Appearance.IsDark, animate: true)) appearance.Tip("Appearance changed, but the preference could not be saved on this PC.");
            };
            albums.WorkStatus = album => queuedAlbums.Contains(album.Key) ? (currentJob?.Album.Key == album.Key ? (currentJob.Artist == null ? "Applying cover…" : "Updating artists…") : "Queued") :
                showingArtists ? (album.ArtistChangeCount > 0 ? album.ArtistChangeCount + (album.ArtistChangeCount == 1 ? " track with artist changes" : " tracks with artist changes") : "No artist changes suggested") : null;
            queueButton.Click += (s, e) => ShowQueue();
            getUpdates.Click += async (s, e) => await GetUpdatesAsync();
            browse.Click += async (s, e) =>
            {
                var picked = await PickFolderAsync("Choose the top folder containing your music", folder.Text);
                if (picked != null) { folder.Text = picked; await ScanAsync(); }
            };
            scan.Tip("Rescan the library. Hold Shift to read every file again.");
            scan.Click += async (s, e) =>
            {
                bool shift = (lastModifiers & KeyModifiers.Shift) != 0;
                if (busy) cancellation?.Cancel(); else await ScanAsync(shift);
            };
            missingTab.Click += (s, e) => SwitchTab(true);
            existingTab.Click += (s, e) => SwitchTab(false);
            artistsTab.Click += (s, e) => SwitchArtists();
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
            issuesButton.Click += (s, e) => Dialogs.Issues(this, issues);
            DragDrop.SetAllowDrop(coverFrame, true);
            coverFrame.AddHandler(DragDrop.DragOverEvent, (s, e) => e.DragEffects = !busy && albums.SelectedAlbum != null && AlbumList.SingleImagePath(e.Data) != null ? DragDropEffects.Copy : DragDropEffects.None);
            coverFrame.AddHandler(DragDrop.DropEvent, (s, e) => { string path = AlbumList.SingleImagePath(e.Data); if (!busy && path != null) StageImage(path); });
            coverFrame.PointerPressed += (s, e) => { if (choose.IsEnabled) choose.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); };
            Closing += (s, e) =>
            {
                if (installingUpdate && !restartForUpdate) { e.Cancel = true; return; }
                if (maintenanceRunning || managerWindow != null && managerWindow.IsWorking) { e.Cancel = true; status.Text = "A library task is running. Stop it in Manage library before closing."; return; }
                if (writing || queueRunning) { e.Cancel = true; status.Text = "Work is still running. Open Queue to clear waiting jobs; the current update will finish safely."; }
                else cancellation?.Cancel();
            };
            Closed += (s, e) => Shutdown();
            AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
            AddHandler(PointerPressedEvent, (s, e) => lastModifiers = e.KeyModifiers, RoutingStrategies.Tunnel);
            AddHandler(KeyUpEvent, (s, e) => lastModifiers = e.KeyModifiers, RoutingStrategies.Tunnel);
            SetBusy(false);
            SwitchTab(true);
            if (autoScan) Opened += async (s, e) => await ScanAsync();
        }

        private KeyModifiers lastModifiers;
        internal AlbumList Albums => albums;
        internal CoverSearchPanel Online => online;
        internal bool HasStagedCover => pending != null;
        internal void DiscardStaged() => ShowSelection();

        private void OnKey(object sender, KeyEventArgs e)
        {
            lastModifiers = e.KeyModifiers;
            bool control = (e.KeyModifiers & KeyModifiers.Control) != 0;
            if (control && e.Key == Key.F) { search.Focus(); e.Handled = true; }
            else if (e.Key == Key.F5 && !busy) { _ = ScanAsync((e.KeyModifiers & KeyModifiers.Shift) != 0); e.Handled = true; }
            else if (control && e.Key == Key.Z && undo.IsEnabled && !(e.Source is TextBox)) { _ = UndoAsync(); e.Handled = true; }
            else if (e.Key == Key.Escape && pending != null && !busy) { ShowSelection(); e.Handled = true; }
        }

        private Control BuildLayout()
        {
            var header = Kit.Grid("*,108,150,178,122", "*").Add(BrandTitle().Cell(0, 0), appearance.Cell(0, 1), undo.Cell(0, 2), queueButton.Cell(0, 3), getUpdates.Cell(0, 4));
            var pathRow = Kit.Grid("*,124,78,132,145", "*").Add(folder.Cell(0, 0), browse.Cell(0, 1), scan.Cell(0, 2), findDuplicates.Cell(0, 3), manageLibrary.Cell(0, 4));
            foreach (var tab in new[] { missingTab, existingTab, artistsTab }) Avalonia.Automation.AutomationProperties.SetControlTypeOverride(tab, Avalonia.Automation.Peers.AutomationControlType.TabItem);
            var searchRow = Kit.Grid("55,*", "*").Add(Kit.Label("Search", Kit.Small).Cell(0, 0), search.Cell(0, 1));
            var tabs = Kit.Grid("238,263,165,*,226", "*").Add(missingTab.Cell(0, 0), existingTab.Cell(0, 1), artistsTab.Cell(0, 2), searchRow.Cell(0, 4));
            tabs.Margin = new Thickness(0, 6);
            empty.TextAlignment = TextAlignment.Center; empty.TextWrapping = TextWrapping.Wrap; empty.HorizontalAlignment = HorizontalAlignment.Center; empty.Margin = new Thickness(35);
            var listHost = new Panel(); listHost.Children.Add(albums); listHost.Children.Add(empty);
            var listLayout = Kit.Grid("*", "57,*").Add(BuildLibraryToolbar().Cell(0, 0), listHost.Cell(1, 0));
            var listPanel = Kit.Frost(listLayout, new Thickness(8, 10, 8, 10)); listPanel.Margin = new Thickness(0, 0, 18, 0);
            selectedArtist.Margin = new Thickness(0, 0, 0, 2);
            coverCaption.HorizontalAlignment = HorizontalAlignment.Center; detail.HorizontalAlignment = HorizontalAlignment.Center;
            var preview = Kit.Grid("*", "*,19,19").Add(coverFrame.Cell(0, 0), coverCaption.Cell(1, 0), detail.Cell(2, 0));
            foreach (var check in new[] { onlyMissing, folderCover }) check.Margin = new Thickness(5, 0, 0, 0);
            var editing = Kit.Grid("*", "32,28,28,36").Add(choose.Cell(0, 0), onlyMissing.Cell(1, 0), folderCover.Cell(2, 0), apply.Cell(3, 0));
            var manual = Kit.Grid("145,*", "*").Add(preview.Cell(0, 0), editing.Cell(0, 1));
            manual.Margin = new Thickness(0, 8, 0, 0);
            var path = Kit.Grid("*,145", "*").Add(selectedPath.Cell(0, 0), openFolder.Cell(0, 1));
            var sidebar = Kit.Grid("*", "29,23,*,141,30").Add(selectedTitle.Cell(0, 0), selectedArtist.Cell(1, 0), online.Cell(2, 0), manual.Cell(3, 0), path.Cell(4, 0));
            coverPanel = sidebar; artistPanel = BuildArtistPanel(); artistPanel.IsVisible = false;
            var detailHost = new Panel(); detailHost.Children.Add(sidebar); detailHost.Children.Add(artistPanel);
            var details = Kit.Frost(detailHost, new Thickness(9, 9, 9, 11));
            var body = Kit.Grid("*,580", "*").Add(listPanel.Cell(0, 0), details.Cell(0, 1));
            status.Named("Operation status and track counter");
            progress.Named("Scan and update progress"); progress.IsVisible = false;
            progressPercent.HorizontalAlignment = HorizontalAlignment.Right; progressPercent.Named("Progress percentage");
            var progressArea = Kit.Grid("*,47", "*").Add(progress.Cell(0, 0), progressPercent.Cell(0, 1));
            var bottom = Kit.Grid("*,190,86", "*").Add(status.Cell(0, 0), progressArea.Cell(0, 1), issuesButton.Cell(0, 2));
            bottom.Margin = new Thickness(0, 8, 0, 0);
            var outer = Kit.Grid("*", "52,44,52,*,42").Add(header.Cell(0, 0), pathRow.Cell(1, 0), tabs.Cell(2, 0), body.Cell(3, 0), bottom.Cell(4, 0));
            outer.Margin = new Thickness(24, 18, 24, 12);
            onlyMissing.Tip("Keeps existing embedded front covers. Uncheck to replace the cover on every track in this album.");
            folderCover.Tip("Updates recognized cover/folder/front/album/albumart images, or creates cover.jpg. Disabled when multiple albums share a folder.");
            getUpdates.Tip("Check GitHub releases, download a newer version, and restart when your scan and artwork queue finish.");
            undo.Tip("Restore files from your most recent edit in this session (Ctrl+Z). Backups are stored in the PC backup folder shown in Manage library.");
            return outer;
        }

        private static Control BrandTitle()
        {
            var mark = new Image { Source = Kit.Mark, Width = 34, Height = 34, Margin = new Thickness(0, 0, 12, 0) };
            var text = Kit.Label("Music Library Manager", 25.33, true);
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(mark); row.Children.Add(text);
            return row.Named("Music Library Manager");
        }

        private void UpdateAppearance()
        {
            appearance.Content = Appearance.IsDark ? "Light mode" : "Dark mode";
            Avalonia.Automation.AutomationProperties.SetHelpText(appearance, (Appearance.IsDark ? "Dark" : "Light") + " mode is active. Switch appearance without interrupting your work.");
            appearance.Tip("Switch to " + (Appearance.IsDark ? "light" : "dark") + " mode. Your choice is remembered on this PC.");
        }

        private void SwitchTab(bool missing)
        {
            if (busy) return;
            showingMissing = missing; showingArtists = false;
            SetViewFilter(0);
            onlyMissing.IsChecked = missing;
            StyleTabs();
            RefreshAlbums();
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
            var visible = library.Albums.Where(a => showingArtists || (showingMissing ? a.MissingCount > 0 : a.HasAnyCover))
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
            missingTab.Content = "Add missing album art (" + missingCount + ")";
            existingTab.Content = "Change existing album art (" + existingCount + ")";
            artistsTab.Content = "Track artists (" + artistCount + ")";
            empty.IsVisible = visible.Count == 0;
            albums.IsVisible = !empty.IsVisible;
            empty.Text = query.Length > 0 ? "No albums match your search." : !hasScanned ? "Scan a folder to find your albums." :
                library.Albums.Count == 0 ? "No supported music files found.\nChoose a folder containing your albums." : showingMissing ?
                "Every scanned track has cover art.\nUse the other tab to change a cover." : "No existing covers found yet.\nAdd your first cover in the other tab.";
            if (visible.Count == 0 && library.Albums.Count > 0 && (showingArtists || albumFilter.SelectedIndex == 1))
                empty.Text = "No albums match this filter.\nChoose All albums in this tab to review other artists.";
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
                online.SetAlbum(null, false); ShowArtistSelection(); UpdateLibraryTools(); return;
            }
            selectedTitle.Text = album?.Title ?? "Select an album";
            selectedArtist.Text = album?.Artist ?? "";
            selectedPath.Text = album?.Folder ?? "";
            selectedTitle.Tip(album?.Title); selectedPath.Tip(album?.Folder);
            detail.Text = album == null ? "" : album.Tracks.Count + " tracks · " + album.MissingCount + " missing covers";
            coverCaption.Text = album?.Thumbnail != null ? (album.PreviewTrack != null ? "Current embedded cover" : "Current folder cover") : "Drop a cover image here";
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
            coverCaption.Text = "New cover · ready to apply";
            var size = Artwork.ReadInfo(image);
            detail.Text = size.Width + " × " + size.Height + " · " + image.Length / 1024 + " KB";
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
            browse.IsEnabled = missingTab.IsEnabled = existingTab.IsEnabled = albums.IsEnabled = !value;
            search.IsReadOnly = value;
            scan.IsEnabled = !writing;
            scan.Content = value && !edit ? "Stop scan" : "Rescan";
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
                empty.Text = "Scanning your music…\nAlbums appear when the scan finishes.";
                albums.IsVisible = false; empty.IsVisible = true;
            }
            issuesButton.IsEnabled = issues.Count > 0;
            UpdateApplyButton(); UpdateLibraryTools();
        }

        internal IProgress<ProgressInfo> Reporter()
        {
            int operation = progressVersion;
            return new Progress<ProgressInfo>(p =>
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
            string root = folder.Text;
            cancellation?.Dispose(); cancellation = new CancellationTokenSource();
            var token = cancellation.Token;
            issues.Clear(); SetBusy(true); status.Text = "Scanning music folders…";
            var reporter = Reporter();
            try
            {
                var result = await Task.Run(() => scanner.Scan(root, token, reporter, useCache: !force));
                library = result; hasScanned = true; issues.AddRange(result.Issues);
                if (!result.Cancelled) AppSettings.LastFolder = root;
                status.Text = (result.Cancelled ? "Scan stopped · " : "") + result.Albums.Count + " albums · " + result.FilesRead + " tracks" +
                    (issues.Count > 0 ? " · " + issues.Count + " issues (Details)" : " · Drop an image onto an album to get started.");
                RefreshAlbums();
            }
            catch (Exception e) { issues.Add(e.Message); status.Text = "Scan could not finish. See Details."; RefreshAlbums(); }
            finally { SetBusy(false); ShowSelection(); }
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
            catch (Exception e) { issues.Add(e.Message); issuesButton.IsEnabled = true; status.Text = "Could not queue this cover. See Details."; return Task.CompletedTask; }
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
                status.Text = result.Restored + " original files restored" + (issues.Count > 0 ? " · Some files could not be restored. See Details." : ".");
            }
            catch (Exception e) { issues.Add(e.Message); status.Text = "Undo could not finish. See Details."; }
            finally { SetBusy(false); ShowSelection(); }
        }

        private async Task RefreshFolderAsync(string path, string key, bool preserveStaged = false)
        {
            var updated = await Task.Run(() => scanner.Scan(path, CancellationToken.None, recursive: false, useCache: false));
            library.Albums.RemoveAll(a => string.Equals(a.Folder, path, StringComparison.OrdinalIgnoreCase));
            library.Albums.AddRange(updated.Albums); issues.AddRange(updated.Issues);
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
            Appearance.Changed -= UpdateAppearance;
            var source = cancellation; cancellation = null;
            source?.Cancel(); source?.Dispose();
            var image = cover.Source as Bitmap; cover.Source = null; image?.Dispose();
            online.Shutdown(); coverSearch.Dispose();
            foreach (var window in OwnedWindows.ToArray()) window.Close();
        }
    }
}
