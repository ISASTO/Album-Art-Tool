using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using AlbumArtTool.Core;

namespace AlbumArtTool
{
    internal sealed class AlbumList : ListView
    {
        private List<Album> albums = new List<Album>();
        private readonly Dictionary<string, Image> images = new Dictionary<string, Image>();
        private readonly Queue<string> imageOrder = new Queue<string>();
        private readonly Font titleFont = new Font("Segoe UI", 10.5f, FontStyle.Bold);
        private readonly Font detailFont = new Font("Segoe UI", 9f);
        private readonly ImageList rowHeight;
        internal event Action<Album, string> CoverDropped;
        internal Album SelectedAlbum => SelectedIndices.Count == 0 ? null : albums[SelectedIndices[0]];
        internal int AlbumCount => albums.Count;

        internal AlbumList()
        {
            DoubleBuffered = true;
            View = View.Details;
            HeaderStyle = ColumnHeaderStyle.None;
            FullRowSelect = true;
            MultiSelect = false;
            HideSelection = false;
            BorderStyle = BorderStyle.None;
            BackColor = MainForm.Surface;
            ForeColor = MainForm.Ink;
            OwnerDraw = true;
            VirtualMode = true;
            ShowItemToolTips = true;
            AccessibleName = "Albums";
            Columns.Add("Album", 600);
            rowHeight = new ImageList { ImageSize = new Size(1, 86), ColorDepth = ColorDepth.Depth32Bit };
            rowHeight.Images.Add(new Bitmap(1, 86));
            SmallImageList = rowHeight;
            HandleCreated += (s, e) => UpdateRowHeight();
            DpiChangedAfterParent += (s, e) => UpdateRowHeight();
            RetrieveVirtualItem += (s, e) =>
            {
                var album = albums[e.ItemIndex];
                e.Item = new ListViewItem(album.Title + " · " + album.Artist + " · " + album.Coverage, 0) { ToolTipText = album.Folder, Tag = album };
            };
            Resize += (s, e) => { if (Columns.Count > 0) Columns[0].Width = Math.Max(80, ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 2); };
            DrawColumnHeader += (s, e) => e.DrawDefault = true;
            DrawSubItem += DrawAlbum;
            AllowDrop = true;
            DragEnter += (s, e) => e.Effect = MainForm.SingleImagePath(e.Data) != null ? DragDropEffects.Copy : DragDropEffects.None;
            DragOver += (s, e) => e.Effect = GetItemAt(PointToClient(new Point(e.X, e.Y)).X, PointToClient(new Point(e.X, e.Y)).Y) != null
                && MainForm.SingleImagePath(e.Data) != null ? DragDropEffects.Copy : DragDropEffects.None;
            DragDrop += (s, e) =>
            {
                var point = PointToClient(new Point(e.X, e.Y));
                var item = GetItemAt(point.X, point.Y);
                string path = MainForm.SingleImagePath(e.Data);
                if (item == null || path == null) return;
                SelectedIndices.Clear();
                SelectedIndices.Add(item.Index);
                CoverDropped?.Invoke(albums[item.Index], path);
            };
        }

        private void UpdateRowHeight()
        {
            rowHeight.ImageSize = new Size(1, Math.Min(256, (int)Math.Round(86.0 * DeviceDpi / 96.0)));
        }

        internal void SetAlbums(IEnumerable<Album> values, string selectedKey = null)
        {
            BeginUpdate();
            SelectedIndices.Clear();
            VirtualListSize = 0;
            foreach (var image in images.Values) image.Dispose();
            images.Clear(); imageOrder.Clear();
            albums = values.ToList();
            VirtualListSize = albums.Count;
            if (albums.Count > 0)
            {
                int index = Math.Max(0, albums.FindIndex(a => a.Key == selectedKey));
                SelectedIndices.Add(index);
            }
            EndUpdate(); Invalidate();
        }

        private Image GetThumbnail(Album album)
        {
            if (album.Thumbnail == null) return null;
            if (images.TryGetValue(album.Key, out Image image)) return image;
            try { image = Artwork.Decode(album.Thumbnail); }
            catch (Exception e) when (Artwork.IsImageError(e)) { return null; }
            if (images.Count >= 160)
            {
                string oldest = imageOrder.Dequeue();
                images[oldest].Dispose(); images.Remove(oldest);
            }
            images[album.Key] = image; imageOrder.Enqueue(album.Key);
            return image;
        }

        private void DrawAlbum(object sender, DrawListViewSubItemEventArgs e)
        {
            if (e.ItemIndex >= albums.Count || e.ColumnIndex != 0) return;
            var album = albums[e.ItemIndex];
            var bounds = e.Bounds;
            int S(int value) => (int)Math.Round(value * bounds.Height / 86.0);
            bool selected = SelectedIndices.Contains(e.ItemIndex);
            using (var fill = new SolidBrush(selected ? Color.FromArgb(32, 55, 52) : MainForm.Surface)) e.Graphics.FillRectangle(fill, bounds);
            int coverSize = bounds.Height - S(18);
            var cover = new Rectangle(bounds.X + S(9), bounds.Y + S(9), coverSize, coverSize);
            using (var fill = new SolidBrush(Color.FromArgb(40, 41, 44))) e.Graphics.FillRectangle(fill, cover);
            var image = GetThumbnail(album);
            if (image != null)
            {
                float scale = Math.Min((float)cover.Width / image.Width, (float)cover.Height / image.Height);
                var size = new Size((int)(image.Width * scale), (int)(image.Height * scale));
                e.Graphics.DrawImage(image, cover.X + (cover.Width - size.Width) / 2, cover.Y + (cover.Height - size.Height) / 2, size.Width, size.Height);
            }
            else
                TextRenderer.DrawText(e.Graphics, "♪", titleFont, cover, MainForm.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            int left = cover.Right + S(13), width = Math.Max(1, bounds.Right - left - S(12));
            var flags = TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(e.Graphics, album.Title, titleFont, new Rectangle(left, bounds.Y + S(10), width, S(23)), MainForm.Ink, flags);
            TextRenderer.DrawText(e.Graphics, album.Artist + "  ·  " + album.Tracks.Count + " tracks", detailFont,
                new Rectangle(left, bounds.Y + S(35), width, S(20)), MainForm.Muted, flags);
            TextRenderer.DrawText(e.Graphics, album.Coverage, detailFont, new Rectangle(left, bounds.Y + S(57), width, S(20)),
                album.MissingCount > 0 ? Color.FromArgb(236, 190, 112) : MainForm.Accent, flags);
            using (var line = new Pen(Color.FromArgb(43, 44, 47))) e.Graphics.DrawLine(line, bounds.X + 9, bounds.Bottom - 1, bounds.Right - 9, bounds.Bottom - 1);
            if (selected && Focused) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(bounds, -2, -2), MainForm.Accent, MainForm.Surface);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (var image in images.Values) image.Dispose();
                titleFont.Dispose(); detailFont.Dispose(); rowHeight.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
