using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace AlbumArtTool.Design
{
    // WinForms simulates transparency by repainting parents, often many times
    // per frame. Cache each complete opaque material at its actual pixel size;
    // children can copy their small part without resampling or rebuilding it.
    internal sealed class SurfaceCache : IDisposable
    {
        private Bitmap image;
        private ulong key;
        internal void Paint(Control control, PaintEventArgs e, Action<PaintEventArgs> render, int variant = 0)
        {
            if (control.ClientSize.Width < 1 || control.ClientSize.Height < 1) return;
            ulong next = Key(control, variant);
            if (image == null || key != next)
            {
                var replacement = new Bitmap(control.ClientSize.Width, control.ClientSize.Height, PixelFormat.Format24bppRgb);
                try
                {
                    using (PaintTrace.Measure("Generate " + control.GetType().Name))
                    using (var graphics = Graphics.FromImage(replacement))
                    using (var paint = new PaintEventArgs(graphics, control.ClientRectangle)) render(paint);
                }
                catch { replacement.Dispose(); throw; }
                image?.Dispose(); image = replacement; key = next;
            }
            var clip = Rectangle.Intersect(control.ClientRectangle, Rectangle.Ceiling(e.Graphics.VisibleClipBounds));
            clip.Intersect(e.ClipRectangle);
            if (clip.Width > 0 && clip.Height > 0) using (PaintTrace.Measure("Copy backdrop")) e.Graphics.DrawImage(image, clip, clip, GraphicsUnit.Pixel);
        }
        private static ulong Key(Control control, int variant)
        {
            ulong value = AppTheme.IsDark ? 14695981039346656037UL : 1099511628211UL;
            Add(ref value, variant); Add(ref value, control.DeviceDpi);
            for (Control current = control; current != null; current = current.Parent)
            {
                Add(ref value, current.ClientSize.Width); Add(ref value, current.ClientSize.Height); Add(ref value, current.BackColor.ToArgb());
                // A window's desktop position does not change its material.
                if (current is Form) break;
                Add(ref value, current.Left); Add(ref value, current.Top);
                if (current is FrostPanel frost) Add(ref value, frost.CornerRadius);
            }
            return value;
        }
        private static void Add(ref ulong value, int number) { unchecked { value = (value ^ (uint)number) * 1099511628211UL; } }
        public void Dispose() { image?.Dispose(); image = null; }
    }
}
