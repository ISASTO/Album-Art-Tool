using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using AlbumArtTool.Core;
using AlbumArtTool.Design;

namespace AlbumArtTool
{
    internal sealed class AlbumList : ListView
    {
        private int hoverIndex = -1;
        private List<Album> albums = new List<Album>();
        private readonly Dictionary<string, Image> images = new Dictionary<string, Image>();
        private readonly Queue<string> imageOrder = new Queue<string>();
        private readonly Font titleFont = new Font("Segoe UI", 10.5f, FontStyle.Bold);
        private readonly Font detailFont = new Font("Segoe UI", 9f);
        private readonly ImageList rowHeight;
        internal event Action<Album, string> CoverDropped;
        internal Album SelectedAlbum => SelectedIndices.Count == 0 ? null : albums[SelectedIndices[0]];
        internal int AlbumCount => albums.Count;
        internal Album AlbumAt(int index) => albums[index];
        internal Func<Album, string> WorkStatus { get; set; }

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
            Resize += (s, e) => { if (Columns.Count > 0) Columns[0].Width = Math.Max(80, ClientSize.Width - 2); };
            DrawColumnHeader += (s, e) => e.DrawDefault = true;
            DrawSubItem += DrawAlbum;
            MouseMove += (s, e) => SetHover(GetItemAt(e.X, e.Y)?.Index ?? -1);
            MouseLeave += (s, e) => SetHover(-1);
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
            hoverIndex = -1;
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

        private void SetHover(int index)
        {
            if (hoverIndex == index) return;
            int previous = hoverIndex; hoverIndex = index;
            foreach (int value in new[] { previous, index })
                if (value >= 0 && value < VirtualListSize) Invalidate(GetItemRect(value));
        }
        private void DrawAlbum(object sender, DrawListViewSubItemEventArgs e)
        {
            if (e.ItemIndex >= albums.Count || e.ColumnIndex != 0) return;
            var album = albums[e.ItemIndex]; var bounds = e.Bounds;
            int S(int value) => (int)Math.Round(value * bounds.Height / 86.0);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var fill = new SolidBrush(AppTheme.Surface)) e.Graphics.FillRectangle(fill, bounds);
            bool selected = SelectedIndices.Contains(e.ItemIndex), hovered = hoverIndex == e.ItemIndex;
            var tile = new Rectangle(bounds.X + S(3), bounds.Y + S(3), Math.Max(1, bounds.Width - S(6)), bounds.Height - S(6));
            using (var path = AppTheme.Rounded(tile, S(12)))
            using (var fill = new SolidBrush(selected ? AppTheme.Selection : hovered ? AppTheme.Input : AppTheme.Surface)) e.Graphics.FillPath(fill, path);
            if (selected)
                using (var path = AppTheme.Rounded(tile, S(12)))
                using (var border = new Pen(AppTheme.SelectedBorder)) e.Graphics.DrawPath(border, path);
            int coverSize = bounds.Height - S(22);
            var cover = new Rectangle(bounds.X + S(12), bounds.Y + S(11), coverSize, coverSize);
            using (var shadow = AppTheme.Rounded(new Rectangle(cover.X, cover.Y + S(2), cover.Width, cover.Height), S(9)))
            using (var fill = new SolidBrush(Color.FromArgb(20, 39, 57, 92))) e.Graphics.FillPath(fill, shadow);
            var image = GetThumbnail(album); var state = e.Graphics.Save();
            using (var shape = AppTheme.Rounded(cover, S(9))) e.Graphics.SetClip(shape);
            using (var fill = new LinearGradientBrush(cover, AppTheme.IsDark ? Color.FromArgb(47, 66, 99) : Color.FromArgb(224, 235, 253), AppTheme.IsDark ? Color.FromArgb(53, 46, 79) : Color.FromArgb(231, 225, 247), 45)) e.Graphics.FillRectangle(fill, cover);
            if (image != null)
            {
                float scale = Math.Min((float)cover.Width / image.Width, (float)cover.Height / image.Height);
                var size = new Size((int)(image.Width * scale), (int)(image.Height * scale));
                e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                e.Graphics.DrawImage(image, cover.X + (cover.Width - size.Width) / 2, cover.Y + (cover.Height - size.Height) / 2, size.Width, size.Height);
            }
            else TextRenderer.DrawText(e.Graphics, "♪", titleFont, cover, AppTheme.Accent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            e.Graphics.Restore(state);
            int left = cover.Right + S(13), width = Math.Max(1, bounds.Right - left - S(15));
            var flags = TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(e.Graphics, album.Title, titleFont, new Rectangle(left, bounds.Y + S(11), width, S(22)), AppTheme.Ink, flags);
            TextRenderer.DrawText(e.Graphics, album.Artist + "  ·  " + album.Tracks.Count + (album.Tracks.Count == 1 ? " track" : " tracks"), detailFont,
                new Rectangle(left, bounds.Y + S(35), width, S(19)), AppTheme.Muted, flags);
            string work = WorkStatus?.Invoke(album), label = work ?? album.Coverage;
            Color tone = work != null ? AppTheme.Accent : album.MissingCount > 0 ? AppTheme.Warning : AppTheme.Positive;
            var badge = new Rectangle(left - S(1), bounds.Y + S(57), Math.Min(width, TextRenderer.MeasureText(label, detailFont).Width + S(13)), S(19));
            using (var path = AppTheme.Rounded(badge, S(6)))
            using (var fill = new SolidBrush(AppTheme.Blend(tone, AppTheme.Surface, AppTheme.IsDark ? .87f : .92f))) e.Graphics.FillPath(fill, path);
            var badgeText = badge; badgeText.X += S(6); badgeText.Width = Math.Max(1, badgeText.Width - S(10));
            TextRenderer.DrawText(e.Graphics, label, detailFont, badgeText, tone, flags);
            if (selected && Focused && ShowFocusCues)
                using (var path = AppTheme.Rounded(Rectangle.Inflate(tile, -1, -1), S(11)))
                using (var focus = new Pen(AppTheme.Accent, 1.2f)) e.Graphics.DrawPath(focus, path);
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
