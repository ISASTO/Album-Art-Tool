using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AlbumArtTool.Design
{
    // A single opaque window covers all native child controls until both appearances
    // are ready. Only these two snapshots participate in the wipe, so native edit,
    // list and checkbox windows cannot reveal an intermediate mixed palette.
    internal sealed class ThemeTransition : Control
    {
        private readonly Form form;
        private Bitmap before, after;
        private readonly Timer timer = new Timer { Interval = 16 };
        private readonly Stopwatch clock = new Stopwatch();
        private bool finished;
        private ThemeWipeBuffer wipe;
        internal bool BeforeCached { get; private set; }
        internal bool AfterCached { get; private set; }
        internal const int Duration = 180;
        internal double Position => Math.Min(1, clock.Elapsed.TotalMilliseconds / Duration);
        internal static bool MotionAllowed
        {
            get
            {
                bool enabled;
                return !SystemInformation.HighContrast && (!SystemParametersInfo(0x1042, 0, out enabled, 0) || enabled);
            }
        }
        internal static ThemeTransition Prepare(Form form)
        {
            if (!form.Visible || form.WindowState == FormWindowState.Minimized || form.ClientSize.Width < 1 || form.ClientSize.Height < 1) return null;
            ThemeTransition previous = form.Controls.OfType<ThemeTransition>().FirstOrDefault();
            ulong key = ThemeSnapshotCache.Key(form);
            Bitmap image = previous?.CurrentImage();
            bool cached = false;
            if (image == null) { image = ThemeSnapshotCache.Get(form, key, AppTheme.IsDark); cached = image != null; }
            if (image == null)
            {
                AppTheme.PrepareSurface(form); key = ThemeSnapshotCache.Key(form);
                image = CaptureRoots(form); ThemeSnapshotCache.Store(form, key, AppTheme.IsDark, image);
            }
            previous?.Finish(false);
            var layer = new ThemeTransition(form, image) { BeforeCached = cached };
            try { form.Controls.Add(layer); layer.BringToFront(); layer.Refresh(); return layer; }
            catch { layer.Finish(); throw; }
        }
        private ThemeTransition(Form form, Bitmap image)
        {
            this.form = form; before = image;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque, true);
            Dock = DockStyle.Fill; TabStop = false; AccessibleName = "Changing appearance";
            timer.Tick += (s, e) => { if (Position >= 1 || ClientSize != before.Size) Finish(); else Invalidate(); };
            form.ResizeBegin += Resizing;
        }
        internal void Start()
        {
            if (IsDisposed) return;
            try
            {
                AppTheme.PrepareSurface(form);
                ulong key = ThemeSnapshotCache.Key(form);
                after = ThemeSnapshotCache.Get(form, key, AppTheme.IsDark); AfterCached = after != null;
                if (after == null) { after = CaptureRoots(form); ThemeSnapshotCache.Store(form, key, AppTheme.IsDark, after); }
                wipe = new ThemeWipeBuffer(before, after); clock.Start(); timer.Start(); Invalidate();
            }
            catch { Finish(); throw; }
        }
        private void Resizing(object sender, EventArgs e) => Finish();
        internal void Finish(bool reveal = true)
        {
            if (finished) return; finished = true; timer.Stop();
            form.ResizeBegin -= Resizing;
            if (Parent != null) Parent.Controls.Remove(this);
            Dispose();
            if (reveal && !form.IsDisposed && !form.Disposing) { form.Invalidate(true); form.Update(); }
        }
        private static Bitmap CaptureRoots(Form form)
        {
            var image = new Bitmap(form.ClientSize.Width, form.ClientSize.Height, PixelFormat.Format24bppRgb);
            try
            {
                using (var graphics = Graphics.FromImage(image)) graphics.Clear(AppTheme.Canvas);
                // The overlay is a sibling of the application's content roots.
                // Printing the roots excludes it without ever hiding it on screen.
                foreach (Control root in form.Controls.Cast<Control>().Reverse().ToArray())
                    if (root.Visible && !(root is ThemeTransition) && root.Width > 0 && root.Height > 0)
                        root.DrawToBitmap(image, root.Bounds);
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
            graphics.DrawImageUnscaled(before, 0, 0);
            if (after == null || position <= 0) return;
            if (position >= 1) { graphics.DrawImageUnscaled(after, 0, 0); return; }
            wipe.Draw(graphics, position);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { timer.Dispose(); form.ResizeBegin -= Resizing; wipe?.Dispose(); wipe = null; before?.Dispose(); after?.Dispose(); before = after = null; }
            base.Dispose(disposing);
        }
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SystemParametersInfo(uint action, uint parameter, [MarshalAs(UnmanagedType.Bool)] out bool value, uint flags);
    }
}
