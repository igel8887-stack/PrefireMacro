using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PrefireMacro
{
    internal static class DarkTheme
    {
        internal static readonly bool HighContrast = SystemInformation.HighContrast;
        internal static readonly Color Window = HighContrast ? SystemColors.Window : Color.FromArgb(16, 20, 25), Surface = HighContrast ? SystemColors.Window : Color.FromArgb(27, 34, 43), Field = HighContrast ? SystemColors.Control : Color.FromArgb(41, 52, 64);
        internal static readonly Color Text = HighContrast ? SystemColors.WindowText : Color.FromArgb(239, 244, 250), Muted = HighContrast ? SystemColors.WindowText : Color.FromArgb(179, 191, 205), Accent = HighContrast ? SystemColors.Highlight : Color.FromArgb(158, 201, 255), Outline = HighContrast ? SystemColors.WindowText : Color.FromArgb(116, 134, 154);
        internal static readonly Color PrimaryText = HighContrast ? SystemColors.HighlightText : Window;
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
        internal static void WindowChrome(Form form)
        {
            if (HighContrast) return;
            try { int dark = 1; DwmSetWindowAttribute(form.Handle, 20, ref dark, 4); } catch (DllNotFoundException) { } catch (EntryPointNotFoundException) { }
        }
        internal static void Button(Button button, bool primary = false)
        {
            button.FlatStyle = FlatStyle.Flat; button.UseVisualStyleBackColor = false; button.UseMnemonic = false;
            button.BackColor = primary ? Accent : Field; button.ForeColor = primary ? PrimaryText : Text;
            button.FlatAppearance.BorderColor = primary ? Accent : Outline;
            button.FlatAppearance.MouseOverBackColor = HighContrast ? button.BackColor : primary ? Color.FromArgb(181, 205, 245) : Color.FromArgb(52, 66, 87);
            button.FlatAppearance.MouseDownBackColor = HighContrast ? button.BackColor : Color.FromArgb(99, 129, 173); button.Cursor = Cursors.Hand;
        }
        internal static void Apply(Control root)
        {
            if (root.ForeColor != Muted) root.ForeColor = Text;
            if (root is Form) { root.BackColor = Window; ((Form)root).Shown += delegate { WindowChrome((Form)root); }; }
            if (root is Button) Button((Button)root);
            if (root is CheckBox) { CheckBox c = (CheckBox)root; c.FlatStyle = FlatStyle.Flat; c.UseVisualStyleBackColor = false; }
            if (root is NumericUpDown) { root.BackColor = Field; ((NumericUpDown)root).BorderStyle = root is RoundedNumber ? BorderStyle.None : BorderStyle.FixedSingle; }
            ComboBox combo = root as ComboBox;
            if (combo != null)
            {
                combo.BackColor = Field; combo.FlatStyle = FlatStyle.Flat; combo.DrawMode = DrawMode.OwnerDrawFixed; combo.ItemHeight = Math.Max(26, combo.Font.Height + 8);
                combo.FontChanged += delegate { combo.ItemHeight = Math.Max(26, combo.Font.Height + 8); combo.Invalidate(); };
                combo.DrawItem += delegate(object sender, DrawItemEventArgs e)
                {
                    if (e.Index < 0) return;
                    bool selected = (e.State & DrawItemState.Selected) != 0;
                    Color background = selected ? HighContrast ? Accent : Color.FromArgb(52, 66, 87) : Field;
                    using (Brush brush = new SolidBrush(background)) e.Graphics.FillRectangle(brush, e.Bounds);
                    TextRenderer.DrawText(e.Graphics, combo.Items[e.Index].ToString(), e.Font, Rectangle.Inflate(e.Bounds, -7, 0), selected && HighContrast ? PrimaryText : combo.Enabled ? Text : Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                    e.DrawFocusRectangle();
                };
            }
            foreach (Control child in root.Controls) Apply(child);
        }
    }
    internal static class RoundSurface
    {
        internal static GraphicsPath Path(RectangleF box, float radius)
        {
            float d = Math.Max(1, Math.Min(radius * 2, Math.Min(box.Width, box.Height))); GraphicsPath p = new GraphicsPath();
            p.AddArc(box.Left, box.Top, d, d, 180, 90); p.AddArc(box.Right - d, box.Top, d, d, 270, 90);
            p.AddArc(box.Right - d, box.Bottom - d, d, d, 0, 90); p.AddArc(box.Left, box.Bottom - d, d, d, 90, 90); p.CloseFigure(); return p;
        }
        internal static float Radius(Control c) { return Math.Min(c.Height / 2f, 12f * c.Font.Height / 18f); }
        internal static void Clip(Control c)
        {
            if (c.Width < 3 || c.Height < 3) return;
            using (GraphicsPath p = Path(new RectangleF(0, 0, c.Width, c.Height), Radius(c)))
            { Region old = c.Region; c.Region = new Region(p); if (old != null) old.Dispose(); }
        }
        internal static void Paint(Control c, Graphics g, bool hover, bool pressed, bool focused, int border)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color fill = c.BackColor;
            if (!DarkTheme.HighContrast && c.Enabled && (hover || pressed))
                fill = ControlPaint.Light(fill, pressed ? .08f : .16f);
            using (GraphicsPath path = Path(new RectangleF(.75f, .75f, c.Width - 1.5f, c.Height - 1.5f), Radius(c)))
            {
                using (Brush b = new SolidBrush(fill)) g.FillPath(b, path);
                if (border > 0 || focused) using (Pen p = new Pen(focused ? DarkTheme.Accent : c is Button ? ((Button)c).FlatAppearance.BorderColor : DarkTheme.Outline, focused ? 2 : 1)) g.DrawPath(p, path);
            }
        }
    }
    internal class RoundedButton : Button
    {
        bool hovered, pressed;
        internal RoundedButton() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); }
        protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); RoundSurface.Clip(this); }
        protected override void OnMouseEnter(EventArgs e) { hovered = true; base.OnMouseEnter(e); Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { hovered = pressed = false; base.OnMouseLeave(e); Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { pressed = e.Button == MouseButtons.Left; base.OnMouseDown(e); Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; base.OnMouseUp(e); Invalidate(); }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space) pressed = true; base.OnKeyDown(e); Invalidate(); }
        protected override void OnKeyUp(KeyEventArgs e) { pressed = false; base.OnKeyUp(e); Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            RoundSurface.Paint(this, e.Graphics, hovered, pressed, Focused && ShowFocusCues, FlatAppearance.BorderSize);
            Rectangle text = new Rectangle(Padding.Left + 5, Padding.Top, Math.Max(1, Width - Padding.Horizontal - 10), Height - Padding.Vertical);
            TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            flags |= TextAlign == ContentAlignment.MiddleLeft ? TextFormatFlags.Left : TextAlign == ContentAlignment.MiddleRight ? TextFormatFlags.Right : TextFormatFlags.HorizontalCenter;
            if (Text == "ⓘ") TextRenderer.DrawText(e.Graphics, "i", Font, ClientRectangle, Enabled ? ForeColor : DarkTheme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            else TextRenderer.DrawText(e.Graphics, Text, Font, text, Enabled ? ForeColor : DarkTheme.Muted, flags);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -6, -6), ForeColor, BackColor);
        }
    }
    internal sealed class RoundedToggle : CheckBox
    {
        bool hover, press;
        protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); if (Appearance == Appearance.Button) RoundSurface.Clip(this); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; base.OnMouseEnter(e); Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { hover = press = false; base.OnMouseLeave(e); Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { press = true; base.OnMouseDown(e); Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { press = false; base.OnMouseUp(e); Invalidate(); }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space) press = true; base.OnKeyDown(e); Invalidate(); }
        protected override void OnKeyUp(KeyEventArgs e) { press = false; base.OnKeyUp(e); Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            if (Appearance != Appearance.Button) { base.OnPaint(e); return; }
            RoundSurface.Paint(this, e.Graphics, hover, press, Focused && ShowFocusCues, 1);
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Enabled ? ForeColor : DarkTheme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }
    internal sealed class RoundedField : Panel
    {
        internal RoundedField(Control child)
        {
            DoubleBuffered = true; Size = child is ComboBox ? new Size(child.Width + 20, 46) : new Size(132, 42);
            child.Dock = DockStyle.None; child.Region = null; Controls.Add(child);
            child.Enter += delegate { Invalidate(); }; child.Leave += delegate { Invalidate(); }; child.EnabledChanged += delegate { Invalidate(); };
        }
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e); if (Controls.Count == 0) return;
            Control child = Controls[0]; child.Width = Math.Max(20, Width - 20); child.Location = new Point(10, Math.Max(2, (Height - child.Height) / 2));
        }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent == null ? DarkTheme.Surface : Parent.BackColor); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = RoundSurface.Path(new RectangleF(.5f, .5f, Width - 1, Height - 1), 10f * Font.Height / 18f))
            { using (Brush b = new SolidBrush(DarkTheme.Field)) e.Graphics.FillPath(b, path); using (Pen p = new Pen(ContainsFocus ? DarkTheme.Accent : DarkTheme.Outline)) e.Graphics.DrawPath(p, path); }
        }
    }
    internal sealed class RoundedNumber : NumericUpDown
    {
        internal RoundedNumber() { BorderStyle = BorderStyle.None; }
        protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = RoundSurface.Path(new RectangleF(.5f, .5f, Width - 1, Height - 1), RoundSurface.Radius(this)))
            using (Pen pen = new Pen(ContainsFocus ? DarkTheme.Accent : DarkTheme.Outline)) e.Graphics.DrawPath(pen, path);
        }
    }
    internal sealed class AdaptivePair : TableLayoutPanel
    {
        readonly Control first, second;
        bool? wide;
        internal AdaptivePair(Control left, Control right)
        {
            first = left; second = right; AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; Dock = DockStyle.Fill; Margin = new Padding(0, 0, 0, 12);
            ColumnCount = 2; RowCount = 1; first.Dock = second.Dock = DockStyle.Fill;
            Controls.Add(first, 0, 0); Controls.Add(second, 1, 0);
        }
        protected override void OnLayout(LayoutEventArgs e)
        {
            bool nextWide = Width >= 780f * Font.SizeInPoints / 12f;
            if (Controls.Count == 2 && Width > 0 && wide != nextWide)
            {
                wide = nextWide; SuspendLayout(); ColumnStyles.Clear(); RowStyles.Clear();
                if (wide.Value)
                {
                    ColumnCount = 2; RowCount = 1; ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); RowStyles.Add(new RowStyle(SizeType.AutoSize));
                    SetCellPosition(second, new TableLayoutPanelCellPosition(1, 0)); first.Margin = new Padding(0, 0, 8, 0); second.Margin = new Padding(8, 0, 0, 0);
                }
                else
                {
                    SetCellPosition(second, new TableLayoutPanelCellPosition(0, 1)); ColumnCount = 1; RowCount = 2; ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); RowStyles.Add(new RowStyle(SizeType.AutoSize)); RowStyles.Add(new RowStyle(SizeType.AutoSize));
                    first.Margin = new Padding(0, 0, 0, 12); second.Margin = Padding.Empty;
                }
                ResumeLayout(false);
            }
            base.OnLayout(e);
        }
    }
    internal sealed class RoundedCard : TableLayoutPanel
    {
        internal RoundedCard() { DoubleBuffered = true; }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (Padding.All == 0 || Width < 4 || Height < 4) { base.OnPaintBackground(e); return; }
            e.Graphics.Clear(Parent == null ? DarkTheme.Window : Parent.BackColor); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = RoundSurface.Path(new RectangleF(0, 0, Width, Height), 20f * Font.Height / 18f))
            using (Brush brush = new SolidBrush(BackColor)) e.Graphics.FillPath(brush, path);
        }
        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (Width < 4 || Height < 4 || Padding.All == 0) return;
            int r = (int)Math.Min(32f * Font.Height / 18f, Math.Min(Width, Height) / 2);
            using (GraphicsPath path = new GraphicsPath())
            {
                path.AddArc(0, 0, r, r, 180, 90); path.AddArc(Width - r, 0, r, r, 270, 90); path.AddArc(Width - r, Height - r, r, r, 0, 90); path.AddArc(0, Height - r, r, r, 90, 90); path.CloseFigure();
                Region old = Region; Region = new Region(path); if (old != null) old.Dispose();
            }
        }
    }
    internal sealed class DarkComboBox : ComboBox
    {
        protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); RoundSurface.Clip(this); }
        internal DarkComboBox() { SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(DarkTheme.Field);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            if (!(Parent is RoundedField)) using (GraphicsPath path = RoundSurface.Path(new RectangleF(.5f, .5f, Width - 1, Height - 1), RoundSurface.Radius(this)))
            using (Pen pen = new Pen(Focused ? DarkTheme.Accent : DarkTheme.Outline, Focused ? 2 : 1)) e.Graphics.DrawPath(pen, path);
            TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(8, 0, Width - 38, Height), Enabled ? DarkTheme.Text : DarkTheme.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            using (Brush brush = new SolidBrush(DarkTheme.Muted)) e.Graphics.FillPolygon(brush, new Point[] { new Point(Width - 23, Height / 2 - 2), new Point(Width - 13, Height / 2 - 2), new Point(Width - 18, Height / 2 + 3) });
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, new Rectangle(3, 3, Width - 7, Height - 7), DarkTheme.Text, DarkTheme.Field);
        }
        protected override void OnSelectedIndexChanged(EventArgs e) { base.OnSelectedIndexChanged(e); Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    }
    internal sealed class ColorSampleButton : RoundedButton
    {
        internal Color? Sample;
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Rectangle box = new Rectangle(14, (Height - 22) / 2, 22, 22);
            using (Brush brush = new SolidBrush(Sample ?? DarkTheme.Field)) e.Graphics.FillRectangle(brush, box);
            using (Pen pen = new Pen(DarkTheme.Outline)) e.Graphics.DrawRectangle(pen, box);
        }
    }
    internal sealed class HelpWindow : Form
    {
        internal HelpWindow(string title, string body)
        {
            Text = title; Font = new Font("Segoe UI", 12); ClientSize = new Size(440, 365); MinimumSize = new Size(400, 300);
            FormBorderStyle = FormBorderStyle.SizableToolWindow; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
            TableLayoutPanel grid = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22), ColumnCount = 1, RowCount = 3 };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); grid.RowStyles.Add(new RowStyle(SizeType.AutoSize)); grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 45)); Controls.Add(grid);
            Label heading = new Label { Text = title, AutoSize = true, Dock = DockStyle.Fill, Font = new Font("Segoe UI", 16, FontStyle.Bold), Margin = new Padding(0, 0, 0, 16) }; grid.Controls.Add(heading);
            TextBox text = new TextBox { Text = body.Replace("\n", Environment.NewLine), Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = DarkTheme.Window, TabStop = true, AccessibleName = title + " – Erklärung" }; grid.Controls.Add(text);
            Button close = new RoundedButton { Text = "Schließen", Dock = DockStyle.Right, Width = 125, DialogResult = DialogResult.Cancel }; grid.Controls.Add(close); close.Click += delegate { Close(); }; CancelButton = close;
            DarkTheme.Apply(this); Shown += delegate { close.Focus(); };
        }
    }
}
