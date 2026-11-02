using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using AlbumArtTool;
using AlbumArtTool.Core;
using AlbumArtTool.Design;

internal static partial class Tests
{
    private static void ThemeTimingGuiSmoke()
    {
        guiStage = "large-window theme timing"; Exception failure = null; AppTheme.SetDark(true, false);
        using (var form = new Form { Text = "Theme timing", ClientSize = new Size(1200, 800), StartPosition = FormStartPosition.CenterScreen, Font = new Font("Segoe UI", 10) })
        {
            var root = new FrostCanvas { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(12) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45)); root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
            var albums = new AlbumList { Dock = DockStyle.Fill };
            albums.SetAlbums(Enumerable.Range(1, 700).Select(n => new Album { Key = n.ToString(), Title = "Album " + n, Artist = "Example artist" }));
            var rows = new ListView { View = View.Details, Dock = DockStyle.Fill, FullRowSelect = true };
            rows.Columns.Add("Song", 350); rows.Columns.Add("Artist", 350);
            rows.BeginUpdate();
            for (int n = 0; n < 6500; n++) { var row = new ListViewItem("Song " + (n + 1)) { ForeColor = n % 2 == 0 ? AppTheme.Muted : AppTheme.Ink }; row.SubItems.Add("Example artist"); rows.Items.Add(row); }
            rows.EndUpdate(); root.Controls.Add(albums, 0, 0); root.Controls.Add(rows, 1, 0); form.Controls.Add(root); AppTheme.Apply(form);
            form.Shown += (s, e) =>
            {
                try
                {
                    var clock = Stopwatch.StartNew(); var layer = ThemeTransition.Prepare(form); double prepare = clock.Elapsed.TotalMilliseconds;
                    clock.Restart(); AppTheme.SetDark(false, false); double recolor = clock.Elapsed.TotalMilliseconds;
                    clock.Restart(); layer.Start(); double start = clock.Elapsed.TotalMilliseconds;
                    using (var frame = new Bitmap(layer.Width, layer.Height))
                    using (var graphics = Graphics.FromImage(frame))
                    {
                        clock.Restart();
                        for (int n = 1; n <= 12; n++) layer.PaintFrame(graphics, n / 14.0);
                        double frameMs = clock.Elapsed.TotalMilliseconds / 12;
                        Assert(frameMs < 50, "large-window gradient frames stay below the 50 ms rendering budget");
                        Console.WriteLine("THEME TIMING: 700 virtual albums / 6500 track rows / " + layer.Width + "x" + layer.Height +
                            "; prepare=" + prepare.ToString("F1") + "ms; recolor=" + recolor.ToString("F1") + "ms; target=" + start.ToString("F1") +
                            "ms; frame=" + frameMs.ToString("F2") + "ms; duration=" + ThemeTransition.Duration + "ms.");
                    }
                    layer.Finish();
                    clock.Restart(); var cached = ThemeTransition.Prepare(form); double cachedPrepare = clock.Elapsed.TotalMilliseconds;
                    clock.Restart(); AppTheme.SetDark(true, false); double cachedRecolor = clock.Elapsed.TotalMilliseconds;
                    clock.Restart(); cached.Start(); double cachedStart = clock.Elapsed.TotalMilliseconds;
                    Console.WriteLine("THEME CACHED: prepare=" + cachedPrepare.ToString("F1") + "ms; recolor=" + cachedRecolor.ToString("F1") + "ms; target=" + cachedStart.ToString("F1") +
                        "ms; source-hit=" + cached.BeforeCached + "; target-hit=" + cached.AfterCached + ".");
                    Assert(cached.BeforeCached && cached.AfterCached, "unchanged large-window toggles reuse both rendered appearances");
                    cached.Finish();
                    Assert(rows.Items.Count == 6500 && albums.AlbumCount == 700, "theme timing retains large-library contents");
                }
                catch (Exception ex) { failure = ex; }
                finally { form.Close(); AppTheme.SetDark(true, false); }
            };
            Application.Run(form);
        }
        if (failure != null) throw failure;
    }
}
