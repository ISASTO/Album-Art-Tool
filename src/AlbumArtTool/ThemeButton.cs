using System.Drawing;
using System.Windows.Forms;

namespace AlbumArtTool
{
    internal sealed class ThemeButton : Button
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Enabled ? BackColor : MainForm.Surface);
            using (var border = new Pen(Enabled ? Color.FromArgb(86, 94, 96) : Color.FromArgb(50, 54, 57)))
                e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
            var bounds = Rectangle.Inflate(ClientRectangle, -5, -2);
            TextRenderer.DrawText(e.Graphics, Text, Font, bounds, Enabled ? ForeColor : Color.FromArgb(110, 117, 122),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -4, -4), ForeColor, BackColor);
        }
    }
}
