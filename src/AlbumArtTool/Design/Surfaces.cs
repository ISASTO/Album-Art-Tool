using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AlbumArtTool.Design
{
    internal sealed class FrostCanvas : TableLayoutPanel
    {
        internal FrostCanvas() { DoubleBuffered = true; BackColor = AppTheme.Canvas; }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (Width < 1 || Height < 1) return;
            using (var fill = new LinearGradientBrush(ClientRectangle, Color.FromArgb(235, 241, 252), Color.FromArgb(246, 244, 250), 35)) e.Graphics.FillRectangle(fill, ClientRectangle);
            Glow(e.Graphics, new Rectangle(-Width / 4, -Height / 3, Width, Height), Color.FromArgb(30, 151, 187, 250));
            Glow(e.Graphics, new Rectangle(Width / 2, 0, Width, Height), Color.FromArgb(22, 181, 166, 238));
        }
        private static void Glow(Graphics g, Rectangle bounds, Color color)
        {
            using (var ellipse = new GraphicsPath())
            {
                ellipse.AddEllipse(bounds);
                using (var brush = new PathGradientBrush(ellipse) { CenterColor = color, SurroundColors = new[] { Color.FromArgb(0, color) } }) g.FillPath(brush, ellipse);
            }
        }
    }
    internal class FrostPanel : Panel
    {
        internal int CornerRadius { get; set; } = 18;
        internal FrostPanel() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true); BackColor = Color.Transparent; Padding = new Padding(10); }
        protected override void OnPaintBackground(PaintEventArgs e) { base.OnPaintBackground(e); AppTheme.Frost(e.Graphics, ClientRectangle, AppTheme.Scale(this, CornerRadius)); }
    }
    internal sealed class InputFrame : Panel
    {
        internal InputFrame(TextBox input)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent; Padding = new Padding(9, 4, 9, 4);
        }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            base.OnPaintBackground(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = AppTheme.Rounded(new RectangleF(.5f, .5f, Math.Max(1, Width - 1), Math.Max(1, Height - 1)), AppTheme.Scale(this, 8)))
            using (var fill = new SolidBrush(AppTheme.Input))
            using (var pen = new Pen(ContainsFocus ? AppTheme.Accent : AppTheme.Border, ContainsFocus ? 1.4f : 1f))
            { e.Graphics.FillPath(fill, path); e.Graphics.DrawPath(pen, path); }
        }
    }
    internal sealed class BrandTitle : Control
    {
        private readonly Font title = new Font("Segoe UI", 19f, FontStyle.Bold);
        internal BrandTitle() { DoubleBuffered = true; SetStyle(ControlStyles.SupportsTransparentBackColor, true); BackColor = Color.Transparent; AccessibleName = "Music Library Manager"; }
        protected override void OnPaint(PaintEventArgs e)
        {
            int size = AppTheme.Scale(this, 34), top = (Height - size) / 2;
            var icon = new Rectangle(0, top, size, size);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = AppTheme.Rounded(icon, size * .28f))
            using (var fill = new LinearGradientBrush(icon, Color.FromArgb(100, 143, 255), Color.FromArgb(78, 82, 215), 55)) e.Graphics.FillPath(fill, path);
            float unit = size / 34f;
            using (var note = new Pen(Color.White, 2.6f * unit) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                e.Graphics.DrawLine(note, 14 * unit, top + 23 * unit, 14 * unit, top + 11 * unit);
                e.Graphics.DrawLine(note, 14 * unit, top + 11 * unit, 25 * unit, top + 8 * unit);
                e.Graphics.DrawLine(note, 25 * unit, top + 8 * unit, 25 * unit, top + 20 * unit);
            }
            using (var fill = new SolidBrush(Color.White)) { e.Graphics.FillEllipse(fill, 8 * unit, top + 20 * unit, 7 * unit, 5 * unit); e.Graphics.FillEllipse(fill, 19 * unit, top + 17 * unit, 7 * unit, 5 * unit); }
            var text = new Rectangle(icon.Right + AppTheme.Scale(this, 12), 0, Math.Max(1, Width - icon.Right - AppTheme.Scale(this, 12)), Height);
            TextRenderer.DrawText(e.Graphics, "Music Library Manager", title, text, AppTheme.Ink, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
        protected override void Dispose(bool disposing) { if (disposing) title.Dispose(); base.Dispose(disposing); }
    }
    internal sealed class ThemeProgressBar : ProgressBar
    {
        private readonly Timer animation = new Timer { Interval = 30 };
        private float phase;
        internal ThemeProgressBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            animation.Tick += (s, e) => { phase = (phase + .018f) % 1.4f; Invalidate(); };
        }
        protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); if (!Visible) animation.Stop(); Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent?.BackColor.A == 255 ? Parent.BackColor : AppTheme.Canvas);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            int height = Math.Min(AppTheme.Scale(this, 7), Height); var bounds = new RectangleF(0, (Height - height) / 2f, Width, height);
            if (Width < 1 || height < 1) return;
            using (var path = AppTheme.Rounded(bounds, height / 2f))
            {
                using (var fill = new SolidBrush(Color.FromArgb(221, 228, 243))) e.Graphics.FillPath(fill, path);
                var saved = e.Graphics.Save(); e.Graphics.SetClip(path);
                bool marquee = Style == ProgressBarStyle.Marquee;
                if (marquee && Visible) animation.Start(); else animation.Stop();
                float width = marquee ? Width * .3f : Width * (Value - Minimum) / (float)Math.Max(1, Maximum - Minimum);
                float left = marquee ? (phase - .3f) * Width : 0;
                if (width > 0)
                    using (var fill = new LinearGradientBrush(new PointF(0, 0), new PointF(Math.Max(1, Width), 0), AppTheme.Accent, Color.FromArgb(118, 154, 255)))
                    using (var segment = AppTheme.Rounded(new RectangleF(left, bounds.Top, width, height), height / 2f)) e.Graphics.FillPath(fill, segment);
                e.Graphics.Restore(saved);
            }
        }
        protected override void Dispose(bool disposing) { if (disposing) animation.Dispose(); base.Dispose(disposing); }
    }
}
