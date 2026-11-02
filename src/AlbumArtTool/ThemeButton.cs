using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using AlbumArtTool.Design;

namespace AlbumArtTool
{
    internal sealed class ThemeButton : Button
    {
        internal bool Primary { get; set; }
        internal bool Selected { get; set; }
        internal bool Chrome { get; set; }
        private float hover;
        private bool over, pressed;
        private readonly Timer animation = new Timer { Interval = 15 };
        internal ThemeButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; UseVisualStyleBackColor = false;
            animation.Tick += (s, e) =>
            {
                float target = over && Enabled ? 1 : 0; hover += (target - hover) * .3f;
                if (Math.Abs(target - hover) < .02f) { hover = target; animation.Stop(); } Invalidate();
            };
        }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); over = true; animation.Start(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); over = pressed = false; animation.Start(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { pressed = true; Invalidate(); } }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); pressed = false; Invalidate(); }
        protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (e.KeyCode == Keys.Space) { pressed = true; Invalidate(); } }
        protected override void OnKeyUp(KeyEventArgs e) { base.OnKeyUp(e); pressed = false; Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); pressed = false; Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); if (!Enabled) { pressed = over = false; hover = 0; animation.Stop(); } Invalidate(); }
        protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); if (!Visible) { animation.Stop(); hover = 0; } }
        protected override void OnPaintBackground(PaintEventArgs pevent) { /* Parent material is painted below, with rounded transparent edges. */ }
        protected override void OnPaint(PaintEventArgs e)
        {
            using (PaintTrace.Measure("Button paint"))
            {
            if (Parent != null)
            {
                var saved = e.Graphics.Save(); e.Graphics.TranslateTransform(-Left, -Top);
                using (var parent = new PaintEventArgs(e.Graphics, new Rectangle(Left, Top, Width, Height))) { InvokePaintBackground(Parent, parent); InvokePaint(Parent, parent); }
                e.Graphics.Restore(saved);
            }
            else e.Graphics.Clear(AppTheme.Canvas);
            if (Width < 4 || Height < 5) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color fill = Primary ? AppTheme.Primary : Selected ? AppTheme.Surface : Chrome ? AppTheme.Input : AppTheme.Surface;
            Color target = Primary ? AppTheme.IsDark ? AppTheme.Blend(AppTheme.Primary, Color.White, .08f) : Color.FromArgb(35, 85, 213) : AppTheme.Selection;
            fill = AppTheme.Blend(fill, target, hover * .7f);
            if (pressed && Enabled) fill = AppTheme.Blend(fill, Primary ? Color.FromArgb(27, 69, 184) : AppTheme.Selection, .7f);
            if (!Enabled) fill = AppTheme.Input;
            int radius = AppTheme.Scale(this, 9);
            var body = new RectangleF(.75f, .75f, Width - 1.5f, Height - 3f);
            if (!Chrome && Enabled)
                using (var shadow = AppTheme.Rounded(new RectangleF(body.X, body.Y + 1.5f, body.Width, body.Height), radius))
                using (var brush = new SolidBrush(Primary ? Color.FromArgb(28, 51, 99, 212) : Color.FromArgb(12, 43, 59, 94))) e.Graphics.FillPath(brush, shadow);
            using (var path = AppTheme.Rounded(body, radius))
            using (var brush = new LinearGradientBrush(body, AppTheme.Blend(fill, Color.White, Primary ? .05f : AppTheme.IsDark ? .025f : .25f), fill, 90))
            using (var border = new Pen(Selected ? AppTheme.SelectedBorder : Primary ? fill : AppTheme.Border))
            { e.Graphics.FillPath(brush, path); if (!Chrome || Selected) e.Graphics.DrawPath(border, path); }
            Color text = !Enabled ? AppTheme.Muted : Primary ? Color.White : Selected ? AppTheme.Accent : AppTheme.Ink;
            var bounds = Rectangle.Inflate(ClientRectangle, -6, -2); if (pressed) bounds.Offset(0, 1);
            TextRenderer.DrawText(e.Graphics, Text, Font, bounds, text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (Focused && ShowFocusCues)
                using (var path = AppTheme.Rounded(new RectangleF(2, 2, Width - 4, Height - 6), Math.Max(1, radius - 1)))
                using (var pen = new Pen(Primary ? Color.White : AppTheme.Accent, 1.5f)) e.Graphics.DrawPath(pen, path);
                    }
        }
        protected override void Dispose(bool disposing) { if (disposing) animation.Dispose(); base.Dispose(disposing); }
    }
}
