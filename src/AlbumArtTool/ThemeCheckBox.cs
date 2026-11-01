using System;
using System.Drawing;
using System.Windows.Forms;
using AlbumArtTool.Design;

namespace AlbumArtTool
{
    internal sealed class ThemeCheckBox : CheckBox
    {
        internal ThemeCheckBox() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true); BackColor = Color.Transparent; }
        protected override void OnPaint(PaintEventArgs e)
        {
            // ButtonBase can suppress the normal background pass. Clear the reused
            // buffered surface explicitly before drawing our glyph and label.
            base.OnPaintBackground(e);
            int size = Math.Max(13, (int)Math.Round(14.0 * DeviceDpi / 96)), gap = AppTheme.Scale(this, 7);
            var box = new Rectangle(Padding.Left + 1, (Height - size) / 2, size, size);
            AppTheme.DrawCheck(e.Graphics, box, Checked, Enabled);
            var text = new Rectangle(box.Right + gap, 0, Math.Max(0, Width - box.Right - gap - Padding.Right), Height);
            TextRenderer.DrawText(e.Graphics, Text, Font, text, Enabled ? AppTheme.Ink : AppTheme.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(text, -1, -2), AppTheme.Accent, AppTheme.Surface);
        }
    }
}
