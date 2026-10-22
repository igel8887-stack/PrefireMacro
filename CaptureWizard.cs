using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PrefireMacro
{
    public class PixelSample
    {
        public int X, Y, Rgb;
        public PixelSample() { }
        public PixelSample(int x, int y, int rgb) { X = x; Y = y; Rgb = rgb; }
    }

    internal sealed class CaptureFrame : Form
    {
        Point dragStart, formStart;
        public CaptureFrame(Rectangle area)
        {
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
            BackColor = Color.Magenta; TransparencyKey = Color.Magenta;
            Bounds = Rectangle.Inflate(area, 5, 5); Cursor = Cursors.SizeAll;
            MouseDown += delegate(object sender, MouseEventArgs e)
            { if (e.Button == MouseButtons.Left) { dragStart = Cursor.Position; formStart = Location; Capture = true; } };
            MouseMove += delegate { if (Capture) { Point p = Cursor.Position; Location = new Point(formStart.X + p.X - dragStart.X, formStart.Y + p.Y - dragStart.Y); } };
            MouseUp += delegate { Capture = false; };
        }
        public Rectangle CaptureArea { get { return new Rectangle(Left + 5, Top + 5, Width - 10, Height - 10); } }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override void OnPaint(PaintEventArgs e)
        { base.OnPaint(e); using (Pen pen = new Pen(Color.FromArgb(220, 45, 55), 5)) e.Graphics.DrawRectangle(pen, 2, 2, Width - 5, Height - 5); }
    }

    internal sealed class RegionCapture : Form
    {
        readonly CaptureFrame frame;
        readonly Timer timer = new Timer();
        readonly Label info = new Label();
        readonly Button next = new Button(), cancel = new Button();
        int countdown = 3, stage;
        public Bitmap Screenshot { get; private set; }
        public Rectangle Area { get; private set; }
        public RegionCapture(Rectangle initial, int number)
        {
            Text = "Farbe " + number + " · 1/2 Bereich aufnehmen";
            AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(470, 190); FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false; TopMost = true; StartPosition = FormStartPosition.Manual;
            Font = new Font("Segoe UI", 10); KeyPreview = true;
            frame = new CaptureFrame(initial);
            Rectangle screen = Screen.FromRectangle(initial).WorkingArea;
            Location = new Point(Math.Max(screen.Left, Math.Min(initial.Left - 160, screen.Right - Width)), Math.Max(screen.Top, Math.Min(initial.Bottom + 26, screen.Bottom - Height)));
            info.Text = "Das rote Quadrat an den Rändern über den Lebensbalken ziehen.\nPfeiltasten verschieben es pixelweise. Weiter nimmt den Bereich nach 3 Sekunden auf – Zeit, zu Fortnite zu wechseln.";
            info.SetBounds(18, 16, 433, 100); Controls.Add(info);
            next.Text = "Weiter – Screenshot"; next.SetBounds(18, 133, 255, 37); Controls.Add(next);
            cancel.Text = "Abbrechen"; cancel.SetBounds(294, 133, 155, 37); cancel.DialogResult = DialogResult.Cancel; Controls.Add(cancel); CancelButton = cancel;
            next.Click += delegate { BeginCapture(); };
            timer.Tick += delegate
            {
                if (stage == 1)
                {
                    countdown--;
                    if (countdown > 0) { info.Text = "Aufnahme in " + countdown + " Sekunden …\nJetzt bei Bedarf mit Alt+Tab zu Fortnite wechseln."; return; }
                    Area = frame.CaptureArea; frame.Hide(); Opacity = 0; stage = 2; timer.Interval = 180; return;
                }
                timer.Stop();
                try
                {
                    Screenshot = new Bitmap(Area.Width, Area.Height);
                    using (Graphics graphics = Graphics.FromImage(Screenshot)) graphics.CopyFromScreen(Area.Location, Point.Empty, Area.Size);
                    DialogResult = DialogResult.OK; Close();
                }
                catch (Exception ex)
                {
                    if (Screenshot != null) { Screenshot.Dispose(); Screenshot = null; }
                    MessageBox.Show("Aufnahme fehlgeschlagen: " + ex.Message, "Screenshot"); DialogResult = DialogResult.Cancel; Close();
                }
            };
            KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (stage != 0) return;
                int step = e.Shift ? 10 : 1;
                Point p = frame.Location;
                if (e.KeyCode == Keys.Left) p.X -= step; else if (e.KeyCode == Keys.Right) p.X += step;
                else if (e.KeyCode == Keys.Up) p.Y -= step; else if (e.KeyCode == Keys.Down) p.Y += step; else return;
                frame.Location = p; e.Handled = e.SuppressKeyPress = true;
            };
            Shown += delegate { frame.Show(); Activate(); };
            FormClosed += delegate { timer.Stop(); timer.Dispose(); frame.Close(); frame.Dispose(); };
            DarkTheme.Apply(this); DarkTheme.Button(next, true);
        }
        internal void BeginCapture()
        {
            if (stage != 0) return;
            next.Enabled = false; stage = 1; info.Text = "Aufnahme in 3 Sekunden …\nJetzt bei Bedarf mit Alt+Tab zu Fortnite wechseln."; timer.Interval = 1000; timer.Start();
        }
    }

    internal sealed class PixelCanvas : Control
    {
        public readonly Bitmap Image;
        public readonly HashSet<Point> Selected = new HashSet<Point>();
        public Point HoverPoint;
        public int Zoom = 4;
        public event Action SelectionChanged;
        public PixelCanvas(Bitmap image)
        {
            Image = image; DoubleBuffered = true; TabStop = true; Cursor = Cursors.Cross;
            Size = new Size(image.Width * Zoom, image.Height * Zoom);
            AccessibleName = "Screenshot-Pixelauswahl";
        }
        public static Point Map(Point display, int zoom, Size image)
        { return new Point(Math.Max(0, Math.Min(image.Width - 1, display.X / zoom)), Math.Max(0, Math.Min(image.Height - 1, display.Y / zoom))); }
        public void Toggle(Point p)
        {
            if (Selected.Contains(p)) Selected.Remove(p);
            else if (Selected.Count < 32) Selected.Add(p);
            HoverPoint = p; Invalidate(); if (SelectionChanged != null) SelectionChanged();
        }
        protected override void OnMouseMove(MouseEventArgs e)
        { base.OnMouseMove(e); HoverPoint = Map(e.Location, Zoom, Image.Size); Invalidate(); if (SelectionChanged != null) SelectionChanged(); }
        protected override void OnMouseDown(MouseEventArgs e)
        { base.OnMouseDown(e); Focus(); if (e.Button == MouseButtons.Left) Toggle(Map(e.Location, Zoom, Image.Size)); }
        protected override bool IsInputKey(Keys keyData)
        { Keys k = keyData & Keys.KeyCode; return k == Keys.Left || k == Keys.Right || k == Keys.Up || k == Keys.Down || base.IsInputKey(keyData); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            Point p = HoverPoint;
            if (e.KeyCode == Keys.Left) p.X--; else if (e.KeyCode == Keys.Right) p.X++;
            else if (e.KeyCode == Keys.Up) p.Y--; else if (e.KeyCode == Keys.Down) p.Y++;
            else if (e.KeyCode == Keys.Space) { Toggle(p); e.Handled = true; return; }
            else { base.OnKeyDown(e); return; }
            HoverPoint = new Point(Math.Max(0, Math.Min(Image.Width - 1, p.X)), Math.Max(0, Math.Min(Image.Height - 1, p.Y)));
            Invalidate(); if (SelectionChanged != null) SelectionChanged(); e.Handled = true;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
            e.Graphics.DrawImage(Image, new Rectangle(0, 0, Width, Height), 0, 0, Image.Width, Image.Height, GraphicsUnit.Pixel);
            foreach (Point p in Selected)
            {
                Rectangle r = new Rectangle(p.X * Zoom, p.Y * Zoom, Zoom, Zoom);
                e.Graphics.DrawRectangle(Pens.Black, r); r.Inflate(-1, -1); e.Graphics.DrawRectangle(Pens.White, r);
            }
            Rectangle hover = new Rectangle(HoverPoint.X * Zoom - 2, HoverPoint.Y * Zoom - 2, Zoom + 4, Zoom + 4);
            e.Graphics.DrawRectangle(Pens.Red, hover);
        }
    }

    internal sealed class PixelPicker : Form
    {
        public readonly PixelCanvas Canvas;
        readonly Rectangle area;
        readonly Label details = new Label(), swatch = new Label();
        readonly Button accept = new Button();
        public List<PixelSample> Result { get; private set; }
        public PixelPicker(Bitmap screenshot, Rectangle captureArea, int number)
        {
            area = captureArea; Text = "Farbe " + number + " · 2/2 Pixel auswählen";
            Font = new Font("Segoe UI", 10); ClientSize = new Size(800, 650); MinimumSize = new Size(650, 510);
            AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterScreen; MaximizeBox = false;
            TableLayoutPanel layout = new TableLayoutPanel(); layout.Dock = DockStyle.Fill; layout.Padding = new Padding(18);
            layout.ColumnCount = 2; layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
            layout.RowCount = 3; layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 66)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58)); Controls.Add(layout);
            Label title = new Label(); title.Text = "Farbige Pixel des Lebensbalkens anklicken (max. 32).\nDie Balkenposition wird später automatisch im Spiel gesucht."; title.Dock = DockStyle.Fill; layout.Controls.Add(title, 0, 0); layout.SetColumnSpan(title, 2);
            Panel imagePanel = new Panel(); imagePanel.AutoScroll = true; imagePanel.Dock = DockStyle.Fill; imagePanel.BackColor = Color.FromArgb(36, 40, 46); layout.Controls.Add(imagePanel, 0, 1);
            Canvas = new PixelCanvas(screenshot); imagePanel.Controls.Add(Canvas); imagePanel.AutoScrollMinSize = Canvas.Size;
            FlowLayoutPanel side = new FlowLayoutPanel(); side.FlowDirection = FlowDirection.TopDown; side.WrapContents = false; side.Dock = DockStyle.Fill; side.Padding = new Padding(14, 0, 0, 0); layout.Controls.Add(side, 1, 1);
            details.Size = new Size(196, 125); side.Controls.Add(details);
            swatch.Size = new Size(180, 58); swatch.TextAlign = ContentAlignment.MiddleCenter; swatch.BorderStyle = BorderStyle.FixedSingle; side.Controls.Add(swatch);
            Button clear = new Button(); clear.Text = "Auswahl leeren"; clear.Size = new Size(184, 36); side.Controls.Add(clear);
            clear.Click += delegate { Canvas.Selected.Clear(); Canvas.Invalidate(); RefreshDetails(); };
            Button again = new Button(); again.Text = "Neu aufnehmen"; again.Size = new Size(184, 36); again.DialogResult = DialogResult.Retry; side.Controls.Add(again);
            Label hint = new Label(); hint.Text = "Gleichmäßige grüne / orange Pixel wählen.\n\nPfeiltasten: Pixel bewegen\nLeertaste: auswählen\nEsc: abbrechen"; hint.Size = new Size(190, 154); side.Controls.Add(hint);
            FlowLayoutPanel actions = new FlowLayoutPanel(); actions.FlowDirection = FlowDirection.RightToLeft; actions.Dock = DockStyle.Fill; actions.Padding = new Padding(0, 12, 0, 0); layout.Controls.Add(actions, 0, 2); layout.SetColumnSpan(actions, 2);
            accept.Text = "Farbe übernehmen"; accept.Size = new Size(185, 36); accept.Enabled = false; actions.Controls.Add(accept);
            Button cancel = new Button(); cancel.Text = "Abbrechen"; cancel.Size = new Size(135, 36); cancel.DialogResult = DialogResult.Cancel; actions.Controls.Add(cancel); CancelButton = cancel;
            accept.Click += delegate { CommitSelection(); };
            Canvas.SelectionChanged += RefreshDetails; RefreshDetails();
            DarkTheme.Apply(this); DarkTheme.Button(accept, true); RefreshDetails();
        }
        internal void CommitSelection()
        {
            if (Canvas.Selected.Count == 0) return;
            Result = new List<PixelSample>();
            foreach (Point p in Canvas.Selected) Result.Add(new PixelSample(area.X + p.X, area.Y + p.Y, Canvas.Image.GetPixel(p.X, p.Y).ToArgb() & 0xFFFFFF));
            DialogResult = DialogResult.OK; Close();
        }
        void RefreshDetails()
        {
            Point p = Canvas.HoverPoint; Color color = Canvas.Image.GetPixel(p.X, p.Y);
            details.Text = "Ausgewählt: " + Canvas.Selected.Count + " / 32 Farben\n\nAufnahme X: " + (area.X + p.X) + "\nAufnahme Y: " + (area.Y + p.Y) + "\nRGB: " + color.R + ", " + color.G + ", " + color.B;
            swatch.BackColor = color; swatch.ForeColor = color.GetBrightness() < .45 ? Color.White : Color.Black; swatch.Text = "#" + (color.ToArgb() & 0xFFFFFF).ToString("X6");
            accept.Enabled = Canvas.Selected.Count > 0;
        }
    }
}
