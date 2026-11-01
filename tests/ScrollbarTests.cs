using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using AlbumArtTool;
using AlbumArtTool.Core;
using AlbumArtTool.Design;

internal static partial class Tests
{
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern IntPtr GetCapture();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int TestWindowStyle(IntPtr window, int index);
    [StructLayout(LayoutKind.Sequential)] private struct TestScrollInfo { public int Size, Mask, Min, Max; public uint Page; public int Position, Track; }
    [DllImport("user32.dll")] private static extern bool GetScrollInfo(IntPtr window, int bar, ref TestScrollInfo info);
    private static int ScrollPosition(Control control, bool vertical)
    {
        var info = new TestScrollInfo { Size = Marshal.SizeOf(typeof(TestScrollInfo)), Mask = 7 };
        GetScrollInfo(control.Handle, vertical ? 1 : 0, ref info); return info.Position;
    }
    private static Bitmap ScreenImage(Rectangle bounds)
    {
        var image = new Bitmap(bounds.Width, bounds.Height);
        using (var graphics = Graphics.FromImage(image)) graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        return image;
    }
    private static int DifferentPixels(Bitmap a, Bitmap b)
    {
        if (a.Size != b.Size) return int.MaxValue;
        int count = 0;
        for (int y = 0; y < a.Height; y++) for (int x = 0; x < a.Width; x++) if (a.GetPixel(x, y) != b.GetPixel(x, y)) count++;
        return count;
    }
    private static async Task DragBar(Control bar, bool vertical)
    {
        var bounds = bar.RectangleToScreen(bar.ClientRectangle);
        Cursor.Position = vertical ? new Point(bounds.Left + bounds.Width / 2, bounds.Top + 12) : new Point(bounds.Left + 12, bounds.Top + bounds.Height / 2);
        await Task.Delay(90);
        Console.WriteLine("DRAG " + (vertical ? "V" : "H") + " bounds=" + bounds + " cursor=" + Cursor.Position + " hit=" + WindowFromPoint(Cursor.Position) + " bar=" + bar.Handle + " visible=" + bar.Visible);
        mouse_event(2, 0, 0, 0, UIntPtr.Zero); await Task.Delay(90);
        try
        {
            Assert(bar.Capture, "rounded scrollbar receives real mouse capture on its thumb (capture=" + GetCapture() + ", bar=" + bar.Handle + ")");
            for (int step = 1; step <= 6; step++)
            {
                Cursor.Position = vertical ? new Point(bounds.Left + bounds.Width / 2, bounds.Top + 12 + (bounds.Height - 19) * step / 6) :
                    new Point(bounds.Left + 12 + (bounds.Width - 19) * step / 6, bounds.Top + bounds.Height / 2);
                await Task.Delay(25);
            }
        }
        finally { mouse_event(4, 0, 0, 0, UIntPtr.Zero); }
        await Task.Delay(40);
    }
    private static void OtherScrollControlsGuiSmoke(string output)
    {
        Exception failure = null;
        using (var form = new Form { Text = "Other scrolling controls", StartPosition = FormStartPosition.CenterScreen, ClientSize = new Size(900, 660), Font = new Font("Segoe UI", 10) })
        {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(12) };
            for (int n = 0; n < 2; n++) { layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); }
            var queue = new ListBox { Dock = DockStyle.Fill };
            var tree = new TreeView { Dock = DockStyle.Fill };
            var text = new TextBox { Dock = DockStyle.Fill, Multiline = true, WordWrap = false, ScrollBars = ScrollBars.Both };
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = false, FlowDirection = FlowDirection.TopDown };
            for (int n = 0; n < 200; n++)
            {
                queue.Items.Add("Queued album " + (n + 1)); tree.Nodes.Add("Duplicate match " + (n + 1));
                flow.Controls.Add(new Label { Text = "Artwork result " + (n + 1), Size = new Size(300, 28) });
            }
            text.Lines = Enumerable.Range(1, 200).Select(n => "Details line " + n + ": " + new string('x', 120)).ToArray();
            layout.Controls.Add(queue, 0, 0); layout.Controls.Add(tree, 1, 0); layout.Controls.Add(text, 0, 1); layout.Controls.Add(flow, 1, 1);
            form.Controls.Add(layout); AppTheme.Apply(form);
            form.Shown += async (s, e) =>
            {
                try
                {
                    await Task.Delay(80);
                    foreach (var control in new Control[] { queue, tree, text, flow })
                    {
                        var host = ThemeScrollbars.For(control);
                        Console.WriteLine("SCROLL CONTROL " + control.GetType().Name + " client=" + control.ClientSize + " viewport=" + host.VisibleContent.Size + " bar=" + host.VerticalBar.Visible);
                        Assert(host.VerticalBar.Visible && control.ClientSize == host.VisibleContent.Size, control.GetType().Name + " keeps its native scrollbar outside the visible viewport");
                        var info = new TestScrollInfo { Size = Marshal.SizeOf(typeof(TestScrollInfo)), Mask = 7 }; GetScrollInfo(control.Handle, 1, ref info);
                        Console.WriteLine("SCROLL RANGE " + control.GetType().Name + " " + info.Min + ".." + info.Max + " page=" + info.Page + " at=" + info.Position);
                        host.VerticalBar.AccessibilityObject.Value = ((info.Max - info.Page + 1) / 2).ToString(); await Task.Delay(30);
                        Assert(ScrollPosition(control, true) > 0, control.GetType().Name + " follows its rounded scrollbar position");
                    }
                    Assert(queue.TopIndex > 0 && tree.TopNode.Index > 0 && SendMessage(text.Handle, 0x00CE, IntPtr.Zero, IntPtr.Zero).ToInt32() > 0 && flow.AutoScrollPosition.Y < 0,
                        "queue, duplicate tree, details text and artwork flow actually scroll their content");
                    using (var screen = ScreenImage(new Rectangle(form.Location, form.Size))) screen.Save(Path.Combine(output, "other-rounded-scroll-controls.png"), ImageFormat.Png);
                }
                catch (Exception ex)
                {
                    using (var screen = ScreenImage(new Rectangle(form.Location, form.Size))) screen.Save(Path.Combine(output, "other-scroll-failure.png"), ImageFormat.Png);
                    failure = ex;
                }
                finally { form.Close(); }
            };
            using (var guard = new System.Threading.Timer(_ =>
            {
                Console.Error.WriteLine("FAIL: additional scrolling controls did not finish within 60 seconds");
                using (var screen = ScreenImage(Screen.PrimaryScreen.Bounds)) screen.Save(Path.Combine(output, "scrolling-watchdog.png"), ImageFormat.Png);
                Environment.Exit(1);
            }, null, 60000, System.Threading.Timeout.Infinite)) Application.Run(form);
        }
        if (failure != null) throw failure;
    }
    private static void ScrollbarGuiSmoke(string output)
    {
        Exception failure = null;
        using (var form = new Form { Text = "Scrollbar check", StartPosition = FormStartPosition.CenterScreen, ClientSize = new Size(900, 680), Font = new Font("Segoe UI", 10) })
        {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(12) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
            var albums = new AlbumList { Dock = DockStyle.Fill };
            var details = new DetailPanel { Dock = DockStyle.Fill };
            var inset = new InsetPanel(); details.Controls.Add(inset);
            var rows = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true };
            inset.Controls.Add(rows); rows.Columns.Add("Track", 350); rows.Columns.Add("Artist", 350);
            for (int i = 0; i < 100; i++) { var row = new ListViewItem("Song " + (i + 1)); row.SubItems.Add("Example artist"); rows.Items.Add(row); }
            albums.SetAlbums(Enumerable.Range(1, 100).Select(i => new Album { Key = i.ToString(), Title = "Album " + i, Artist = "Example artist" }));
            layout.Controls.Add(albums, 0, 0); layout.Controls.Add(details, 1, 0); form.Controls.Add(layout); AppTheme.Apply(form);
            form.Shown += async (s, e) =>
            {
                try
                {
                    form.Activate(); form.BringToFront(); await Task.Delay(80);
                    var albumHost = ThemeScrollbars.For(albums); var rowHost = ThemeScrollbars.For(rows);
                    Assert(albumHost.VerticalBar.Visible && rowHost.VerticalBar.Visible && rowHost.HorizontalBar.Visible, "independent scrollbars follow native virtual and track-list ranges");
                    Assert(rows.ClientSize == rowHost.VisibleContent.Size && albums.ClientSize == albumHost.VisibleContent.Size,
                        "native scrollbar areas lie entirely outside the clipped content viewports");
                    Assert((TestWindowStyle(details.Handle, -20) & 0x02000000) == 0, "track details do not use subtree compositing that defers visible updates");
                    albums.Focus(); int before = albums.TopItem.Index;
                    SendMessage(albums.Handle, 0x0115, (IntPtr)3, IntPtr.Zero);
                    Assert(albums.TopItem.Index > before, "rounded virtual-album scrollbars retain native page scrolling");
                    SendMessage(albums.Handle, 0x0100, (IntPtr)Keys.End, IntPtr.Zero);
                    Assert(albums.SelectedIndices.Contains(99), "rounded album scrollbars retain native keyboard navigation to the end");
                    rows.Focus(); Cursor.Position = rows.PointToScreen(new Point(100, 85)); await Task.Delay(30);
                    before = rows.TopItem.Index;
                    using (var prior = ScreenImage(rowHost.VisibleContent))
                    {
                        SendMessage(rows.Handle, 0x020A, new IntPtr(unchecked(-120 << 16)), IntPtr.Zero); await Task.Delay(80);
                        using (var after = ScreenImage(rowHost.VisibleContent))
                        {
                            Assert(rows.TopItem.Index > before && DifferentPixels(prior, after) > 30,
                                "wheel scrolling repaints visible track names while the pointer remains over the list");
                            after.Save(Path.Combine(output, "track-scroll-pointer-over-list.png"), ImageFormat.Png);
                        }
                    }
                    rows.TopItem = rows.Items[0]; await Task.Delay(35);
                    await DragBar(rowHost.VerticalBar, true);
                    Assert(rows.TopItem.Index > 70, "dragging the rounded track thumb scrolls the displayed rows to the bottom");
                    await DragBar(rowHost.HorizontalBar, false);
                    Assert(ScrollPosition(rows, false) > 100, "dragging the horizontal thumb scrolls wide track columns");
                    albums.SetAlbums(Enumerable.Range(1, 100000).Select(i => new Album { Key = i.ToString(), Title = "Album " + i, Artist = "Example artist" }));
                    await Task.Delay(60); await DragBar(albumHost.VerticalBar, true);
                    Assert(albums.TopItem.Index > 90000, "thumb dragging reaches positions above 65,535 in a 100,000-album virtual library");
                    using (var screen = ScreenImage(new Rectangle(form.Location, form.Size))) screen.Save(Path.Combine(output, "scrollbars-dragged-large-library.png"), ImageFormat.Png);
                    for (int i = 0; i < 10; i++)
                    {
                        var bar = albumHost.VerticalBar;
                        Cursor.Position = i % 2 == 0 ? bar.PointToScreen(new Point(2, 30 + i * 15)) : albums.PointToScreen(new Point(100, 100));
                        await Task.Delay(25);
                        using (var strip = ScreenImage(bar.RectangleToScreen(bar.ClientRectangle)))
                        {
                            var pixel = strip.GetPixel(1, strip.Height / 2);
                            Assert(Math.Abs(pixel.R - AppTheme.Surface.R) < 8 && Math.Abs(pixel.G - AppTheme.Surface.G) < 8 && Math.Abs(pixel.B - AppTheme.Surface.B) < 8,
                                "hover/leave frames keep the scrollbar gutter free of stock Windows repainting");
                        }
                    }
                    rows.EnsureVisible(99); Assert(rows.TopItem.Index > 0, "long track lists still reveal the final song");
                    rows.Columns[1].Width = 900; await Task.Delay(40);
                    using (var screen = ScreenImage(new Rectangle(form.Location, form.Size))) screen.Save(Path.Combine(output, "rounded-scrollbars-dark.png"), ImageFormat.Png);
                    AppTheme.SetDark(false, false); await Task.Delay(60);
                    using (var screen = ScreenImage(new Rectangle(form.Location, form.Size))) screen.Save(Path.Combine(output, "rounded-scrollbars-light.png"), ImageFormat.Png);
                    rows.Items.Clear(); albums.SetAlbums(new Album[0]); await Task.Delay(40);
                    Assert(rows.Items.Count == 0 && albums.AlbumCount == 0 && !albumHost.VerticalBar.Visible && !rowHost.VerticalBar.Visible,
                        "scrollbars disappear when lists become empty without retaining stale rows");
                }
                catch (Exception ex)
                {
                    using (var screen = ScreenImage(new Rectangle(form.Location, form.Size))) screen.Save(Path.Combine(output, "scrollbar-failure-screen.png"), ImageFormat.Png);
                    failure = ex;
                }
                finally { mouse_event(4, 0, 0, 0, UIntPtr.Zero); AppTheme.SetDark(true, false); form.Close(); }
            };
            Application.Run(form);
        }
        if (failure != null) throw failure;
        OtherScrollControlsGuiSmoke(output);
    }
}
