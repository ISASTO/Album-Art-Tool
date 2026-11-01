using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Windows.Forms;
using AlbumArtTool;
using AlbumArtTool.Core;
using AlbumArtTool.Design;

internal static partial class Tests
{
    private static void ScrollbarGuiSmoke(string output)
    {
        Exception failure = null;
        using (var form = new Form { Text = "Scrollbar check", ClientSize = new Size(900, 680), Font = new Font("Segoe UI", 10) })
        {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(12) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
            var albums = new AlbumList { Dock = DockStyle.Fill };
            var rows = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true };
            rows.Columns.Add("Track", 350); rows.Columns.Add("Artist", 350);
            for (int i = 0; i < 100; i++) { var row = new ListViewItem("Song " + (i + 1)); row.SubItems.Add("Example artist"); rows.Items.Add(row); }
            albums.SetAlbums(Enumerable.Range(1, 100).Select(i => new Album { Key = i.ToString(), Title = "Album " + i, Artist = "Example artist" }));
            layout.Controls.Add(albums, 0, 0); layout.Controls.Add(rows, 1, 0); form.Controls.Add(layout); AppTheme.Apply(form);
            form.Shown += (s, e) =>
            {
                try
                {
                    albums.Focus(); int before = albums.TopItem.Index;
                    SendMessage(albums.Handle, 0x0115, (IntPtr)3, IntPtr.Zero);
                    Assert(albums.TopItem.Index > before, "rounded virtual-album scrollbars retain native page scrolling");
                    SendMessage(albums.Handle, 0x0100, (IntPtr)Keys.End, IntPtr.Zero);
                    Assert(albums.SelectedIndices.Contains(99), "rounded album scrollbars retain native keyboard navigation to the end");
                    rows.Focus(); before = rows.TopItem.Index;
                    SendMessage(rows.Handle, 0x020A, new IntPtr(unchecked(-120 << 16)), IntPtr.Zero);
                    Assert(rows.TopItem.Index > before, "rounded track scrollbars retain mouse-wheel scrolling");
                    rows.EnsureVisible(99); Assert(rows.TopItem.Index > 0, "long track lists still reveal the final song");
                    rows.Columns[1].Width = 900; form.Refresh();
                    using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(System.IO.Path.Combine(output, "rounded-scrollbars-dark.png"), ImageFormat.Png); }
                    AppTheme.SetDark(false, false); form.Refresh();
                    using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(System.IO.Path.Combine(output, "rounded-scrollbars-light.png"), ImageFormat.Png); }
                    rows.Items.Clear(); albums.SetAlbums(new Album[0]);
                    Assert(rows.Items.Count == 0 && albums.AlbumCount == 0, "scrollbars handle lists becoming empty without retaining stale rows");
                }
                catch (Exception ex) { failure = ex; }
                finally { AppTheme.SetDark(true, false); form.Close(); }
            };
            Application.Run(form);
        }
        if (failure != null) throw failure;
    }
}
