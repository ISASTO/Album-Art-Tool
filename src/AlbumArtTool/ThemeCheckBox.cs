using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AlbumArtTool
{
    internal sealed class ThemeCheckBox : CheckBox
    {
        // Native disabled checkbox text is drawn nearly black on a dark background.
        protected override void OnPaint(PaintEventArgs e)
        {
            var background = BackColor.A == 0 ? Parent?.BackColor ?? MainForm.Surface : BackColor;
            e.Graphics.Clear(background);
            int size = Math.Max(12, (int)Math.Round(13.0 * DeviceDpi / 96));
            int gap = Math.Max(5, (int)Math.Round(6.0 * DeviceDpi / 96));
            var box = new Rectangle(Padding.Left, (Height - size) / 2, size, size);
            using (var fill = new SolidBrush(Checked ? (Enabled ? MainForm.Accent : Color.FromArgb(73, 81, 79)) : MainForm.Surface))
                e.Graphics.FillRectangle(fill, box);
            using (var pen = new Pen(Enabled && Checked ? MainForm.Accent : MainForm.Muted))
                e.Graphics.DrawRectangle(pen, box);
            if (Checked)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var pen = new Pen(Enabled ? MainForm.Surface : MainForm.Ink, Math.Max(1.5f, size / 7f)))
                    e.Graphics.DrawLines(pen, new[] {
                        new PointF(box.Left + size * .2f, box.Top + size * .5f),
                        new PointF(box.Left + size * .43f, box.Top + size * .73f),
                        new PointF(box.Left + size * .83f, box.Top + size * .25f) });
            }
            var text = new Rectangle(box.Right + gap, Padding.Top, Math.Max(0, Width - box.Right - gap - Padding.Right), Height - Padding.Vertical);
            TextRenderer.DrawText(e.Graphics, Text, Font, text, Enabled ? ForeColor : MainForm.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(text, -1, -2), MainForm.Ink, background);
        }
    }
}
