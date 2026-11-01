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
        internal const int Duration = 440;
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
            Bitmap image = previous == null ? Capture(form) : previous.CurrentImage();
            previous?.Finish();
            var layer = new ThemeTransition(form, image);
            form.Controls.Add(layer); layer.BringToFront(); layer.Refresh();
            return layer;
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
            try { form.PerformLayout(); after = Capture(form); clock.Start(); timer.Start(); Invalidate(); }
            catch { Finish(); throw; }
        }
        private void Resizing(object sender, EventArgs e) => Finish();
        internal void Finish()
        {
            if (finished) return; finished = true; timer.Stop();
            form.ResizeBegin -= Resizing;
            if (Parent != null) Parent.Controls.Remove(this);
            Dispose();
            if (!form.IsDisposed && !form.Disposing) { form.Invalidate(true); form.Update(); }
        }
        private static Bitmap Capture(Form form)
        {
            var image = new Bitmap(form.ClientSize.Width, form.ClientSize.Height, PixelFormat.Format32bppPArgb);
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
            var image = new Bitmap(before.Width, before.Height, PixelFormat.Format32bppPArgb);
            using (var graphics = Graphics.FromImage(image)) PaintFrame(graphics, Position);
            return image;
        }
        protected override void OnPaint(PaintEventArgs e) => PaintFrame(e.Graphics, Position);
        internal void PaintFrame(Graphics graphics, double position)
        {
            graphics.DrawImageUnscaled(before, 0, 0);
            if (after == null || position <= 0) return;
            if (position >= 1) { graphics.DrawImageUnscaled(after, 0, 0); return; }
            double eased = position * position * (3 - 2 * position);
            int band = Math.Max(100, before.Width / 5);
            double edge = -band + eased * (before.Width + 2 * band);
            int solid = Math.Max(0, Math.Min(before.Width, (int)edge));
            if (solid > 0) graphics.DrawImage(after, new Rectangle(0, 0, solid, before.Height), new Rectangle(0, 0, solid, before.Height), GraphicsUnit.Pixel);
            using (var attributes = new ImageAttributes())
            {
                const int strips = 48;
                for (int n = 0; n < strips; n++)
                {
                    int left = Math.Max(0, (int)Math.Floor(edge + band * n / (double)strips));
                    int right = Math.Min(before.Width, (int)Math.Floor(edge + band * (n + 1) / (double)strips));
                    if (right <= left) continue;
                    double t = (n + .5) / strips;
                    float alpha = (float)(1 - t * t * (3 - 2 * t));
                    var matrix = new ColorMatrix { Matrix33 = alpha };
                    attributes.SetColorMatrix(matrix);
                    var slice = new Rectangle(left, 0, right - left, before.Height);
                    graphics.DrawImage(after, slice, left, 0, slice.Width, slice.Height, GraphicsUnit.Pixel, attributes);
                }
            }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { timer.Dispose(); form.ResizeBegin -= Resizing; before?.Dispose(); after?.Dispose(); before = after = null; }
            base.Dispose(disposing);
        }
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SystemParametersInfo(uint action, uint parameter, [MarshalAs(UnmanagedType.Bool)] out bool value, uint flags);
    }
}
