using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AlbumArtTool.Design
{
    // Keep the native selection, popup, keyboard handling and accessibility. Paint the
    // complete closed face so native bevels cannot show through a rounded border.
    internal sealed class ThemeComboBox : ComboBox
    {
        private bool hovered;
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = false; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnSelectedIndexChanged(EventArgs e) { base.OnSelectedIndexChanged(e); Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
        protected override void OnDropDown(EventArgs e) { base.OnDropDown(e); Invalidate(); }
        protected override void OnDropDownClosed(EventArgs e) { base.OnDropDownClosed(e); Invalidate(); }
        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if ((e.State & DrawItemState.ComboBoxEdit) == 0) base.OnDrawItem(e);
        }
        protected override void WndProc(ref Message m)
        {
            if (DropDownStyle == ComboBoxStyle.DropDownList && (m.Msg == 0x0317 || m.Msg == 0x0318) && m.WParam != IntPtr.Zero)
            {
                using (var graphics = Graphics.FromHdc(m.WParam)) PaintFace(graphics);
                m.Result = IntPtr.Zero; return;
            }
            base.WndProc(ref m);
            if (IsDisposed || !IsHandleCreated || DropDownStyle != ComboBoxStyle.DropDownList) return;
            if (m.Msg == 0x000F) { using (var graphics = CreateGraphics()) PaintFace(graphics); }
        }
        private void PaintFace(Graphics target)
        {
            if (Width < 4 || Height < 4) return;
            using (var bitmap = new Bitmap(Width, Height))
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(AppTheme.Surface);
                if (Parent != null)
                {
                    var saved = graphics.Save(); graphics.TranslateTransform(-Left, -Top);
                    using (var background = new PaintEventArgs(graphics, new Rectangle(Left, Top, Width, Height))) InvokePaintBackground(Parent, background);
                    graphics.Restore(saved);
                }
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                bool focus = Enabled && (Focused || DroppedDown);
                using (var shape = AppTheme.Rounded(new RectangleF(.75f, .75f, Width - 1.5f, Height - 1.5f), AppTheme.Scale(this, 7)))
                using (var fill = new SolidBrush(Enabled && hovered ? AppTheme.Blend(AppTheme.Input, AppTheme.Selection, .45f) : AppTheme.Input))
                using (var border = new Pen(focus ? AppTheme.Accent : AppTheme.Border, focus ? 1.4f : 1f))
                { graphics.FillPath(fill, shape); graphics.DrawPath(border, shape); }
                var bounds = new Rectangle(AppTheme.Scale(this, 10), 0, Math.Max(1, Width - AppTheme.Scale(this, 36)), Height);
                // GDI TextRenderer does not preserve alpha on the offscreen bitmap.
                // Use GDI+ here so switching themes cannot leave opaque/stippled glyphs.
                graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                using (var ink = new SolidBrush(Enabled ? AppTheme.Ink : AppTheme.Muted))
                using (var format = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center,
                    Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                    graphics.DrawString(Text, Font, ink, bounds, format);
                float x = Width - AppTheme.Scale(this, 15), y = Height / 2f, span = AppTheme.Scale(this, 3);
                using (var pen = new Pen(Enabled ? AppTheme.Muted : AppTheme.Border, AppTheme.Scale(this, 1) + .4f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                    graphics.DrawLines(pen, new[] { new PointF(x - span, y - span / 2), new PointF(x, y + span / 2), new PointF(x + span, y - span / 2) });
                target.DrawImageUnscaled(bitmap, 0, 0);
            }
        }
    }
}
