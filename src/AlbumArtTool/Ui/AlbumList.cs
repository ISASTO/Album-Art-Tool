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
        internal void Update(string work)
        {
            string badge = work ?? Album.Coverage, tone = work != null ? "Accent" : Album.MissingCount > 0 ? "Warning" : "Positive";
            if (badge == Badge && tone == Tone) return;
            Badge = badge; Tone = tone; Changed?.Invoke();
        }
    }

    // Virtualized album list. Only visible rows exist as controls; thumbnails are decoded
    // when a row first appears and kept in a bounded cache.
    internal sealed class AlbumList : ListBox
    {
        protected override Type StyleKeyOverride => typeof(ListBox);
        private List<AlbumRow> rows = new List<AlbumRow>();
        internal event Action<Album, string> CoverDropped;
        internal Func<Album, string> WorkStatus { get; set; }
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

    internal sealed class AlbumRowView : Border
    {
        private readonly Border tile, placeholder, badge;
        private readonly Image cover;
        private readonly TextBlock title, detail, badgeText, note;
        private AlbumRow row;
        private IDisposable badgeBrush, toneBrush;

        internal AlbumRowView()
        {
            Height = 86; Padding = new Thickness(3); Background = Brushes.Transparent;
            cover = new Image { Stretch = Stretch.Uniform };
            note = new TextBlock { Text = "♪", FontWeight = FontWeight.Bold, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            note.Brush(TextBlock.ForegroundProperty, "Accent");
            var coverBox = new Panel(); coverBox.Children.Add(note); coverBox.Children.Add(cover);
            placeholder = new Border { Width = 64, Height = 64, CornerRadius = new CornerRadius(9), ClipToBounds = true, Child = coverBox, Margin = new Thickness(9, 8, 13, 8),
                BoxShadow = BoxShadows.Parse("0 2 0 0 #14273958"), VerticalAlignment = VerticalAlignment.Center };
            placeholder.Brush(Border.BackgroundProperty, "CoverPlaceholder");
            title = Kit.Label("", 14, true); title.Margin = new Thickness(0, 6, 12, 0);
            detail = Kit.Label("", Kit.Small, cls: "muted"); detail.Margin = new Thickness(0, 2, 12, 0);
            badgeText = new TextBlock { FontSize = Kit.Small, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            badge = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 1, 7, 2), Margin = new Thickness(-1, 5, 12, 0), HorizontalAlignment = HorizontalAlignment.Left, Child = badgeText };
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
            text.Children.Add(title); text.Children.Add(detail); text.Children.Add(badge);
            var grid = Kit.Grid("Auto,*", null).Add(placeholder.Cell(0, 0), text.Cell(0, 1));
            tile = new Border { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Child = grid, Classes = { "tile" } };
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
            badgeText.Text = row.Badge;
            badgeBrush?.Dispose(); toneBrush?.Dispose();
            badgeBrush = badge.Bind(Border.BackgroundProperty, badge.GetResourceObservable("Badge" + row.Tone));
            toneBrush = badgeText.Bind(TextBlock.ForegroundProperty, badgeText.GetResourceObservable(row.Tone));
        }
    }
}
