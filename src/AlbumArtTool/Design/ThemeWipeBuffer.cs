using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace AlbumArtTool.Design
{
    // The old renderer invoked GDI+ once for every gradient column, processing an
    // image/color-matrix operation each time. Blend only the narrow moving band
    // in one reusable buffer, then present the completed frame with one draw.
    internal sealed class ThemeWipeBuffer : IDisposable
    {
        private readonly int width, height;
        private readonly int[] before, after, pixels;
        private readonly Bitmap frame;
        internal ThemeWipeBuffer(Bitmap before, Bitmap after)
        {
            width = before.Width; height = before.Height;
            if (after.Size != before.Size) throw new ArgumentException("Theme frame sizes must match.");
            this.before = ReadPixels(before); this.after = ReadPixels(after); pixels = new int[width * height];
            frame = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        }
        internal void Draw(Graphics graphics, double position)
        {
            double eased = position * position * (3 - 2 * position);
            int band = Math.Max(100, width / 5);
            double edge = -band + eased * (width + 2 * band);
            int left = Math.Max(0, Math.Min(width, (int)Math.Floor(edge)));
            int right = Math.Max(left, Math.Min(width, (int)Math.Ceiling(edge + band)));
            var weights = new uint[right - left];
            for (int x = left; x < right; x++)
            {
                double t = Math.Max(0, Math.Min(1, (x + .5 - edge) / band));
                weights[x - left] = (uint)Math.Round((1 - t * t * (3 - 2 * t)) * 256);
            }
            for (int y = 0, row = 0; y < height; y++, row += width)
            {
                Array.Copy(after, row, pixels, row, left);
                Array.Copy(before, row + right, pixels, row + right, width - right);
                for (int x = left; x < right; x++)
                {
                    uint a = weights[x - left], b = 256 - a, old = (uint)before[row + x], next = (uint)after[row + x];
                    uint rb = (((old & 0x00ff00ffU) * b + (next & 0x00ff00ffU) * a) >> 8) & 0x00ff00ffU;
                    uint g = (((old & 0x0000ff00U) * b + (next & 0x0000ff00U) * a) >> 8) & 0x0000ff00U;
                    pixels[row + x] = unchecked((int)(0xff000000U | rb | g));
                }
            }
            var data = frame.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                if (data.Stride == width * 4) Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
                else for (int y = 0; y < height; y++) Marshal.Copy(pixels, y * width, IntPtr.Add(data.Scan0, y * data.Stride), width);
            }
            finally { frame.UnlockBits(data); }
            graphics.DrawImageUnscaled(frame, 0, 0);
        }
        private static int[] ReadPixels(Bitmap source)
        {
            using (var converted = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb))
            {
                using (var graphics = Graphics.FromImage(converted)) graphics.DrawImageUnscaled(source, 0, 0);
                var pixels = new int[source.Width * source.Height];
                var data = converted.LockBits(new Rectangle(Point.Empty, converted.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    if (data.Stride == source.Width * 4) Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
                    else for (int y = 0; y < source.Height; y++) Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), pixels, y * source.Width, source.Width);
                }
                finally { converted.UnlockBits(data); }
                return pixels;
            }
        }
        public void Dispose() => frame.Dispose();
    }
}
