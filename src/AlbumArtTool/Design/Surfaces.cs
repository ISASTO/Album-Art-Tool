using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AlbumArtTool.Design
{
    internal sealed class FrostCanvas : TableLayoutPanel
    {
        private readonly SurfaceCache material = new SurfaceCache();
        internal FrostCanvas() { DoubleBuffered = true; BackColor = AppTheme.Canvas; }
        protected override void OnPaintBackground(PaintEventArgs e) => material.Paint(this, e, PaintMaterial);
        private void PaintMaterial(PaintEventArgs e) => CanvasMaterial.Draw(e.Graphics, ClientRectangle, AppTheme.IsDark);
        protected override void Dispose(bool disposing) { if (disposing) material.Dispose(); base.Dispose(disposing); }
    }

    internal class FrostPanel : Panel
    {
        private readonly SurfaceCache material = new SurfaceCache();
        internal int CornerRadius { get; set; } = 18;
        internal FrostPanel() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true); BackColor = Color.Transparent; Padding = new Padding(10); }
        protected override void OnPaintBackground(PaintEventArgs e) => material.Paint(this, e, PaintMaterial, CornerRadius);
        private void PaintMaterial(PaintEventArgs e) { base.OnPaintBackground(e); AppTheme.Frost(e.Graphics, ClientRectangle, AppTheme.Scale(this, CornerRadius)); }
        protected override void Dispose(bool disposing) { if (disposing) material.Dispose(); base.Dispose(disposing); }
    }
    // Buffer each native list locally; compositing the whole details subtree can
    // leave owner-drawn rows stale until the mouse leaves a child window.
    internal sealed class DetailPanel : FrostPanel { }
    internal sealed class BufferedTableLayoutPanel : TableLayoutPanel
    {
        internal BufferedTableLayoutPanel() { DoubleBuffered = true; ResizeRedraw = true; }
    }
    internal sealed class InsetPanel : Panel
    {
        private readonly SurfaceCache material = new SurfaceCache();
        internal InsetPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent; Padding = new Padding(6); Margin = Padding.Empty; Dock = DockStyle.Fill;
        }
        protected override void OnPaintBackground(PaintEventArgs e) => material.Paint(this, e, PaintMaterial);
        private void PaintMaterial(PaintEventArgs e)
        {
            base.OnPaintBackground(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var shape = AppTheme.Rounded(new RectangleF(.5f, .5f, Math.Max(1, Width - 1), Math.Max(1, Height - 1)), AppTheme.Scale(this, 12)))
            using (var fill = new SolidBrush(AppTheme.Inset))
            using (var edge = new Pen(AppTheme.Blend(AppTheme.Border, AppTheme.Inset, .55f)))
            { e.Graphics.FillPath(fill, shape); e.Graphics.DrawPath(edge, shape); }
        }
        protected override void Dispose(bool disposing) { if (disposing) material.Dispose(); base.Dispose(disposing); }
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
            AppTheme.DrawBrandMark(e.Graphics, icon);
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
            if (Parent != null)
            {
                var saved = e.Graphics.Save(); e.Graphics.TranslateTransform(-Left, -Top);
                using (var background = new PaintEventArgs(e.Graphics, new Rectangle(Left, Top, Width, Height))) InvokePaintBackground(Parent, background);
                e.Graphics.Restore(saved);
            }
            else e.Graphics.Clear(AppTheme.Canvas);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            int height = Math.Min(AppTheme.Scale(this, 7), Height); var bounds = new RectangleF(0, (Height - height) / 2f, Width, height);
            if (Width < 1 || height < 1) return;
            using (var path = AppTheme.Rounded(bounds, height / 2f))
            {
                using (var fill = new SolidBrush(AppTheme.IsDark ? AppTheme.Border : Color.FromArgb(221, 228, 243))) e.Graphics.FillPath(fill, path);
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
