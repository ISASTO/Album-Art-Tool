using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AlbumArtTool.Design
{
    // An owned, non-activating popup is composited ABOVE the owner's entire HWND
    // tree. Native edit/list/header windows cannot paint through this surface.
    // Adding a child overlay to the live layout did not provide that guarantee.
    internal sealed class ThemeTransition : Form
    {
        private readonly Form form;
        private Bitmap before, after;
        private readonly Timer timer = new Timer { Interval = 16 };
        private readonly Stopwatch clock = new Stopwatch();
        private bool finished, revealing;
        private ThemeWipeBuffer wipe;
        internal bool BeforeCached { get; private set; }
        internal bool AfterCached { get; private set; }
        internal const int Duration = 180;
        internal double Position => revealing ? 1 : Math.Min(1, clock.Elapsed.TotalMilliseconds / Duration);
        internal static ThemeTransition For(Form owner) => owner.OwnedForms.OfType<ThemeTransition>().FirstOrDefault(t => !t.IsDisposed);
        internal static bool MotionAllowed
        {
            get
            {
                bool enabled;
                return !SystemInformation.HighContrast && (!SystemParametersInfo(0x1042, 0, out enabled, 0) || enabled);
            }
        }
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get { var parameters = base.CreateParams; parameters.ExStyle |= 0x08000080; return parameters; } // NOACTIVATE | TOOLWINDOW
        }
        internal static ThemeTransition Prepare(Form form)
        {
            if (!form.Visible || form.WindowState == FormWindowState.Minimized || form.ClientSize.Width < 1 || form.ClientSize.Height < 1) return null;
            var previous = For(form);
            ulong key = ThemeSnapshotCache.Key(form);
            Bitmap image = previous?.CurrentImage();
            bool cached = false;
            if (image == null) { image = ThemeSnapshotCache.Get(form, key, AppTheme.IsDark); cached = image != null; }
            if (image == null)
            {
                // WM_PRINT captures native controls directly. Do not first repaint
                // them on screen, and do not insert anything into the live layout.
                AppTheme.SettleSurface(form); key = ThemeSnapshotCache.Key(form);
                image = CaptureRoots(form); ThemeSnapshotCache.Store(form, key, AppTheme.IsDark, image);
            }
            var layer = new ThemeTransition(form, image) { BeforeCached = cached };
            try
            {
                layer.Show(form); layer.Update();
                // Keep the prior surface visible until its replacement is ready.
                previous?.Finish(false); return layer;
            }
            catch { layer.Finish(false); throw; }
        }
        private ThemeTransition(Form form, Bitmap image)
        {
            this.form = form; before = image;
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.Manual; Bounds = form.RectangleToScreen(form.ClientRectangle);
            BackColor = AppTheme.Canvas; TabStop = false; AccessibleName = "Changing appearance";
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque, true);
            timer.Tick += (s, e) => { if (finished) return; if (Position >= 1 || form.ClientSize != before.Size) Finish(); else Invalidate(); };
            form.ResizeBegin += Resizing; form.LocationChanged += OwnerMoved; form.ClientSizeChanged += OwnerResized;
            form.VisibleChanged += OwnerVisibility; form.FormClosed += OwnerClosed;
        }
        internal void Start()
        {
            if (IsDisposed) return;
            try
            {
                AppTheme.SettleSurface(form);
                ulong key = ThemeSnapshotCache.Key(form);
                after = ThemeSnapshotCache.Get(form, key, AppTheme.IsDark); AfterCached = after != null;
                if (after == null) { after = CaptureRoots(form); ThemeSnapshotCache.Store(form, key, AppTheme.IsDark, after); }
                wipe = new ThemeWipeBuffer(before, after); clock.Start(); timer.Start(); Invalidate();
            }
            catch { Finish(); throw; }
        }
        private void Resizing(object sender, EventArgs e) => Finish();
        private void OwnerMoved(object sender, EventArgs e) { if (!finished && Bounds.Location != form.PointToScreen(Point.Empty)) Finish(); }
        private void OwnerResized(object sender, EventArgs e) { if (!finished && form.ClientSize != before.Size) Finish(); }
        private void OwnerVisibility(object sender, EventArgs e) { if (!form.Visible) Finish(false); }
        private void OwnerClosed(object sender, FormClosedEventArgs e) => Finish(false);
        internal void Finish(bool reveal = true)
        {
            if (finished) return; finished = true; timer.Stop();
            try
            {
                if (reveal && !form.IsDisposed && !form.Disposing && form.Visible)
                {
                    // Present the final image before repainting the live controls,
                    // then remove the cover only AFTER every native child is ready.
                    revealing = true; if (Visible) Refresh();
                    RedrawWindow(form.Handle, IntPtr.Zero, IntPtr.Zero, 0x0185); // INVALIDATE | ERASE | ALLCHILDREN | UPDATENOW
                    GdiFlush();
                    try { DwmFlush(); } catch (DllNotFoundException) { } catch (EntryPointNotFoundException) { }
                }
            }
            finally { Dispose(); }
        }
        private static Bitmap CaptureRoots(Form form)
        {
            var image = new Bitmap(form.ClientSize.Width, form.ClientSize.Height, PixelFormat.Format24bppRgb);
            try
            {
                using (var graphics = Graphics.FromImage(image)) graphics.Clear(AppTheme.Canvas);
                foreach (Control root in form.Controls.Cast<Control>().Reverse().ToArray())
                    if (root.Visible && root.Width > 0 && root.Height > 0) root.DrawToBitmap(image, root.Bounds);
                return image;
            }
            catch { image.Dispose(); throw; }
        }
        private Bitmap CurrentImage()
        {
            var image = new Bitmap(before.Width, before.Height, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(image)) PaintFrame(graphics, Position);
            return image;
        }
        protected override void OnPaint(PaintEventArgs e) => PaintFrame(e.Graphics, Position);
        internal void PaintFrame(Graphics graphics, double position)
        {
            if (after == null || wipe == null || position <= 0) { graphics.DrawImageUnscaled(before, 0, 0); return; }
            if (position >= 1) { graphics.DrawImageUnscaled(after, 0, 0); return; }
            wipe.Draw(graphics, position);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                timer.Dispose(); form.ResizeBegin -= Resizing; form.LocationChanged -= OwnerMoved; form.ClientSizeChanged -= OwnerResized;
                form.VisibleChanged -= OwnerVisibility; form.FormClosed -= OwnerClosed;
                wipe?.Dispose(); wipe = null; before?.Dispose(); after?.Dispose(); before = after = null;
            }
            base.Dispose(disposing);
        }
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SystemParametersInfo(uint action, uint parameter, [MarshalAs(UnmanagedType.Bool)] out bool value, uint flags);
        [DllImport("gdi32.dll")] private static extern bool GdiFlush();
        [DllImport("dwmapi.dll")] private static extern int DwmFlush();
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RedrawWindow(IntPtr window, IntPtr update, IntPtr region, uint flags);
    }
}
