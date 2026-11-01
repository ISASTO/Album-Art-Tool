using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.CompilerServices;
using System.Windows.Forms;

namespace AlbumArtTool.Design
{
    internal static class AppTheme
    {
        private static ThemePalette palette = new ThemePalette(ThemeSettings.ReadDark());
        internal static bool IsDark => palette.Dark;
        internal static Color Canvas => palette.Canvas;
        internal static Color Surface => palette.Surface;
        internal static Color Input => palette.Input;
        internal static Color Inset => palette.Inset;
        internal static Color Ink => palette.Ink;
        internal static Color Muted => palette.Muted;
        internal static Color Accent => palette.Accent;
        internal static Color Selection => palette.Selection;
        internal static Color Border => palette.Border;
        internal static Color Positive => palette.Positive;
        internal static Color Warning => palette.Warning;
        internal static Color Primary => IsDark ? Color.FromArgb(50, 102, 214) : Accent;
        internal static Color Alternate => IsDark ? Color.FromArgb(35, 42, 56) : Color.FromArgb(249, 251, 254);
        internal static Color SelectedBorder => IsDark ? Color.FromArgb(67, 96, 148) : Color.FromArgb(211, 223, 251);
        private sealed class WindowStyle { internal Action Updated; }
        private static readonly ConditionalWeakTable<Form, WindowStyle> windows = new ConditionalWeakTable<Form, WindowStyle>();
        private static readonly List<WeakReference<Form>> openWindows = new List<WeakReference<Form>>();
        private sealed class Applied { }
        private static readonly ConditionalWeakTable<Control, Applied> applied = new ConditionalWeakTable<Control, Applied>();
        private static readonly Bitmap grain = MakeGrain();
        private static readonly Icon appIcon = LoadIcon();
        private static readonly Bitmap brandMark = appIcon.ToBitmap();
        private static Icon LoadIcon()
        {
            using (var stream = typeof(AppTheme).Assembly.GetManifestResourceStream("AlbumArtTool.AppIcon.ico"))
            using (var icon = new Icon(stream, 256, 256)) return (Icon)icon.Clone();
        }
        internal static void DrawBrandMark(Graphics graphics, Rectangle rectangle)
        { graphics.InterpolationMode = InterpolationMode.HighQualityBicubic; graphics.DrawImage(brandMark, rectangle); }
        internal static bool SetDark(bool dark, bool persist = true)
        {
            if (dark != IsDark)
            {
                var previous = palette; palette = new ThemePalette(dark);
                var colors = new Dictionary<int, Color>(); var before = previous.Colors; var after = palette.Colors;
                for (int i = 0; i < before.Length; i++) colors[before[i].ToArgb()] = after[i];
                for (int i = openWindows.Count - 1; i >= 0; i--)
                {
                    if (!openWindows[i].TryGetTarget(out var form) || form.IsDisposed) { openWindows.RemoveAt(i); continue; }
                    Recolor(form, colors); foreach (Control control in Snapshot(form)) Recolor(control, colors);
                    if (form.IsHandleCreated) NativeEffects.Apply(form);
                    windows.GetValue(form, _ => new WindowStyle()).Updated?.Invoke();
                    form.Invalidate(true);
                }
            }
            return !persist || ThemeSettings.SaveDark(dark);
        }
        private static void Recolor(Control control, Dictionary<int, Color> colors)
        {
            if (colors.TryGetValue(control.ForeColor.ToArgb(), out var ink)) control.ForeColor = ink;
            if (colors.TryGetValue(control.BackColor.ToArgb(), out var fill)) control.BackColor = fill;
            if (control is LinkLabel link) { link.LinkColor = Accent; link.ActiveLinkColor = Ink; link.VisitedLinkColor = Accent; }
            if (control is TreeView tree) tree.LineColor = Border;
            if (control is ListView list && !list.VirtualMode)
            {
                list.BeginUpdate();
                foreach (ListViewItem item in list.Items)
                    if (colors.TryGetValue(item.ForeColor.ToArgb(), out var color)) item.ForeColor = color;
                list.EndUpdate();
            }
        }
        internal static int Scale(Control c, int n) => Math.Max(1, (int)Math.Round(n * c.DeviceDpi / 96.0));
        internal static GraphicsPath Rounded(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            radius = Math.Max(0, Math.Min(radius, Math.Min(r.Width, r.Height) / 2));
            if (radius < 1) { path.AddRectangle(r); return path; }
            float d = radius * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90); path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); path.AddArc(r.X, r.Bottom - d, d, d, 90, 90); path.CloseFigure(); return path;
        }
        internal static Color Blend(Color a, Color b, float amount) => Color.FromArgb((int)(a.R + (b.R - a.R) * amount), (int)(a.G + (b.G - a.G) * amount), (int)(a.B + (b.B - a.B) * amount));
        internal static void ClipRound(Control c, int radius)
        {
            if (c.Width < 2 || c.Height < 2) return;
            using (var shape = Rounded(c.ClientRectangle, Scale(c, radius)))
            { var old = c.Region; c.Region = new Region(shape); old?.Dispose(); }
        }
        internal static void Frost(Graphics g, Rectangle rectangle, int radius, bool shadow = true)
        {
            if (rectangle.Width < 2 || rectangle.Height < 2) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var body = new RectangleF(rectangle.X + .5f, rectangle.Y + .5f, rectangle.Width - 1, rectangle.Height - (shadow ? 3 : 1));
            if (shadow)
                using (var outline = Rounded(new RectangleF(body.X, body.Y + 2, body.Width, body.Height), radius))
                using (var brush = new SolidBrush(Color.FromArgb(13, 37, 55, 93))) g.FillPath(brush, outline);
            using (var outline = Rounded(body, radius))
            using (var fill = new LinearGradientBrush(body, IsDark ? Color.FromArgb(38, 46, 61) : Color.FromArgb(254, 255, 255), IsDark ? Surface : Color.FromArgb(247, 249, 253), 90))
            using (var edge = new Pen(Border))
            {
                g.FillPath(fill, outline);
                var saved = g.Save(); g.SetClip(outline);
                using (var texture = new TextureBrush(grain)) g.FillRectangle(texture, rectangle);
                g.Restore(saved); g.DrawPath(edge, outline);
            }
        }
        private static Bitmap MakeGrain()
        {
            var bitmap = new Bitmap(64, 64); var random = new Random(418);
            for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++) bitmap.SetPixel(x, y, Color.FromArgb(random.Next(0, 5), 67, 84, 115));
            return bitmap;
        }
        internal static void Apply(Form form, Action updated = null)
        {
            form.BackColor = Canvas; form.ForeColor = Ink; form.Icon = appIcon;
            if (!windows.TryGetValue(form, out var style))
            {
                style = new WindowStyle(); windows.Add(form, style); openWindows.Add(new WeakReference<Form>(form));
                form.HandleCreated += (s, e) => NativeEffects.Apply(form);
            }
            style.Updated = updated;
            if (form.IsHandleCreated) NativeEffects.Apply(form);
            ApplyTree(form);
            updated?.Invoke();
        }
        internal static void ApplyTree(Control root)
        {
            foreach (Control c in Snapshot(root))
            {
                if (applied.TryGetValue(c, out _)) continue; applied.Add(c, new Applied());
                if (c is TextBox text) WrapInput(text);
                else if (c is ComboBox combo) StyleCombo(combo);
                else if (c is ListView list && !(list is AlbumList)) StyleList(list);
                else if (c is FlowLayoutPanel flow) { flow.BackColor = Color.Transparent; flow.SizeChanged += (s, e) => ClipRound(flow, 12); ClipRound(flow, 12); }
                else if (c is TreeView tree) { tree.BackColor = Surface; tree.ForeColor = Ink; tree.LineColor = Border; tree.BorderStyle = BorderStyle.None; tree.ItemHeight = Scale(tree, 27); }
                else if (c is ListBox box) StyleQueue(box);
                else if (c is LinkLabel link) { link.LinkColor = Accent; link.ActiveLinkColor = Ink; link.VisitedLinkColor = Accent; link.BackColor = Color.Transparent; }
                else if (c is Label) c.BackColor = Color.Transparent;
                else if (c is PictureBox picture)
                {
                    picture.BackColor = Input; picture.SizeChanged += (s, e) => ClipRound(picture, 10); ClipRound(picture, 10);
                }
                else if (c is TableLayoutPanel || c.GetType() == typeof(Panel)) c.BackColor = Color.Transparent;
                if (c is ListView || c is TreeView || c is ListBox || c is FlowLayoutPanel || c is TextBox multiline && multiline.Multiline) ThemeScrollbars.Apply(c);
            }
        }
        private static Control[] Snapshot(Control root)
        {
            var list = new System.Collections.Generic.List<Control>();
            foreach (Control c in root.Controls) { list.Add(c); list.AddRange(Snapshot(c)); }
            return list.ToArray();
        }
        private static void WrapInput(TextBox input)
        {
            if (input.Parent is InputFrame) return;
            var parent = input.Parent; if (parent == null) return;
            var frame = new InputFrame(input) { Dock = input.Dock, Margin = input.Margin, Bounds = input.Bounds, Anchor = input.Anchor, TabIndex = input.TabIndex };
            if (parent is TableLayoutPanel table)
            {
                var cell = table.GetCellPosition(input); int rows = table.GetRowSpan(input), columns = table.GetColumnSpan(input);
                table.Controls.Remove(input); table.Controls.Add(frame, cell.Column, cell.Row); table.SetRowSpan(frame, rows); table.SetColumnSpan(frame, columns);
            }
            else { int index = parent.Controls.GetChildIndex(input); parent.Controls.Remove(input); parent.Controls.Add(frame); parent.Controls.SetChildIndex(frame, index); }
            input.BorderStyle = BorderStyle.None; input.AutoSize = false; input.Dock = DockStyle.Fill; input.Margin = Padding.Empty;
            input.BackColor = Input; input.ForeColor = Ink; frame.Controls.Add(input);
            input.Enter += (s, e) => frame.Invalidate(); input.Leave += (s, e) => frame.Invalidate();
        }
        private static void StyleCombo(ComboBox combo)
        {
            combo.BackColor = Surface; combo.ForeColor = Ink; combo.FlatStyle = FlatStyle.Flat; combo.DrawMode = DrawMode.OwnerDrawFixed;
            combo.ItemHeight = Math.Max(Scale(combo, 20), combo.Font.Height + 4);
            combo.DrawItem += (s, e) =>
            {
                if (e.Index < 0) return;
                bool selected = (e.State & DrawItemState.Selected) != 0;
                using (var brush = new SolidBrush(selected ? Selection : Surface)) e.Graphics.FillRectangle(brush, e.Bounds);
                var text = Rectangle.Inflate(e.Bounds, -7, 0);
                TextRenderer.DrawText(e.Graphics, combo.Items[e.Index].ToString(), combo.Font, text, combo.Enabled ? Ink : Muted,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            };
            combo.DpiChangedAfterParent += (s, e) => combo.ItemHeight = Math.Max(Scale(combo, 20), combo.Font.Height + 4);
        }
        private static void StyleList(ListView list)
        {
            list.BackColor = list.Parent is InsetPanel ? Inset : Surface; list.ForeColor = Ink; list.BorderStyle = BorderStyle.None; list.GridLines = false;
            var rows = new ImageList { ImageSize = new Size(1, Scale(list, 30)), ColorDepth = ColorDepth.Depth32Bit };
            rows.Images.Add(new Bitmap(1, Scale(list, 30))); list.SmallImageList = rows;
            list.Disposed += (s, e) => rows.Dispose();
            list.DpiChangedAfterParent += (s, e) => rows.ImageSize = new Size(1, Math.Min(256, Scale(list, 30)));
            list.OwnerDraw = true;
            list.DrawColumnHeader += (s, e) =>
            {
                using (var brush = new SolidBrush(list.Parent is InsetPanel ? Blend(Inset, Ink, IsDark ? .025f : .02f) : Input)) e.Graphics.FillRectangle(brush, e.Bounds);
                var text = e.Bounds; text.X += 10; text.Width = Math.Max(1, text.Width - 16);
                TextRenderer.DrawText(e.Graphics, e.Header.Text, list.Font, text, Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                using (var pen = new Pen(Border)) e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            };
            list.DrawItem += (s, e) => { if (list.View != View.Details) e.DrawDefault = true; };
            list.DrawSubItem += (s, e) =>
            {
                var alternate = list.Parent is InsetPanel ? Blend(list.BackColor, Ink, IsDark ? .025f : .015f) : Alternate;
                using (var brush = new SolidBrush(e.Item.Selected ? Selection : e.ItemIndex % 2 == 0 ? list.BackColor : alternate)) e.Graphics.FillRectangle(brush, e.Bounds);
                var text = e.Bounds; text.X += 10; text.Width = Math.Max(1, text.Width - 16);
                if (e.ColumnIndex == 0 && list.CheckBoxes)
                { DrawCheck(e.Graphics, new Rectangle(e.Bounds.Left + Scale(list, 4), e.Bounds.Top + (e.Bounds.Height - Scale(list, 14)) / 2, Scale(list, 14), Scale(list, 14)), e.Item.Checked, list.Enabled); text.X += Scale(list, 18); text.Width = Math.Max(1, text.Width - Scale(list, 18)); }
                Color color = e.Item.ForeColor == Muted || !list.Enabled ? Muted : Ink;
                TextRenderer.DrawText(e.Graphics, e.SubItem.Text, list.Font, text, color, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            };
        }
        private static void StyleQueue(ListBox box)
        {
            box.BackColor = Surface; box.ForeColor = Ink; box.BorderStyle = BorderStyle.None; box.DrawMode = DrawMode.OwnerDrawFixed; box.ItemHeight = Scale(box, 36);
            box.DrawItem += (s, e) =>
            {
                if (e.Index < 0) return;
                using (var fill = new SolidBrush((e.State & DrawItemState.Selected) != 0 ? Selection : Surface)) e.Graphics.FillRectangle(fill, e.Bounds);
                var bounds = Rectangle.Inflate(e.Bounds, -12, 0);
                TextRenderer.DrawText(e.Graphics, box.Items[e.Index].ToString(), box.Font, bounds, Ink, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            };
        }
        internal static void DrawCheck(Graphics g, Rectangle box, bool value, bool enabled)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var shape = Rounded(box, Math.Max(3, box.Width / 4)))
            using (var fill = new SolidBrush(value ? enabled ? Primary : Blend(Primary, Surface, .55f) : Input))
            using (var border = new Pen(value ? Primary : IsDark ? Border : Color.FromArgb(175, 187, 207))) { g.FillPath(fill, shape); g.DrawPath(border, shape); }
            if (value)
                using (var pen = new Pen(Color.White, Math.Max(1.5f, box.Width / 7f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                    g.DrawLines(pen, new[] { new PointF(box.Left + box.Width * .22f, box.Top + box.Height * .51f), new PointF(box.Left + box.Width * .43f, box.Top + box.Height * .72f), new PointF(box.Left + box.Width * .8f, box.Top + box.Height * .28f) });
        }
    }
}
