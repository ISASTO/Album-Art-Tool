using System;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using AlbumArtTool.Design;

internal static partial class Tests
{
    private static void ThemeBufferPixels()
    {
        using (var before = new Bitmap(400, 8))
        using (var after = new Bitmap(400, 8))
        using (var result = new Bitmap(400, 8))
        {
            using (var graphics = Graphics.FromImage(before)) graphics.Clear(Color.FromArgb(20, 80, 200));
            using (var graphics = Graphics.FromImage(after)) graphics.Clear(Color.FromArgb(240, 160, 40));
            using (var buffer = new ThemeWipeBuffer(before, after))
            using (var graphics = Graphics.FromImage(result)) buffer.Draw(graphics, .5);
            Assert(result.GetPixel(10, 4).ToArgb() == Color.FromArgb(240, 160, 40).ToArgb() && result.GetPixel(390, 4).ToArgb() == Color.FromArgb(20, 80, 200).ToArgb(),
                "the fast wipe preserves the exact colors on both sides of the gradient");
            var middle = result.GetPixel(249, 4);
            Assert(Math.Abs(middle.R - 130) <= 3 && Math.Abs(middle.G - 120) <= 3 && Math.Abs(middle.B - 120) <= 3 && middle.A == 255,
                "the pixel-buffer gradient blends each RGB channel without artifacts or transparency");
        }
    }
    private static void ThemeCacheGuiSmoke()
    {
        guiStage = "theme snapshot freshness"; Exception failure = null; AppTheme.SetDark(true, false);
        using (var red = new Bitmap(80, 80))
        using (var green = new Bitmap(80, 80))
        using (var form = new Form { Text = "Snapshot freshness", ClientSize = new Size(800, 600), StartPosition = FormStartPosition.CenterScreen })
        {
            using (var g = Graphics.FromImage(red)) g.Clear(Color.Red);
            using (var g = Graphics.FromImage(green)) g.Clear(Color.LimeGreen);
            var root = new FrostCanvas { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Padding = new Padding(12) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140)); root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            var input = new TextBox { Text = "The current album", Dock = DockStyle.Fill };
            var picture = new PictureBox { Image = red, SizeMode = PictureBoxSizeMode.StretchImage, Dock = DockStyle.Fill };
            var rows = new ListView { View = View.Details, Dock = DockStyle.Fill, FullRowSelect = true };
            rows.Columns.Add("Track", 700); for (int n = 0; n < 50; n++) rows.Items.Add("Song " + n);
            var progress = new ThemeProgressBar { Dock = DockStyle.Fill, Maximum = 100, Value = 12 };
            root.Controls.Add(input, 0, 0); root.SetColumnSpan(input, 2); root.Controls.Add(picture, 0, 1); root.Controls.Add(rows, 1, 1);
            root.Controls.Add(progress, 0, 2); root.SetColumnSpan(progress, 2); form.Controls.Add(root); AppTheme.Apply(form);
            form.Shown += async (s, e) =>
            {
                try
                {
                    await Task.Delay(60);
                    var first = ThemeTransition.Prepare(form); AppTheme.SetDark(false, false); first.Start(); first.Finish();
                    var repeat = ThemeTransition.Prepare(form); AppTheme.SetDark(true, false); repeat.Start();
                    Assert(repeat.BeforeCached && repeat.AfterCached, "snapshot reuse works with native text, artwork, tracks and progress controls"); repeat.Finish();
                    picture.Image = green; rows.Items[20].Selected = true; rows.EnsureVisible(20); progress.Value = 64; await Task.Delay(30);
                    var changed = ThemeTransition.Prepare(form); AppTheme.SetDark(false, false); changed.Start();
                    Assert(!changed.BeforeCached && !changed.AfterCached, "new artwork, list scroll/selection and progress invalidate both old views");
                    using (var frame = new Bitmap(changed.Width, changed.Height))
                    {
                        using (var graphics = Graphics.FromImage(frame)) changed.PaintFrame(graphics, 1);
                        var point = form.PointToClient(picture.PointToScreen(new Point(picture.Width / 2, picture.Height / 2)));
                        Assert(frame.GetPixel(point.X, point.Y).ToArgb() == Color.LimeGreen.ToArgb(), "the transition renders the new cover rather than a cached old cover");
                    }
                    changed.Finish();
                    Assert(input.Text == "The current album" && rows.Items[20].Selected && progress.Value == 64, "theme caching preserves live input, selection and job progress");
                }
                catch (Exception ex) { failure = ex; }
                finally { picture.Image = null; form.Close(); AppTheme.SetDark(true, false); }
            };
            Application.Run(form);
        }
        if (failure != null) throw failure;
    }
}
