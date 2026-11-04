using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using AlbumArtTool.Core;
using AlbumArtTool.Online;
using AlbumArtTool.Ui;

namespace AlbumArtTool
{
    // Change the covers of downloads in New music before they join the library: the same online suggestions,
    // image picker and drag and drop as the Albums page. Every song of the album gets the new cover; Undo puts the old one back.
    internal sealed class NewMusicCoversWindow : Window
    {
        private readonly string folder;
        private readonly ICoverSearch search;
        private readonly Scanner scanner = new Scanner();
        private readonly CoverEditor editor = new CoverEditor();
        private readonly AlbumList albums = new AlbumList();
        private readonly CoverSearchPanel online;
        private readonly Image cover = new Image { Stretch = Stretch.Uniform };
        private readonly TextBlock title = Kit.Label("Select an album", 17.33, true), artist = Kit.Label("", Kit.Body, cls: "muted"),
            caption = Kit.Label("", Kit.Tiny, cls: "muted"), status = Kit.Label("", Kit.Small, cls: "muted");
        private readonly Button choose = Kit.Button("Choose image…"), apply = Kit.Button("Apply cover", "primary"), undo = Kit.IconButton(Icons.Undo, "Undo", "ghost");
        private byte[] pending;
        private EditResult last;
        private bool working;
        internal event Action Changed;
        internal AlbumList Albums => albums;
        internal CoverSearchPanel Online => online;
        internal Task Work { get; private set; } = Task.CompletedTask;

        internal NewMusicCoversWindow(string folder, ICoverSearch search)
        {
            this.folder = folder; this.search = search;
            Title = "Covers in New music"; Width = 1100; Height = 720; MinWidth = 900; MinHeight = 560;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            online = new CoverSearchPanel(search) { UseCover = UseOnlineAsync };
            albums.Named("Albums in New music");
            albums.SelectionChanged += (s, e) => ShowSelection();
            albums.CoverDropped += (album, path) => Stage(path);
            var frame = new Border { CornerRadius = new CornerRadius(10), ClipToBounds = true, Child = cover, Width = 150, Height = 150, Cursor = new Cursor(StandardCursorType.Hand) };
            frame.Brush(Border.BackgroundProperty, "Input");
            frame.Named("New music cover preview and image drop target");
            DragDrop.SetAllowDrop(frame, true);
            frame.AddHandler(DragDrop.DragOverEvent, (s, e) => e.DragEffects = albums.SelectedAlbum != null && AlbumList.SingleImagePath(e.Data) != null ? DragDropEffects.Copy : DragDropEffects.None);
            frame.AddHandler(DragDrop.DropEvent, (s, e) => { string path = AlbumList.SingleImagePath(e.Data); if (path != null) Stage(path); });
            frame.PointerPressed += async (s, e) => await ChooseAsync();
            choose.Tip("Pick an image file (JPG, PNG, BMP or GIF) for the selected album. You can also drop one on the cover or on the album.");
            choose.Click += async (s, e) => await ChooseAsync();
            apply.Named("Apply cover in New music").Tip("Embed the chosen cover in every song of this album in New music, as one front cover (JPEG, at most " + Artwork.EmbedSide + " px).");
            apply.Click += async (s, e) => await ApplyAsync();
            undo.Named("Undo cover change").Tip("Put back the covers from before the last change in this window.");
            undo.Click += async (s, e) => await UndoAsync();
            status.Named("New music covers status"); status.TextWrapping = TextWrapping.Wrap; status.TextTrimming = TextTrimming.None;
            caption.HorizontalAlignment = HorizontalAlignment.Center;
            var preview = new StackPanel { Spacing = 4 }; preview.Children.Add(frame); preview.Children.Add(caption);
            foreach (var b in new[] { choose, apply }) { b.Height = 34; b.HorizontalAlignment = HorizontalAlignment.Stretch; b.Margin = new Thickness(0, 0, 0, 6); }
            var actions = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            actions.Children.Add(choose); actions.Children.Add(apply); actions.Children.Add(undo); actions.Children.Add(status);
            var manual = Kit.Grid("Auto,*", "Auto").Add(preview.Cell(0, 0), actions.Cell(0, 1)); manual.Margin = new Thickness(0, 10, 0, 0);
            var details = Kit.Frost(Kit.Grid("*", "Auto,Auto,*,Auto").Add(title.Cell(0, 0), artist.Cell(1, 0), online.Cell(2, 0), manual.Cell(3, 0)), new Thickness(12));
            var list = Kit.Frost(albums, new Thickness(8)); list.Margin = new Thickness(0, 0, 14, 0);
            var heading = Kit.Label("Covers in New music", 20, true);
            var sub = Kit.Label("Pick an album, then use a suggested cover, choose an image or drop one. Downloads stay in New music until you add them to your library.", Kit.Small, cls: "muted");
            sub.Margin = new Thickness(0, 2, 0, 12);
            var layout = Kit.Grid("*", "Auto,Auto,*").Add(heading.Cell(0, 0), sub.Cell(1, 0), Kit.Grid("*,520", "*").Add(list.Cell(0, 0), details.Cell(0, 1)).Cell(2, 0));
            layout.Margin = new Thickness(18, 14, 18, 14);
            Kit.Prepare(this, layout);
            Closed += (s, e) => { online.Shutdown(); var b = cover.Source as Bitmap; cover.Source = null; b?.Dispose(); };
            UpdateButtons();
        }

        /// <summary>Reads New music again, keeping the selected album.</summary>
        internal async Task LoadAsync()
        {
            string key = albums.SelectedAlbum?.Key;
            var result = await Task.Run(() => scanner.Scan(folder, CancellationToken.None, useCache: false));
            albums.SetAlbums(result.Albums.OrderBy(a => a.Artist, StringComparer.CurrentCultureIgnoreCase).ThenBy(a => a.Title, StringComparer.CurrentCultureIgnoreCase), key);
            if (albums.SelectedAlbum == null && albums.AlbumCount > 0) albums.Select(0);
            if (albums.AlbumCount == 0) status.Text = "There are no songs in New music.";
            ShowSelection();
        }

        private void ShowSelection()
        {
            pending = null;
            var album = albums.SelectedAlbum;
            title.Text = album?.Title ?? "Select an album"; artist.Text = album?.Artist ?? "";
            caption.Text = album?.Thumbnail != null ? "Current cover" : album != null ? "No cover yet" : "";
            SetImage(album?.Thumbnail);
            online.SetAlbum(album, !working);
            UpdateButtons();
        }

        private void SetImage(byte[] bytes)
        {
            var old = cover.Source as Bitmap; Bitmap next = null;
            try { next = Kit.Decode(bytes); } catch (Exception) { }
            cover.Source = next; if (old != null && !ReferenceEquals(old, next)) old.Dispose();
        }

        private void UpdateButtons()
        {
            var album = albums.SelectedAlbum;
            choose.IsEnabled = album != null && !working;
            apply.IsEnabled = album != null && pending != null && !working;
            apply.Content = album == null ? "Apply cover" : "Apply cover to " + album.Tracks.Count + (album.Tracks.Count == 1 ? " song" : " songs");
            undo.IsVisible = last != null && last.Entries.Count > 0; undo.IsEnabled = !working;
        }

        internal void Stage(string path)
        {
            if (albums.SelectedAlbum == null || working) return;
            try { SetPending(Artwork.Normalize(Artwork.LoadFile(path))); }
            catch (Exception e) when (Scanner.IsFileError(e) || Artwork.IsImageError(e)) { status.Text = "That image could not be opened. Use a JPG, PNG, BMP or GIF."; }
        }

        private void SetPending(byte[] image)
        {
            pending = image; SetImage(image);
            var size = Artwork.ReadInfo(image);
            caption.Text = "New cover · " + size.Width + " × " + size.Height;
            status.Text = "Review the cover, then click Apply.";
            UpdateButtons();
        }

        private async Task ChooseAsync()
        {
            if (albums.SelectedAlbum == null || working) return;
            var result = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Choose an album cover", AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType("Cover images") { Patterns = new[] { "*.jpg", "*.jpeg", "*.png", "*.bmp", "*.gif" } }, FilePickerFileTypes.All } });
            string path = result.Count > 0 ? result[0].TryGetLocalPath() : null;
            if (path != null) Stage(path);
        }

        // One click on a suggestion applies it straight away, as on the Albums page.
        private async Task UseOnlineAsync(string key, CoverCandidate candidate)
        {
            var album = albums.SelectedAlbum;
            if (album == null || album.Key != key || working) return;
            status.Text = "Downloading the cover from " + candidate.Source + "…";
            try { var bytes = await search.DownloadAsync(candidate, CancellationToken.None); pending = Artwork.Normalize(bytes); }
            catch (Exception e) { status.Text = "That cover could not be downloaded: " + e.Message; return; }
            await ApplyAsync();
        }

        internal Task ApplyAsync() => Work = ApplyCoreAsync();

        private async Task ApplyCoreAsync()
        {
            var album = albums.SelectedAlbum; byte[] image = pending;
            if (album == null || image == null || working) return;
            working = true; UpdateButtons(); online.SetActive(false);
            status.Text = "Saving the cover in " + album.Tracks.Count + " songs…";
            try
            {
                var result = await Task.Run(() => editor.Apply(album, image, false, false));
                last = result;
                status.Text = result.Issues.Count == 0 ? "Cover saved in " + result.TracksWritten + (result.TracksWritten == 1 ? " song." : " songs.") : "Saved with problems: " + string.Join(" · ", result.Issues.Take(3));
            }
            catch (Exception e) { status.Text = "The cover could not be saved: " + e.Message; }
            finally { working = false; await LoadAsync(); Changed?.Invoke(); }
        }

        internal async Task UndoAsync()
        {
            if (last == null || working) return;
            working = true; UpdateButtons();
            try { var result = await Task.Run(() => editor.Undo(last)); status.Text = result.Restored + " songs have their previous cover again."; last = null; }
            catch (Exception e) { status.Text = "Undo could not finish: " + e.Message; }
            finally { working = false; await LoadAsync(); Changed?.Invoke(); }
        }
    }
}
