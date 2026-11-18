using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using AlbumArtTool.Core;

namespace AlbumArtTool.Ui
{
    internal sealed class AlbumRow
    {
        internal readonly Album Album;
        internal string Badge { get; private set; }
        internal string Tone { get; private set; }
        internal event Action Changed;
        internal AlbumRow(Album album) { Album = album; }

        /// <summary>The row's status tag: work in progress when there is some, otherwise its cover coverage.</summary>
        internal void Update((string Text, string Tone)? work)
        {
            string badge = work?.Text ?? Short(Album), tone = work?.Tone ?? (Album.MissingCount > 0 ? "Warning" : "Positive");
            if (badge == Badge && tone == Tone) return;
            Badge = badge; Tone = tone; Changed?.Invoke();
        }

        private static string Short(Album album) => album.MissingCount == 0 ? "Complete" :
            album.MissingCount == album.Tracks.Count ? (album.FolderCovers.Count > 0 ? "Folder art" : "No cover") :
            album.MissingCount + "/" + album.Tracks.Count + " missing";
    }

    // Virtualized album list. Only visible rows exist as controls; thumbnails are decoded
    // when a row first appears and kept in a bounded cache.
    internal sealed class AlbumList : ListBox
    {
        protected override Type StyleKeyOverride => typeof(ListBox);
        private List<AlbumRow> rows = new List<AlbumRow>();
        private (long At, bool Keyboard) input;
        internal event Action<Album, string> CoverDropped;
        internal Func<Album, (string Text, string Tone)?> WorkStatus { get; set; }
        internal Album SelectedAlbum => (SelectedItem as AlbumRow)?.Album;
        internal int AlbumCount => rows.Count;
        internal Album AlbumAt(int index) => rows[index].Album;

        internal AlbumList()
        {
            Classes.Add("albums");
            SelectionMode = SelectionMode.Single;
            ItemTemplate = new FuncDataTemplate<AlbumRow>((row, scope) => new AlbumRowView(), true);
            ScrollViewer.SetHorizontalScrollBarVisibility(this, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
            DragDrop.SetAllowDrop(this, true);
            // A row chosen with the mouse or keyboard latches: it sinks, then springs back up and stays pressed in.
            AddHandler(PointerPressedEvent, (s, e) => input = (Environment.TickCount64, false), Avalonia.Interactivity.RoutingStrategies.Tunnel);
            AddHandler(KeyDownEvent, (s, e) => input = (Environment.TickCount64, true), Avalonia.Interactivity.RoutingStrategies.Tunnel);
            SelectionChanged += (s, e) =>
            {
                if (SelectedIndex < 0 || Environment.TickCount64 - input.At > 400) return;
                Motion.Latch(ContainerFromIndex(SelectedIndex), input.Keyboard);
            };
            AddHandler(DragDrop.DragOverEvent, (s, e) => e.DragEffects = RowAt(e) != null && SingleImagePath(e.Data) != null ? DragDropEffects.Copy : DragDropEffects.None);
            AddHandler(DragDrop.DropEvent, (s, e) =>
            {
                var row = RowAt(e); string path = SingleImagePath(e.Data);
                if (row == null || path == null) return;
                SelectedItem = row;
                CoverDropped?.Invoke(row.Album, path);
            });
        }

        private AlbumRow RowAt(DragEventArgs e)
        {
            var hit = this.InputHitTest(e.GetPosition(this)) as Visual;
            for (var visual = hit; visual != null; visual = visual.GetVisualParent())
                if (visual is ListBoxItem item) return item.DataContext as AlbumRow;
            return null;
        }

        internal void SetAlbums(IEnumerable<Album> values, string selectedKey = null)
        {
            rows = values.Select(a => new AlbumRow(a)).ToList();
            foreach (var row in rows) row.Update(WorkStatus?.Invoke(row.Album));
            ItemsSource = rows;
            if (rows.Count > 0)
            {
                int index = Math.Max(0, rows.FindIndex(r => r.Album.Key == selectedKey));
                SelectedIndex = index;
                ScrollIntoView(index);
            }
            else SelectedIndex = -1;
        }

        /// <summary>Recomputes queue/cleanup badges without rebuilding the list.</summary>
        internal void RefreshBadges() { foreach (var row in rows) row.Update(WorkStatus?.Invoke(row.Album)); }

        internal void Select(int index) { SelectedIndex = index; if (index >= 0) ScrollIntoView(index); }

        internal static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".bmp", ".gif" };
        internal static string SingleImagePath(IDataObject data)
        {
            var files = data?.GetFiles()?.ToArray();
            if (files == null || files.Length != 1) return null;
            return SingleImagePath(files[0].TryGetLocalPath());
        }
        internal static string SingleImagePath(string path)
        {
            if (path == null || !System.IO.File.Exists(path)) return null;
            string extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
            return ImageExtensions.Contains(extension) ? path : null;
        }
    }

    internal static class ThumbnailCache
    {
        private const int Capacity = 400;
        private static readonly Dictionary<byte[], LinkedListNode<KeyValuePair<byte[], Bitmap>>> entries = new Dictionary<byte[], LinkedListNode<KeyValuePair<byte[], Bitmap>>>(ReferenceComparer.Instance);
        private static readonly LinkedList<KeyValuePair<byte[], Bitmap>> order = new LinkedList<KeyValuePair<byte[], Bitmap>>();

        // Evicted bitmaps are left to the garbage collector: a row that is still on screen may
        // reference one, so it must not be disposed underneath the renderer.
        internal static Bitmap Get(byte[] bytes)
        {
            if (bytes == null) return null;
            if (entries.TryGetValue(bytes, out var node)) { order.Remove(node); order.AddFirst(node); return node.Value.Value; }
            Bitmap bitmap;
            try { bitmap = Kit.Decode(bytes); }
            catch (Exception) { return null; }
            node = order.AddFirst(new KeyValuePair<byte[], Bitmap>(bytes, bitmap));
            entries[bytes] = node;
            if (order.Count > Capacity) { entries.Remove(order.Last.Value.Key); order.RemoveLast(); }
            return bitmap;
        }

        private sealed class ReferenceComparer : IEqualityComparer<byte[]>
        {
            internal static readonly ReferenceComparer Instance = new ReferenceComparer();
            public bool Equals(byte[] x, byte[] y) => ReferenceEquals(x, y);
            public int GetHashCode(byte[] value) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value);
        }
    }

    // One album: its cover, title and artist, and a status tag. Rows are separated by seams; the selected row is
    // pressed into the panel with a glowing orange edge.
    internal sealed class AlbumRowView : Border
    {
        private readonly Border tile, frame;
        private readonly Image cover;
        private readonly PathIcon note;
        private readonly TextBlock title, detail;
        private readonly Pill tag = new Pill();
        private AlbumRow row;

        internal AlbumRowView()
        {
            Height = 64; Padding = new Thickness(0, 0, 0, 1); Background = Brushes.Transparent; BorderThickness = new Thickness(0, 0, 0, 1);
            this.Brush(BorderBrushProperty, "Seam");
            cover = new Image { Stretch = Stretch.UniformToFill };
            note = Kit.Glyph(Icons.Album, 16); note.HorizontalAlignment = HorizontalAlignment.Center; note.Brush(PathIcon.ForegroundProperty, "Muted");
            var coverBox = new Panel(); coverBox.Children.Add(note); coverBox.Children.Add(cover);
            frame = new Border { Width = 44, Height = 44, CornerRadius = new CornerRadius(3), ClipToBounds = true, Child = coverBox, Margin = new Thickness(12, 0, 14, 0),
                VerticalAlignment = VerticalAlignment.Center, BorderThickness = new Thickness(1) };
            frame.Brush(Border.BackgroundProperty, "CoverPlaceholder"); frame.Brush(Border.BorderBrushProperty, "Seam");
            title = Kit.Label("", 13.5, true); title.FontWeight = FontWeight.SemiBold; title.Margin = new Thickness(0, 0, 12, 0);
            detail = Kit.Label("", Kit.Small, cls: "muted"); detail.Margin = new Thickness(0, 3, 12, 0);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(title); text.Children.Add(detail);
            tag.Margin = new Thickness(0, 0, 16, 0); tag.MaxWidth = 190; tag.Label.FontSize = 9.5;
            var edge = new Border { Classes = { "edge" }, Width = 3, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 12, 0, 12), HorizontalAlignment = HorizontalAlignment.Left,
                Effect = Skin.Glow(Skin.Orange, 8, 0.85), IsHitTestVisible = false };
            edge.Brush(Border.BackgroundProperty, "Primary");
            var grid = Kit.Grid("Auto,*,Auto", null).Add(frame.Cell(0, 0), text.Cell(0, 1), tag.Cell(0, 2));
            var layers = new Panel(); layers.Children.Add(grid); layers.Children.Add(edge);
            tile = new Border { CornerRadius = new CornerRadius(6), Child = layers, Classes = { "tile" }, Margin = new Thickness(2, 1) };
            Child = tile;
            DataContextChanged += (s, e) => Bind(DataContext as AlbumRow);
        }

        private void Bind(AlbumRow next)
        {
            if (row != null) row.Changed -= UpdateBadge;
            row = next;
            if (row == null) { cover.Source = null; return; }
            row.Changed += UpdateBadge;
            var album = row.Album;
            title.Text = album.Title;
            detail.Text = album.Artist + "  ·  " + album.Tracks.Count + (album.Tracks.Count == 1 ? " track" : " tracks");
            ToolTip.SetTip(this, album.Folder);
            var image = ThumbnailCache.Get(album.Thumbnail);
            cover.Source = image; note.IsVisible = image == null;
            UpdateBadge();
        }

        private void UpdateBadge()
        {
            if (row == null) return;
            tag.Set(row.Badge, row.Tone);
            tag.Tip(row.Tone == "Accent" ? row.Badge : row.Album.Coverage);
        }
    }
}
