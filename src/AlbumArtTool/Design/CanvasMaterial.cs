using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace AlbumArtTool.Design
{
    // Transparent WinForms children ask their ancestors to paint again. Generate
    // the expensive, smooth canvas material once per palette, never per child or
    // resize step. The small shared images have a constant ~2.3 MB memory cost.
    internal static class CanvasMaterial
    {
        private static readonly Lazy<Bitmap> dark = new Lazy<Bitmap>(() => Create(true));
        private static readonly Lazy<Bitmap> light = new Lazy<Bitmap>(() => Create(false));
        internal static void Draw(Graphics graphics, Rectangle bounds, bool isDark)
        {
            if (bounds.Width < 1 || bounds.Height < 1) return;
            var clip = RectangleF.Intersect(graphics.VisibleClipBounds, bounds);
            if (clip.Width <= 0 || clip.Height <= 0) return;
            var image = (isDark ? dark : light).Value;
            var source = new RectangleF((clip.X - bounds.X) * image.Width / bounds.Width, (clip.Y - bounds.Y) * image.Height / bounds.Height,
                clip.Width * image.Width / bounds.Width, clip.Height * image.Height / bounds.Height);
            var saved = graphics.Save();
            graphics.InterpolationMode = InterpolationMode.Bilinear; graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.DrawImage(image, clip, source, GraphicsUnit.Pixel); graphics.Restore(saved);
        }
        private static Bitmap Create(bool isDark)
        {
            var image = new Bitmap(768, 512, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(image))
            {
                using (var fill = new LinearGradientBrush(new Rectangle(Point.Empty, image.Size), isDark ? Color.FromArgb(23, 31, 45) : Color.FromArgb(235, 241, 252),
                    isDark ? Color.FromArgb(23, 24, 34) : Color.FromArgb(246, 244, 250), 35)) graphics.FillRectangle(fill, 0, 0, image.Width, image.Height);
                Glow(graphics, new Rectangle(-image.Width / 4, -image.Height / 3, image.Width, image.Height), Color.FromArgb(isDark ? 12 : 30, 151, 187, 250));
                Glow(graphics, new Rectangle(image.Width / 2, 0, image.Width, image.Height), Color.FromArgb(isDark ? 9 : 22, 181, 166, 238));
            }
            return image;
        }
        private static void Glow(Graphics graphics, Rectangle bounds, Color color)
        {
            using (var ellipse = new GraphicsPath())
            {
                ellipse.AddEllipse(bounds);
                using (var brush = new PathGradientBrush(ellipse) { CenterColor = color, SurroundColors = new[] { Color.FromArgb(0, color) } }) graphics.FillPath(brush, ellipse);
            }
        }
    }
}
