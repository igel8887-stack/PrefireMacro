using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace PrefireMacro
{
    public sealed partial class MainForm
    {
        HelpWindow helpWindow;
        readonly ToolTip helpTips = new ToolTip { AutoPopDelay = 15000, InitialDelay = 450, ReshowDelay = 150 };
        Panel[] pages;
        Button[] navigation;
        int currentPage;
        void PauseNow() { enabled.Checked = false; StopPending(); feedback.Text = "Pausiert. F8 aktiviert das Tool wieder."; }
        readonly Label pageTitle = new Label(), pageSubtitle = new Label();
        readonly Label previewHint = new Label();
        readonly Label frameTiming = new Label();
        void ClearPreview()
        { Image old = barPreview.Image; barPreview.Image = null; previewHint.Visible = true; frameTiming.Text = "Bildzeiten: noch keine Messung"; frameTiming.AccessibleName = frameTiming.Text; if (old != null) old.Dispose(); }
        static Label Copy(string text, float size = 11.5f, bool bold = false)
        { return new Label { Text = text, AutoSize = true, Dock = DockStyle.Fill, ForeColor = bold ? DarkTheme.Text : DarkTheme.Muted, Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular), Margin = new Padding(0, 0, 0, 12) }; }
        static TableLayoutPanel Stack(int padding)
        {
            TableLayoutPanel panel = new RoundedCard { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, ColumnCount = 1, Padding = new Padding(padding), Margin = new Padding(0, 0, 0, 12), BackColor = DarkTheme.Surface };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); return panel;
        }
        static void Row(TableLayoutPanel stack, Control control)
        { int row = stack.RowCount++; stack.RowStyles.Add(new RowStyle(SizeType.AutoSize)); stack.Controls.Add(control is NumericUpDown ? new RoundedField(control) : control, 0, row); }
        static FlowLayoutPanel Inline(params Control[] controls)
        {
            FlowLayoutPanel row = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0, 0, 0, 10) };
            foreach (Control c in controls) { Control item = c is NumericUpDown || c is ComboBox ? new RoundedField(c) : c; item.Margin = new Padding(0, 0, 14, 8); row.Controls.Add(item); } return row;
        }
        static Button ActionButton(string text, int width, EventHandler click)
        { Button b = new RoundedButton { Text = text, Size = new Size(width, 42) }; b.Click += click; return b; }
        void Explain(Control source, string title, string body)
        {
            if (helpWindow != null && !helpWindow.IsDisposed) helpWindow.Close();
            helpWindow = new HelpWindow(title, body); Rectangle screen = Screen.FromControl(source).WorkingArea;
            Point at = source.PointToScreen(new Point(source.Width + 10, 0));
            helpWindow.Location = new Point(Math.Max(screen.Left, Math.Min(at.X, screen.Right - helpWindow.Width)), Math.Max(screen.Top, Math.Min(at.Y, screen.Bottom - helpWindow.Height)));
            helpWindow.FormClosed += delegate { if (!source.IsDisposed && Visible) source.Focus(); }; helpWindow.Show(this);
        }
        Control Topic(string title, string description)
        {
            TableLayoutPanel row = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Margin = new Padding(0, 0, 0, 10) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 38));
            row.Controls.Add(Copy(title, 13, true), 0, 0);
            Button info = new RoundedButton { Text = "ⓘ", Font = new Font("Segoe UI", 13), Size = new Size(32, 32), AccessibleName = "Information zu " + title, AccessibleDescription = "Öffnet eine Erklärung. Mit Escape schließen.", Margin = Padding.Empty };
            info.Click += delegate { Explain(info, title, description); }; helpTips.SetToolTip(info, "Erklärung zu „" + title + "“ öffnen"); row.Controls.Add(info, 1, 0); return row;
        }
        Control OptionHelp(CheckBox option, string title, string description)
        {
            TableLayoutPanel row = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Margin = new Padding(0, 0, 0, 10) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 38));
            option.Margin = new Padding(0, 4, 0, 0); row.Controls.Add(option, 0, 0);
            Button info = new RoundedButton { Text = "ⓘ", Size = new Size(32, 32), Font = new Font("Segoe UI", 13), AccessibleName = "Information zu " + title, Margin = Padding.Empty };
            info.Click += delegate { Explain(info, title, description); }; helpTips.SetToolTip(info, title + " erklären"); row.Controls.Add(info, 1, 0); return row;
        }
        void Number(NumericUpDown number, int min, int max, int value, string name)
        { number.Minimum = min; number.Maximum = max; number.Value = Math.Max(min, Math.Min(max, value)); number.Width = 100; number.Font = new Font("Segoe UI", 12); number.AccessibleName = name; }
        void Combo(ComboBox combo, int width, string name)
        { combo.DropDownStyle = ComboBoxStyle.DropDownList; combo.Width = width; combo.Font = new Font("Segoe UI", 12); combo.AccessibleName = name; }
        void BuildChoices(ComboBox combo, int selected, string name)
        {
            Combo(combo, 330, name);
            combo.Items.Add(new Choice("Hintere Maustaste (XButton1)", 5)); combo.Items.Add(new Choice("Vordere Maustaste (XButton2)", 6));
            combo.Items.Add(new Choice("Mittlere Maustaste", 4)); combo.Items.Add(new Choice("Rechte Maustaste", 2));
            foreach (char c in "QFECVGRT123456789") combo.Items.Add(new Choice(c.ToString(), c)); SelectKey(combo, selected);
        }
        Panel Page(Panel host)
        {
            Panel page = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(24, 12, 24, 4), BackColor = DarkTheme.Window };
            host.Controls.Add(page); return page;
        }
        TableLayoutPanel PageStack(Panel page)
        { TableLayoutPanel result = Stack(0); result.BackColor = DarkTheme.Window; page.Controls.Add(result); return result; }
        internal void SelectPage(int index)
        {
            currentPage = index;
            string[] titles = { "Einrichten", "Baufolge", "Aufnahmen" };
            string[] subtitles = { "Lebensbalken wählen, Taste festlegen und Erkennung prüfen.", "Schuss, Bautaste und Platzierung klar aufeinander abstimmen.", "Spielabläufe aufnehmen und Fehler gezielt festhalten." };
            pageTitle.Text = titles[index]; pageSubtitle.Text = subtitles[index];
            for (int i = 0; i < pages.Length; i++)
            { pages[i].Visible = i == index; navigation[i].BackColor = i == index ? DarkTheme.Field : DarkTheme.Window; navigation[i].ForeColor = i == index ? DarkTheme.Text : DarkTheme.Muted; navigation[i].FlatAppearance.BorderColor = i == index ? DarkTheme.Accent : DarkTheme.Window; navigation[i].FlatAppearance.BorderSize = i == index ? 1 : 0; navigation[i].AccessibleDescription = i == index ? "Aktueller Bereich" : "Bereich öffnen"; }
        }
        protected override bool ProcessCmdKey(ref Message message, Keys keys)
        {
            if (keys == (Keys.Control | Keys.S)) { Save(); return true; }
            if (keys == Keys.F10) { PauseNow(); return true; }
            if (keys == (Keys.Control | Keys.Tab)) { SelectPage((currentPage + 1) % pages.Length); return true; }
            if (keys == (Keys.Control | Keys.Shift | Keys.Tab)) { SelectPage((currentPage + pages.Length - 1) % pages.Length); return true; }
            return base.ProcessCmdKey(ref message, keys);
        }
        internal void ReviewInterface(string directory)
        {
            Directory.CreateDirectory(directory); TopMost = true; Show(); Application.DoEvents();
            using (StreamWriter report = new StreamWriter(Path.Combine(directory, "layout.txt")))
            {
                report.WriteLine("Preview-only form: no hotkeys, no automatic inputs.");
                for (int page = 0; page < 3; page++)
                {
                    SelectPage(page); Application.DoEvents();
                    using (Bitmap shot = new Bitmap(Width, Height)) { DrawToBitmap(shot, new Rectangle(Point.Empty, Size)); shot.Save(Path.Combine(directory, "page-" + page + ".png")); }
                }
                SelectPage(1); extras.Checked = true; extraFloor.Checked = true; Application.DoEvents();
                if (!stairKey.Enabled || !floorKey.Enabled || !extraDelay.Enabled) throw new Exception("Extra placement options unavailable");
                pages[1].ScrollControlIntoView(extraStair); Application.DoEvents();
                using (Bitmap shot = new Bitmap(Width, Height)) { DrawToBitmap(shot, new Rectangle(Point.Empty, Size)); shot.Save(Path.Combine(directory, "extras-on.png")); }
                extras.Checked = false; if (stairKey.Enabled || floorKey.Enabled || extraDelay.Enabled) throw new Exception("Extra placement settings must disable with option off");
                SelectPage(0); Application.DoEvents();
                int originalKey = hold.SelectedIndex; hold.Focus(); hold.DroppedDown = true; Application.DoEvents();
                if (!hold.DroppedDown) throw new Exception("Key menu did not open");
                hold.SelectedIndex = 1; hold.DroppedDown = false; Application.DoEvents();
                if (((Choice)hold.SelectedItem).Key != 6) throw new Exception("Key selection was not applied"); hold.SelectedIndex = originalKey;
                enabled.Checked = true; if (enabled.Checked || !feedback.Text.Contains("Farbe 1")) throw new Exception("First-run calibration guidance missing");
                Describe(this, report, 0);
                MinimumSize = new Size(640, 480); ClientSize = new Size(734, 570); Application.DoEvents();
                if (ClientSize.Width != 734) throw new Exception("Compact preview width was not applied");
                using (Bitmap shot = new Bitmap(Width, Height)) { DrawToBitmap(shot, new Rectangle(Point.Empty, Size)); shot.Save(Path.Combine(directory, "compact.png")); }
                foreach (Control c in Descendants(this)) if (c is Button && c.Text == "ⓘ")
                {
                    if (String.IsNullOrEmpty(c.AccessibleName) || c.Width < 28 || c.Height < 28 || !c.TabStop) throw new Exception("Info control accessibility");
                }
                SelectPage(1); build.Checked = true; adaptiveBuild.Checked = true; Application.DoEvents();
                editGuard.Checked = true; if (!editGuard.Enabled) throw new Exception("Edit guard unavailable with building enabled");
                build.Checked = false; if (editGuard.Enabled) throw new Exception("Edit guard must be disabled with building off"); build.Checked = true;
                if (!TimingControlsValid || delay.Enabled || placeDelay.Enabled) throw new Exception("Adaptive UI state");
                adaptiveBuild.Checked = false; if (!TimingControlsValid || !delay.Enabled || !placeDelay.Enabled) throw new Exception("Fixed UI state");
                Button pauseControl = null; foreach (Control c in Descendants(this)) if (c is Button && c.AccessibleName == "Sofort pausieren (F10)") pauseControl = (Button)c;
                if (pauseControl == null || !pauseControl.Visible || pauseControl.Width < 28) throw new Exception("Persistent pause control missing");
                pauseControl.PerformClick(); if (enabled.Checked || !feedback.Text.StartsWith("Pausiert")) throw new Exception("Pause control failed");
                Message keyboard = new Message(); SelectPage(0); ProcessCmdKey(ref keyboard, Keys.Control | Keys.Shift | Keys.Tab); if (currentPage != 2) throw new Exception("Reverse tab navigation failed");
                ProcessCmdKey(ref keyboard, Keys.Control | Keys.Tab); if (currentPage != 0) throw new Exception("Forward tab navigation failed");
                SelectPage(1);
                Button editInfo = null; foreach (Control c in Descendants(pages[1])) if (c is Button && c.AccessibleName == "Information zu Edit-Schutz") editInfo = (Button)c;
                if (editInfo == null) throw new Exception("Missing edit guard explanation"); editInfo.PerformClick(); Application.DoEvents();
                if (helpWindow == null || !helpWindow.Visible || helpWindow.Text != "Edit-Schutz") throw new Exception("Edit explanation did not open"); ((Button)helpWindow.CancelButton).PerformClick();
                SelectPage(0); Button filters = null; foreach (Control c in Descendants(pages[0])) if (c is Button && c.Text == "Erweiterte Filter anzeigen") filters = (Button)c;
                if (filters == null) throw new Exception("Missing filter disclosure"); filters.PerformClick(); Application.DoEvents();
                if (!tolerance.Visible || !lossDelay.Visible) throw new Exception("Filter controls remained hidden"); filters.PerformClick();
                SelectPage(0); Button firstInfo = null;
                foreach (Control c in Descendants(pages[0])) if (c is Button && c.Text == "ⓘ") { firstInfo = (Button)c; break; }
                if (firstInfo == null) throw new Exception("Missing contextual information"); firstInfo.PerformClick(); Application.DoEvents();
                if (helpWindow == null || !helpWindow.Visible) throw new Exception("Info button did not open help");
                using (Bitmap shot = new Bitmap(helpWindow.Width, helpWindow.Height)) { helpWindow.DrawToBitmap(shot, new Rectangle(Point.Empty, helpWindow.Size)); shot.Save(Path.Combine(directory, "help.png")); }
                ((Button)helpWindow.CancelButton).PerformClick(); Application.DoEvents(); if (OwnedForms.Length != 0) throw new Exception("Help did not close");
                ClientSize = new Size(850, 850);
                System.Collections.Generic.Dictionary<Control, Font> fonts = new System.Collections.Generic.Dictionary<Control, Font>();
                fonts[this] = Font; foreach (Control c in Descendants(this)) fonts[c] = c.Font;
                SuspendLayout(); Scale(new SizeF(1.5f, 1.5f)); foreach (System.Collections.Generic.KeyValuePair<Control, Font> item in fonts) item.Key.Font = new Font(item.Value.FontFamily, item.Value.Size * 1.5f, item.Value.Style); ResumeLayout(true); Application.DoEvents();
                using (Bitmap shot = new Bitmap(Width, Height)) { DrawToBitmap(shot, new Rectangle(Point.Empty, Size)); shot.Save(Path.Combine(directory, "scaled-150.png")); }
                SuspendLayout(); Scale(new SizeF(4f / 3f, 4f / 3f)); foreach (System.Collections.Generic.KeyValuePair<Control, Font> item in fonts) item.Key.Font = new Font(item.Value.FontFamily, item.Value.Size * 2, item.Value.Style); ResumeLayout(true); Application.DoEvents();
                using (Bitmap shot = new Bitmap(Width, Height)) { DrawToBitmap(shot, new Rectangle(Point.Empty, Size)); shot.Save(Path.Combine(directory, "scaled-200.png")); }
                report.WriteLine("PASS V27: adaptive sidebar/top navigation, all three pages, native key selection, first-run guidance, named/tabbable 32px info buttons, contextual edit explanation, guard on/off availability, filter disclosure, fixed/auto controls; actual 734px compact width, persistent F10 pause action, bidirectional keyboard navigation, and simulated 150/200 percent screenshots. No automatic inputs.");
            }
        }
        static System.Collections.Generic.IEnumerable<Control> Descendants(Control root)
        { foreach (Control c in root.Controls) { yield return c; foreach (Control child in Descendants(c)) yield return child; } }
        static void Describe(Control root, StreamWriter report, int depth)
        {
            report.WriteLine(new String(' ', depth) + root.GetType().Name + ": " + root.Bounds + "; preferred=" + root.PreferredSize + "; " + root.Text.Replace("\n", " "));
            foreach (Control child in root.Controls) Describe(child, report, depth + 1);
        }
        Button BuildInterface()
        {
            Text = "Prefire V27.5"; Font = new Font("Segoe UI", 12); BackColor = DarkTheme.Window;
            AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(1180, 860); MinimumSize = new Size(900, 650); StartPosition = FormStartPosition.CenterScreen;
            if (Height > Screen.PrimaryScreen.WorkingArea.Height) Height = Screen.PrimaryScreen.WorkingArea.Height;
            TableLayoutPanel shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Margin = Padding.Empty };
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 184)); shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 76)); shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 148)); shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); Controls.Add(shell);
            TableLayoutPanel toolbar = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24, 16, 24, 8), ColumnCount = 2, RowCount = 2, Margin = Padding.Empty };
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 400)); toolbar.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); toolbar.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); shell.Controls.Add(toolbar, 0, 0); shell.SetColumnSpan(toolbar, 2);
            FlowLayoutPanel brand = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            Label brandName = Copy("Prefire", 20, true), version = Copy("V27.5", 10.5f); brandName.Margin = new Padding(0, 3, 14, 0); version.Margin = new Padding(0, 14, 0, 0); brand.Controls.Add(brandName); brand.Controls.Add(version); toolbar.Controls.Add(brand, 0, 0);
            FlowLayoutPanel actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty };
            enabled.Text = "Aktivieren · F8"; enabled.Appearance = Appearance.Button; enabled.TextAlign = ContentAlignment.MiddleCenter; enabled.Size = new Size(156, 42); enabled.Margin = new Padding(8, 0, 0, 0); enabled.AccessibleName = "Automatische Eingaben aktivieren oder pausieren"; actions.Controls.Add(enabled);
            Button pause = ActionButton("Pause · F10", 110, delegate { PauseNow(); }); pause.Margin = new Padding(8, 0, 0, 0); pause.AccessibleName = "Sofort pausieren (F10)"; actions.Controls.Add(pause); helpTips.SetToolTip(pause, "Pausiert sofort und bricht ausstehende Eingaben ab.");
            Button save = ActionButton("Speichern", 104, delegate { }); save.Margin = Padding.Empty; actions.Controls.Add(save); toolbar.Controls.Add(actions, 1, 0); helpTips.SetToolTip(save, "Einstellungen speichern · Strg+S");
            Panel sidebar = new Panel { Dock = DockStyle.Fill, Padding = new Padding(18, 10, 0, 16), Margin = Padding.Empty };
            shell.Controls.Add(sidebar, 0, 1); shell.SetRowSpan(sidebar, 2);
            FlowLayoutPanel tabs = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty }; sidebar.Controls.Add(tabs);
            TableLayoutPanel header = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24, 0, 24, 10), ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); header.RowStyles.Add(new RowStyle(SizeType.Absolute, 44)); header.RowStyles.Add(new RowStyle(SizeType.Absolute, 24)); header.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); shell.Controls.Add(header, 1, 1);
            pageTitle.Dock = DockStyle.Fill; pageTitle.Font = new Font("Segoe UI", 24, FontStyle.Bold); pageTitle.Margin = Padding.Empty; header.Controls.Add(pageTitle, 0, 0);
            pageSubtitle.Dock = DockStyle.Fill; pageSubtitle.Font = new Font("Segoe UI", 11); pageSubtitle.Margin = Padding.Empty; pageSubtitle.ForeColor = DarkTheme.Muted; header.Controls.Add(pageSubtitle, 0, 1);
            TableLayoutPanel state = new RoundedCard { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(16, 8, 16, 8), Margin = new Padding(0, 4, 0, 0), BackColor = DarkTheme.Surface };
            state.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); state.RowStyles.Add(new RowStyle(SizeType.Absolute, 27)); state.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); header.Controls.Add(state, 0, 2);
            status.Dock = DockStyle.Fill; status.Font = new Font("Segoe UI", 11.5f, FontStyle.Bold); status.AutoEllipsis = true; status.Margin = Padding.Empty; state.Controls.Add(status, 0, 0);
            feedback.Dock = DockStyle.Fill; feedback.Font = new Font("Segoe UI", 10.5f); feedback.AutoEllipsis = false; feedback.Margin = Padding.Empty; state.Controls.Add(feedback, 0, 1); feedback.TextChanged += delegate { helpTips.SetToolTip(feedback, feedback.Text); };
            string[] names = { "Einrichten", "Baufolge", "Aufnahmen" }; navigation = new Button[3];
            Panel host = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty }; shell.Controls.Add(host, 1, 2); pages = new Panel[] { Page(host), Page(host), Page(host) };
            for (int i = 0; i < 3; i++) { int selected = i; Button tab = ActionButton(names[i], 164, delegate { SelectPage(selected); }); tab.Height = 50; tab.TextAlign = ContentAlignment.MiddleLeft; tab.Padding = new Padding(16, 0, 0, 0); tab.Margin = new Padding(0, 0, 0, 10); tab.AccessibleName = names[i] + " anzeigen"; navigation[i] = tab; tabs.Controls.Add(tab); }
            bool? wideNavigation = null;
            Action arrangeNavigation = delegate {
                bool wide = ClientSize.Width >= 1100f * Font.SizeInPoints / 12f;
                if (wideNavigation == wide) return; wideNavigation = wide; shell.SuspendLayout(); toolbar.SuspendLayout(); tabs.SuspendLayout();
                shell.ColumnStyles[0].Width = wide ? 184f * Font.SizeInPoints / 12f : 0;
                shell.RowStyles[0].Height = (wide ? 76f : 124f) * Font.SizeInPoints / 12f;
                sidebar.Visible = wide; tabs.Parent = wide ? (Control)sidebar : toolbar;
                tabs.FlowDirection = wide ? FlowDirection.TopDown : FlowDirection.LeftToRight;
                if (!wide) { toolbar.SetCellPosition(tabs, new TableLayoutPanelCellPosition(0, 1)); toolbar.SetColumnSpan(tabs, 2); }
                foreach (Button tab in navigation) { tab.Height = (int)((wide ? 50f : 42f) * Font.SizeInPoints / 12f); tab.TextAlign = wide ? ContentAlignment.MiddleLeft : ContentAlignment.MiddleCenter; tab.Padding = wide ? new Padding(16, 0, 0, 0) : Padding.Empty; tab.Margin = wide ? new Padding(0, 0, 0, 10) : new Padding(0, 0, 8, 0); }
                tabs.ResumeLayout(true); toolbar.ResumeLayout(true); shell.ResumeLayout(true);
            };
            ClientSizeChanged += delegate { arrangeNavigation(); }; FontChanged += delegate { arrangeNavigation(); }; arrangeNavigation();

            TableLayoutPanel setup = PageStack(pages[0]), timing = PageStack(pages[1]), recording = PageStack(pages[2]);
            TableLayoutPanel colors = Stack(18); Row(setup, colors);
            Row(colors, Topic("01  Lebensbalken einrichten", "Wähle Farben aus der Lebensanzeige eines Bauteils, nicht aus der Wandtextur.\n\n1. Farbe 1 anklicken und das rote Quadrat über einen vollen grünen Balken schieben.\n2. Weiter drücken, zu Fortnite wechseln und im Screenshot mehrere grüne Pixel auswählen.\n3. Für Farbe 2 dieselben Schritte mit einem beschädigten orangefarbenen Balken wiederholen.\n\nDie Position des Balkens wird anschließend automatisch gesucht. Auf jedem PC einmal selbst einrichten."));
            Row(colors, Copy("Über den Balken ziehen → aufnehmen → farbige Pixel auswählen.", 11));
            foreach (Button sample in new Button[] { first, second }) { sample.Size = new Size(342, 52); sample.TextAlign = ContentAlignment.MiddleLeft; sample.Padding = new Padding(44, 0, 4, 0); sample.Font = new Font("Segoe UI", 10.5f); }
            TableLayoutPanel swatches = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Margin = new Padding(0, 0, 0, 10) };
            swatches.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); swatches.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            first.Dock = second.Dock = DockStyle.Fill; first.Margin = new Padding(0, 0, 8, 0); second.Margin = new Padding(8, 0, 0, 0); swatches.Controls.Add(first, 0, 0); swatches.Controls.Add(second, 1, 0);
            Row(colors, swatches); first.AccessibleName = "Farbe 1 – voller grüner Lebensbalken"; second.AccessibleName = "Farbe 2 – beschädigter orangefarbener Lebensbalken";
            first.Click += delegate { ShowColorWizard(true); }; second.Click += delegate { ShowColorWizard(false); };
            TableLayoutPanel activation = Stack(18);
            Row(activation, Topic("Haltetaste", "F8 schaltet das Tool ein oder aus. Nur solange du zusätzlich die Haltetaste drückst, kann eine Schusseingabe ausgelöst werden.\n\nVor dem Halten muss der Lebensbalken kurz stabil erkannt werden. Nach Aktivierung oder Fensterwechsel die Haltetaste einmal loslassen.\n\nF10 pausiert sofort. Haltetaste und Bautaste müssen unterschiedlich sein. Einstellungen ändern pausiert das Tool."));
            Combo(hold, 330, "Haltetaste"); hold.Items.AddRange(new object[] { new Choice("Seitliche Maustaste 1 (XButton1)", 5), new Choice("Seitliche Maustaste 2 (XButton2)", 6), new Choice("Umschalt", 0x10), new Choice("Strg", 0x11), new Choice("Alt", 0x12), new Choice("Leertaste", 0x20) }); SelectKey(hold, cfg.HoldKey); Row(activation, Inline(hold));
            Row(activation, Copy("Nur während des Haltens auslösen.\nF8 schaltet das Tool ein oder aus.", 11));
            TableLayoutPanel live = Stack(18); Row(setup, new AdaptivePair(activation, live));
            Row(live, Topic("Erkennung im Spiel", "Wechsle zu Fortnite und visiere ein Bauteil mit Lebensbalken an. Der Bildtest läuft auch bei pausiertem Tool.\n\nDie Vorschau zeigt den erkannten Balken. Solange ein bestätigter Zahlenbalken sichtbar ist, wird kein Schuss angefordert.\n\nEin freier Edit-Ausschnitt bei weiterhin sichtbarem Balken lässt sich damit nicht zuverlässig als freie Schussbahn erkennen. Das Tool erkennt keine Gegner und prüft keine Geometrie im Spiel."));
            detectionStatus.AutoSize = true; detectionStatus.Dock = DockStyle.Fill; detectionStatus.Text = "Noch kein Bildtest. In Fortnite einen Lebensbalken anvisieren."; Row(live, detectionStatus);
            barPreview.Size = new Size(260, 46); barPreview.SizeMode = PictureBoxSizeMode.Zoom; barPreview.BackColor = DarkTheme.Field; barPreview.AccessibleName = "Vorschau des erkannten Lebensbalkens";
            previewHint.Text = "Noch kein Balken erkannt"; previewHint.Dock = DockStyle.Fill; previewHint.TextAlign = ContentAlignment.MiddleCenter; previewHint.Font = new Font("Segoe UI", 10.5f); previewHint.BackColor = DarkTheme.Field; previewHint.ForeColor = DarkTheme.Muted; barPreview.Controls.Add(previewHint); Row(live, Inline(barPreview));
            frameTiming.Text = "Bildzeiten: noch keine Messung"; frameTiming.AutoSize = true; frameTiming.Dock = DockStyle.Fill; frameTiming.Font = new Font("Segoe UI", 10.5f); frameTiming.AccessibleName = frameTiming.Text; frameTiming.AccessibleDescription = "Zeiten für Aufnahme und Erkennung des letzten Bildes, keine gesamte Reaktionszeit im Spiel."; Row(live, frameTiming);
            helpTips.SetToolTip(frameTiming, "Gemessene Verarbeitung des letzten Bildes. Aufnahme und Erkennung werden getrennt angezeigt. Das ist keine Messung der gesamten Reaktionszeit im Spiel.");
            turbo.Text = "Turbomodus"; turbo.AutoSize = true; turbo.Checked = cfg.TurboMode;
            Row(live, OptionHelp(turbo, "Turbomodus", "Nutzt bis zu acht CPU-Arbeiter für große Bildbereiche und fordert bei gehaltener Haltetaste alle 1 ms eine neue Verarbeitung an. Es wird höchstens eine Anforderung vorgemerkt; überlappende Prüfungen und ein Rückstau alter Bilder werden verhindert. Die tatsächliche Bildrate hängt von Aufnahme und Verarbeitung ab.\n\nGPU-Aufnahme wird automatisch mitbenutzt. Die letzte Prüfung verwendet dieselbe bereits eingerichtete GPU-Quelle und verlangt ein neues Bild nach Ablauf des Verlustfilters. Ist keines verfügbar, folgt eine frische GDI-Aufnahme. Im entscheidenden Moment wird keine zweite GPU-Quelle eingerichtet.\n\nKann mehr CPU/GPU beanspruchen und bei ausgelastetem Spiel dessen Bildrate beeinträchtigen. Verlustfilter, Fokusprüfung und Schutz vor erneut sichtbaren Wänden bleiben erhalten. Turbo erkennt keine freien Fenster-Edits bei weiterhin sichtbarem Lebensbalken. Änderungen pausieren das Tool; anschließend speichern und mit F8 aktivieren."));
            fastCapture.Text = "GPU-Aufnahme ohne Turbo"; fastCapture.AutoSize = true; fastCapture.Checked = cfg.FastCapture;
            Row(live, OptionHelp(fastCapture, "GPU-Aufnahme", "Verwendet die GPU für die laufende Zielbeobachtung, auch wenn Turbo aus ist. Die letzte Schussprüfung verwendet ohne Turbo eine neue GDI-Aufnahme. Mit Turbo wird diese Option automatisch mitbenutzt.\n\nGPU-Bilder müssen neu und höchstens 12 ms alt sein. Ist kein frisches Bild verfügbar, folgt eine GDI-Aufnahme. Nach einem GPU-Fehler bleibt GDI bis zum Fokus- oder Optionswechsel aktiv; dadurch werden wiederholte Einrichtungsverzögerungen vermieden. Fokus, Spielbereich und Bildalter werden weiterhin geprüft. HDR, gedrehte oder mehrere Monitore im Spielbereich verwenden GDI.\n\nDie Bildzeiten sind Verarbeitungszeiten und keine Messung der gesamten Schussreaktion. Änderungen pausieren das Tool; anschließend speichern und mit F8 aktivieren."));
            TableLayoutPanel building = Stack(18); Row(timing, building);
            Row(building, Topic("01  Wand nach dem Schuss", "Die Folge lautet: Linksklick zum Schießen → Bautaste → erneuter Linksklick zum Platzieren.\n\nWähle dieselbe Bautaste, die in Fortnite die Wand auswählt. XButton1 ist meist die hintere und XButton2 die vordere Daumentaste.\n\nDer Platzierungsklick bleibt 80 ms gehalten. Ein gesendeter Klick bestätigt noch nicht, dass der Server eine Wand platziert hat. Bei Fokusverlust oder F10 wird die Folge abgebrochen."));
            build.Text = "Nach dem Schuss bauen und platzieren"; build.AutoSize = true; build.Checked = cfg.BuildAfter; Row(building, Inline(build));
            Combo(buildKey, 430, "Bautaste");
            buildKey.Items.Add(new Choice("Hintere Maustaste (XButton1)", 5)); buildKey.Items.Add(new Choice("Vordere Maustaste (XButton2)", 6)); buildKey.Items.Add(new Choice("Mittlere Maustaste", 4)); buildKey.Items.Add(new Choice("Rechte Maustaste", 2)); foreach (char c in "QFECVGRT123456789") buildKey.Items.Add(new Choice(c.ToString(), c)); SelectKey(buildKey, cfg.BuildKey); Row(building, Inline(Copy("Bautaste", 11), buildKey));
            editGuard.Text = "Edit-Schutz: keine Baufolge bei sichtbarem Balken"; editGuard.AutoSize = true; editGuard.Checked = cfg.EditBuildGuard; editGuard.AccessibleName = "Edit-Schutz – Baufolge bei sichtbarem Bauteil unterdrücken";
            Row(building, OptionHelp(editGuard, "Edit-Schutz", "Ein: Vor der Bautaste wird ein neues Bild geprüft. Ist wieder ein Bauteilbalken sichtbar, bleibt es beim Schuss; Bautaste und Platzierungsklick werden ausgelassen.\n\nDas schützt zum Beispiel bei einem Edit oder einer sofort neu gebauten Wand. Auch bei einer unlesbaren Aufnahme wird die Baufolge ausgelassen.\n\nDer Schutz erkennt keine Spielgeometrie und kann nicht jeden Edit unterscheiden. Die zusätzliche Bildprüfung verlängert den Abstand bis zur Bautaste.\n\nAus: Es gilt die bisherige Schuss- und Baufolge. Der Haken ist standardmäßig aus."));
            Row(building, Copy("Prüft vor der Bautaste erneut. Der Schuss bleibt erhalten.", 10.5f));
            TableLayoutPanel additional = Stack(18); Row(timing, additional);
            extras.Text = "Zusätzliche Platzierungen"; extras.AutoSize = true; extras.Checked = cfg.ExtraPlacements;
            Row(additional, OptionHelp(extras, "Zusätzliche Platzierungen", "Nach dem Schuss: Wand → Treppe → Boden. Nur angehakte Bauteile werden gesendet. Wähle jeweils die entsprechende Fortnite-Taste.\n\nJede Auswahl bekommt einen eigenen Linksklick. Die Kamera wird nicht bewegt: Ob das Bauteil an dieser Stelle möglich ist, entscheidet das Spiel. Es gibt keine Serverbestätigung.\n\nF10 oder ein Fensterwechsel bricht auch zusätzliche Platzierungen ab. Bei aktiviertem Edit-Schutz wird die gesamte Baufolge ausgelassen, wenn wieder ein Balken sichtbar ist."));
            Panel extraPanel = new Panel { AutoSize = true, Dock = DockStyle.Top, Margin = Padding.Empty };
            TableLayoutPanel extraStack = Stack(0); extraPanel.Controls.Add(extraStack); extraPanel.Visible = extras.Checked; Row(additional, extraPanel);
            extraStair.Text = "Treppe platzieren"; extraStair.AutoSize = true; extraStair.Checked = cfg.ExtraStair;
            extraFloor.Text = "Boden platzieren"; extraFloor.AutoSize = true; extraFloor.Checked = cfg.ExtraFloor;
            BuildChoices(stairKey, cfg.StairKey, "Taste für Treppe"); BuildChoices(floorKey, cfg.FloorKey, "Taste für Boden");
            Row(extraStack, Copy("02  Treppe", 12, true)); Row(extraStack, Inline(extraStair)); Row(extraStack, Inline(stairKey));
            Row(extraStack, Copy("03  Boden", 12, true)); Row(extraStack, Inline(extraFloor)); Row(extraStack, Inline(floorKey));
            Number(extraDelay, 15, 500, cfg.ExtraDelayMs, "Pause vor nächstem Bauteil in Millisekunden");
            Row(extraStack, Copy("Pause vor dem nächsten Bauteil", 11)); Row(extraStack, Inline(extraDelay, Copy("ms", 11)));
            extras.CheckedChanged += delegate { extraPanel.Visible = extras.Checked; };
            TableLayoutPanel times = Stack(18); Row(timing, times);
            Row(times, Topic("Zwei Abstände", "Schuss → Bautaste: Wartezeit vor der Auswahl der Wand (mindestens 35 ms).\n\nBautaste → Linksklick: Wartezeit bis zum Platzieren (mindestens 10 ms).\n\nAutomatik: Beide Abstände reagieren auf lesbare Munitions- und Baumodusanzeigen. Pro Abstand wird höchstens etwa 20 ms länger als der eingestellte Wert auf die Anzeige gewartet. Fehlt sie, läuft die Folge ohne Bestätigung weiter.\n\nEine perfekte Zeit oder der aktuelle Ping wird damit nicht gemessen. Für kurze, gleichbleibende Abstände feste Zeiten verwenden."));
            Number(delay, 35, 1000, cfg.BuildDelay, "Schuss bis Bautaste in Millisekunden"); Number(placeDelay, 10, 200, cfg.PlaceDelayMs, "Bautaste bis Platzierung in Millisekunden");
            TableLayoutPanel gaps = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Margin = new Padding(0, 0, 0, 10) };
            gaps.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); gaps.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            TableLayoutPanel shotGap = Stack(0), placeGap = Stack(0); Row(shotGap, Copy("Schuss → Bautaste", 11.5f, true)); Row(shotGap, Inline(delay, Copy("ms", 11))); Row(placeGap, Copy("Bautaste → Linksklick", 11.5f, true)); Row(placeGap, Inline(placeDelay, Copy("ms", 11))); gaps.Controls.Add(shotGap, 0, 0); gaps.Controls.Add(placeGap, 1, 0); Row(times, gaps);
            adaptiveBuild.Text = "Beide Zeiten automatisch anpassen"; adaptiveBuild.AutoSize = true; adaptiveBuild.Checked = cfg.AdaptiveBuildAfter; Row(times, Inline(adaptiveBuild)); Row(times, Copy("Experimentell · wartet je Abstand höchstens etwa 20 ms zusätzlich.\nOhne Haken gelten die festen Werte oben.", 11));
            Button filterToggle = ActionButton("Erweiterte Filter anzeigen", 300, delegate { }); Row(setup, Inline(filterToggle));
            TableLayoutPanel sensitivity = Stack(18); Row(setup, sensitivity); sensitivity.Visible = false;
            filterToggle.Click += delegate { sensitivity.Visible = !sensitivity.Visible; filterToggle.Text = sensitivity.Visible ? "Erweiterte Filter ausblenden" : "Erweiterte Filter anzeigen"; };
            Row(sensitivity, Topic("Erkennungsfilter", "Verlustfilter: Mindestabstand zwischen dem ersten Bild ohne Balken und einer zweiten, frischen Prüfung. Kurze Bildausfälle sollen nicht auslösen.\n\nFarbtoleranz: Wie stark die Balkenfarbe von der Auswahl abweichen darf. Ein großer Wert kann mehr fremde Farben einschließen.\n\nStandard: 20 ms und 25. Eine kleinere Zahl allein löst keine fehlende Zielerkennung.\n\nBauteile hintereinander: Die Erkennung vergleicht auch Balkenposition, Lebensanzeige und Kamerabewegung. Ein anderer Balken darf ein zuvor verschwundenes Ziel nicht einfach ersetzen. Bei identischen Balken oder unklarer Bewegung bleibt das Ergebnis unsicher."));
            Number(lossDelay, 20, 200, cfg.LossDelayMs, "Verlustfilter in Millisekunden"); Number(tolerance, 0, 100, cfg.Tolerance, "Farbtoleranz");
            TableLayoutPanel lossField = Stack(0), toleranceField = Stack(0); lossField.Dock = toleranceField.Dock = DockStyle.None; lossField.Width = toleranceField.Width = 220;
            Row(lossField, Copy("Verlustfilter (ms)", 11)); Row(lossField, lossDelay); Row(toleranceField, Copy("Farbtoleranz", 11)); Row(toleranceField, tolerance); Row(sensitivity, Inline(lossField, toleranceField));
            Number(x, -32768, 32767, cfg.X, "Kalibrierung X"); Number(y, -32768, 32767, cfg.Y, "Kalibrierung Y"); x.Visible = y.Visible = false;
            TableLayoutPanel clips = Stack(18); Row(recording, clips);
            Row(clips, Topic("Gameplay aufnehmen", "F11 startet oder stoppt die Aufnahme. Es wird nur das Fortnite-Bild ohne Ton aufgenommen. Beim Fensterwechsel zeigt das Video eine neutrale Pause.\n\nF12 markiert den aktuellen Zeitpunkt samt Aktivierung, Haltetaste und Erkennungszustand. So können Fehler später zugeordnet werden.\n\nAufnahmen bleiben im Unterordner Aufnahmen neben der EXE. Es erfolgt kein automatischer Upload. Die Datei zum Weitergeben enthält keine Aufnahmen."));
            Row(clips, Copy("F11  Aufnahme / Stopp     ·     F12  Fehler markieren", 12));
            recordButton.Text = "Aufnahme starten (F11)"; recordButton.Size = new Size(280, 42); recordButton.Click += delegate { ToggleRecording(); };
            Button mark = ActionButton("Fehler markieren (F12)", 270, delegate { MarkRecording(); }); Row(clips, Inline(recordButton, mark));
            recordingStatus.Text = "Aufnahme aus · nur Spielbild, ohne Ton"; recordingStatus.AutoSize = true; recordingStatus.Dock = DockStyle.Fill; Row(clips, recordingStatus);
            Row(clips, Inline(ActionButton("Aufnahmen öffnen", 250, delegate { try { string path = recorder.DirectoryPath ?? RecordingRoot; Directory.CreateDirectory(path); System.Diagnostics.Process.Start("explorer.exe", "\"" + path + "\""); } catch (Exception ex) { feedback.Text = ex.Message; } })));
            TableLayoutPanel debug = Stack(18); Row(recording, debug);
            Row(debug, Topic("Diagnose speichern", "Speichert das aktuelle Erkennungsprotokoll lokal. Es enthält Zustände und Zeitpunkte, aber keine vollständige Gameplay-Aufnahme.\n\nBei einem Fehler zusätzlich F11 für ein Video und F12 für eine Markierung verwenden. An Freunde nur den Loader weitergeben; Diagnose und Videos gehören nicht hinein."));
            Row(debug, Copy("Ein lokales Protokoll hilft, einen markierten Fehler nachzuvollziehen."));
            Row(debug, Inline(ActionButton("Diagnose speichern", 250, delegate { try { feedback.Text = "Diagnose gespeichert: " + trace.Save(); } catch (Exception ex) { feedback.Text = "Diagnose konnte nicht gespeichert werden: " + ex.Message; } })));
            DarkTheme.Apply(this); feedback.ForeColor = DarkTheme.Muted; enabled.BackColor = DarkTheme.Field;
            foreach (TableLayoutPanel panel in new TableLayoutPanel[] { colors, activation, live, building, times, sensitivity, clips, debug }) foreach (Control c in panel.Controls) if (c is Label) c.ForeColor = DarkTheme.Muted;
            foreach (Button sample in new Button[] { first, second }) DarkTheme.Button(sample);
            enabled.CheckedChanged += delegate { enabled.BackColor = enabled.Checked ? DarkTheme.Field : DarkTheme.Accent; enabled.ForeColor = enabled.Checked ? DarkTheme.Text : DarkTheme.PrimaryText; enabled.Text = enabled.Checked ? "Pausieren · F8" : "Aktivieren · F8"; };
            enabled.BackColor = DarkTheme.Accent; enabled.ForeColor = DarkTheme.PrimaryText;
            RefreshSwatches(); SelectPage(0);
            Shown += delegate
            {
                Rectangle available = Screen.FromControl(this).WorkingArea;
                MinimumSize = new Size(Math.Min(MinimumSize.Width, available.Width), Math.Min(MinimumSize.Height, available.Height));
                Size = new Size(Math.Min(Width, available.Width), Math.Min(Height, available.Height));
                Location = new Point(Math.Max(available.Left, Math.Min(Left, available.Right - Width)), Math.Max(available.Top, Math.Min(Top, available.Bottom - Height)));
            };
            FormClosed += delegate { helpTips.Dispose(); if (helpWindow != null) helpWindow.Dispose(); };
            return save;
        }
    }
}
