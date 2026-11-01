using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using AlbumArtTool;
using AlbumArtTool.Design;

internal static partial class Tests
{
    private static void ThemeWipeGuiSmoke(string output)
    {
        guiStage = "theme animation"; Exception failure = null; AppTheme.SetDark(true, false);
        using (var form = new Form { Text = "Theme transition", ClientSize = new Size(900, 620), StartPosition = FormStartPosition.CenterScreen, Font = new Font("Segoe UI", 10) })
        {
            var root = new FrostCanvas { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(24) };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var input = new TextBox { Text = "Keep this staged input", Dock = DockStyle.Fill };
            var button = new ThemeButton { Text = "A native-control theme transition", Dock = DockStyle.Fill };
            var list = new ListView { View = View.Details, Dock = DockStyle.Fill, FullRowSelect = true };
            list.Columns.Add("Track", 500); for (int n = 0; n < 40; n++) list.Items.Add("A song " + (n + 1));
            root.Controls.Add(input, 0, 0); root.Controls.Add(button, 0, 1); root.Controls.Add(list, 0, 2); form.Controls.Add(root); AppTheme.Apply(form);
            form.Shown += async (s, e) =>
            {
                try
                {
                    await Task.Delay(60); list.Items[2].Selected = true;
                    // Exercise animation even if the CI desktop requests reduced
                    // motion; the real toggle separately honors that preference.
                    var layer = ThemeTransition.Prepare(form); AppTheme.SetDark(false, false); layer.Start();
                    Assert(layer.Visible && layer.Bounds == form.ClientRectangle && form.Controls.GetChildIndex(layer) == 0, "a single opaque theme layer stays above every native child window");
                    Assert(input.BackColor == AppTheme.Input && input.Text == "Keep this staged input" && list.Items[2].Selected, "the next theme is ready underneath the wipe without losing edit or selection state");
                    foreach (double t in new[] { .0, .25, .5, .75, 1.0 })
                        using (var frame = new Bitmap(layer.Width, layer.Height))
                        {
                            using (var graphics = Graphics.FromImage(frame)) layer.PaintFrame(graphics, t);
                            frame.Save(Path.Combine(output, "theme-wipe-frame-" + (int)(t * 100) + ".png"), ImageFormat.Png);
                            if (t == .5) Assert(frame.GetPixel(8, 8).R > 200 && frame.GetPixel(frame.Width - 8, 8).R < 80, "the middle frame is a spatial dark-to-light wipe across native controls, not a staggered control repaint");
                        }
                    await Task.Delay(150);
                    using (var screen = ScreenImage(form.RectangleToScreen(form.ClientRectangle))) screen.Save(Path.Combine(output, "theme-wipe-live.png"), ImageFormat.Png);
                    var reverse = ThemeTransition.Prepare(form); AppTheme.SetDark(true, false); reverse.Start();
                    Assert(form.Controls.OfType<ThemeTransition>().Count() == 1, "reversing a theme change replaces its layer instead of stacking overlays");
                    for (int n = 0; n < 100 && form.Controls.OfType<ThemeTransition>().Any(); n++) await Task.Delay(20);
                    Assert(!form.Controls.OfType<ThemeTransition>().Any() && input.Text == "Keep this staged input" && list.Items[2].Selected,
                        "the animation cleans up its buffers and reveals working controls with their state intact");
                }
                catch (Exception ex) { failure = ex; }
                finally { form.Close(); AppTheme.SetDark(true, false); }
            };
            Application.Run(form);
        }
        if (failure != null) throw failure;
    }
}
