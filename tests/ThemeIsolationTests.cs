using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using AlbumArtTool.Design;

internal static partial class Tests
{
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    private static void ThemeIsolationGuiSmoke(string output)
    {
        guiStage = "native window isolation during theme changes"; Exception failure = null; AppTheme.SetDark(true, false);
        using (var form = new Form { Text = "Native theme isolation", ClientSize = new Size(800, 580), StartPosition = FormStartPosition.CenterScreen })
        {
            var root = new FrostCanvas { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 2 };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var input = new TextBox { Text = "Keep focus and selection", Dock = DockStyle.Fill };
            var rows = new ListView { Dock = DockStyle.Fill, View = View.Details }; rows.Columns.Add("Native header", 550);
            for (int n = 0; n < 30; n++) rows.Items.Add("A track " + n);
            root.Controls.Add(input, 0, 0); root.Controls.Add(rows, 0, 1); form.Controls.Add(root); AppTheme.Apply(form);
            form.Shown += async (s, e) =>
            {
                try
                {
                    form.Activate(); input.Focus(); input.Select(2, 5); await Task.Delay(60);
                    var layer = ThemeTransition.Prepare(form);
                    Assert(layer.Owner == form && (TestWindowStyle(layer.Handle, -16) & 0x40000000) == 0 && (TestWindowStyle(layer.Handle, -20) & 0x08000000) != 0,
                        "the theme cover is a non-activating top-level owned window, outside the live child layout");
                    var inputPoint = input.PointToScreen(new Point(input.ClientSize.Width - 12, input.ClientSize.Height / 2));
                    var headerPoint = rows.PointToScreen(new Point(45, 10));
                    Assert(WindowFromPoint(inputPoint) == layer.Handle && WindowFromPoint(headerPoint) == layer.Handle,
                        "the independent transition covers native edits AND native list headers");
                    Assert(form.ActiveControl == input && input.Focused && input.SelectionStart == 2 && input.SelectionLength == 5,
                        "showing the transition does not activate another window or steal input focus");
                    using (var before = ScreenImage(form.RectangleToScreen(form.ClientRectangle)))
                    {
                        AppTheme.SetDark(false, false);
                        for (int n = 0; n < 3; n++)
                        {
                            // Force colors that would make any HWND paint-through
                            // immediately obvious, rather than testing only snapshots.
                            input.BackColor = n % 2 == 0 ? Color.Magenta : Color.Lime;
                            rows.BackColor = n % 2 == 0 ? Color.Lime : Color.Magenta;
                            input.Refresh(); rows.Refresh(); form.Refresh(); DwmFlush();
                            using (var during = ScreenImage(form.RectangleToScreen(form.ClientRectangle)))
                            {
                                var point = form.PointToClient(inputPoint);
                                Assert(during.GetPixel(point.X, point.Y).ToArgb() == before.GetPixel(point.X, point.Y).ToArgb(),
                                    "forced native/full-window repaints never bleed through the opaque transition cover");
                                if (n == 2) during.Save(Path.Combine(output, "theme-native-repaint-isolation.png"), ImageFormat.Png);
                            }
                        }
                    }
                    input.BackColor = AppTheme.Input; rows.BackColor = AppTheme.Surface; layer.Start();
                    for (int n = 0; n < 80 && ThemeTransition.For(form) != null; n++) await Task.Delay(20);
                    Assert(ThemeTransition.For(form) == null && input.Focused && input.Text == "Keep focus and selection", "a completed wipe restores live native controls without focus loss");
                    var resizing = ThemeTransition.Prepare(form); AppTheme.SetDark(true, false); resizing.Start(); form.Width += 48;
                    Assert(resizing.IsDisposed && ThemeTransition.For(form) == null, "resizing cancels the cover immediately without leaving an orphan window");
                    var closing = ThemeTransition.Prepare(form); form.Close();
                    Assert(closing.IsDisposed, "closing the owner disposes a prepared transition even before its animation starts");
                }
                catch (Exception ex) { failure = ex; }
                finally { form.Close(); AppTheme.SetDark(true, false); }
            };
            Application.Run(form);
        }
        if (failure != null) throw failure;
    }
}
