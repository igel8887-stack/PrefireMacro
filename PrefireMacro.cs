using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using System.Xml.Serialization;

namespace PrefireMacro
{
    public class Config
    {
        public int X = 960, Y = 540, Tolerance = 25, HoldKey = 5;
        public int FirstColor, SecondColor;
        public bool HasFirst, HasSecond, BuildAfter;
        public int BuildKey = 5, BuildDelay = 70;
        public int PlaceDelayMs = 35;
        public bool AdaptiveBuildAfter;
        public bool EditBuildGuard;
        public bool FastCapture;
        public bool ExtraPlacements, ExtraStair = true, ExtraFloor;
        public int StairKey = 70, FloorKey = 67, ExtraDelayMs = 60;
        public int LossDelayMs = 20;
        public List<PixelSample> FirstSamples = new List<PixelSample>(), SecondSamples = new List<PixelSample>();
    }

    public static class Trigger
    {
        public static bool Matches(int rgb, int reference, int tolerance)
        {
            return Math.Abs(((rgb >> 16) & 255) - ((reference >> 16) & 255)) <= tolerance
                && Math.Abs(((rgb >> 8) & 255) - ((reference >> 8) & 255)) <= tolerance
                && Math.Abs((rgb & 255) - (reference & 255)) <= tolerance;
        }
    }

    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] internal struct MouseInput
        { public int dx, dy; public uint mouseData, dwFlags, time; public UIntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] internal struct KeyInput
        { public ushort wVk, wScan; public uint dwFlags, time; public UIntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Explicit)] internal struct InputUnion
        { [FieldOffset(0)] public MouseInput mouse; [FieldOffset(0)] public KeyInput key; }
        [StructLayout(LayoutKind.Sequential)] internal struct Input
        { public uint type; public InputUnion data; }
        [DllImport("user32.dll")] internal static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point p);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr h, out uint id);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] internal static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] internal static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32.dll")] internal static extern uint GetPixel(IntPtr dc, int x, int y);
        [DllImport("user32.dll", SetLastError = true)] internal static extern uint SendInput(uint n, Input[] inputs, int size);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint key);
        [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr h, int id);
        [DllImport("winmm.dll")] internal static extern uint timeBeginPeriod(uint period);
        [DllImport("winmm.dll")] internal static extern uint timeEndPeriod(uint period);
        internal static bool Held(int key) { return (GetAsyncKeyState(key) & 0x8000) != 0; }
        internal static bool ForegroundMatches(IntPtr window, uint process)
        {
            if (window == IntPtr.Zero || process == 0 || GetForegroundWindow() != window) return false;
            uint actual; return GetWindowThreadProcessId(window, out actual) != 0 && actual == process;
        }
        internal static bool ReadPixel(IntPtr dc, int x, int y, out int rgb)
        {
            uint c = GetPixel(dc, x, y);
            rgb = (int)(((c & 255) << 16) | (c & 0xFF00) | ((c >> 16) & 255));
            return c != 0xFFFFFFFF;
        }
        internal static bool MouseButton(bool down)
        {
            Input i = new Input(); i.data.mouse.dwFlags = down ? 2u : 4u;
            return SendInput(1, new Input[] { i }, Marshal.SizeOf(typeof(Input))) == 1;
        }
        internal static bool SetButton(int key, bool down)
        {
            Input i = MakeButton(key, down);
            return SendInput(1, new Input[] { i }, Marshal.SizeOf(typeof(Input))) == 1;
        }
        internal static Input MakeButton(int key, bool down)
        {
            Input i = new Input();
            if (key == 1) { i.data.mouse.dwFlags = down ? 2u : 4u; return i; }
            if (key == 2 || key == 4 || key == 5 || key == 6)
            {
                if (key == 2) i.data.mouse.dwFlags = down ? 8u : 16u;
                else if (key == 4) i.data.mouse.dwFlags = down ? 32u : 64u;
                else { i.data.mouse.dwFlags = down ? 128u : 256u; i.data.mouse.mouseData = key == 5 ? 1u : 2u; }
                return i;
            }
            i.type = 1; i.data.key.wVk = (ushort)key; i.data.key.dwFlags = down ? 0u : 2u; return i;
        }
        internal static bool KeyDown(int key)
        {
            Input i = new Input(); i.type = 1; i.data.key.wVk = (ushort)key;
            return SendInput(1, new Input[] { i }, Marshal.SizeOf(typeof(Input))) == 1;
        }
        internal static bool KeyUp(int key)
        {
            Input i = new Input(); i.type = 1; i.data.key.wVk = (ushort)key; i.data.key.dwFlags = 2;
            return SendInput(1, new Input[] { i }, Marshal.SizeOf(typeof(Input))) == 1;
        }
    }

    internal sealed class Choice
    {
        internal string Name; internal int Key;
        internal Choice(string name, int key) { Name = name; Key = key; }
        public override string ToString() { return Name; }
    }

    public sealed partial class MainForm : Form
    {
        readonly Config cfg;
        PrefireGate trigger;
        readonly HealthBarTracker tracker = new HealthBarTracker();
        readonly DetectionTrace trace = new DetectionTrace();
        readonly GameplayRecorder recorder = new GameplayRecorder();
        readonly Button recordButton = new RoundedButton();
        readonly Label recordingStatus = new Label();
        bool recordingKeysReady, markerWasHeld, preciseTimer;
        long lastRecorderStatus;
        public bool RecordOnStart;
        long attemptGeneration;
        bool hadForeground, wasHeld;
        BarObservation lastObservation;
        readonly Timer timer = new Timer();
        readonly CheckBox enabled = new RoundedToggle(), build = new CheckBox();
        readonly CheckBox adaptiveBuild = new CheckBox();
        readonly CheckBox editGuard = new CheckBox();
        readonly CheckBox fastCapture = new CheckBox();
        readonly CheckBox extras = new CheckBox(), extraStair = new CheckBox(), extraFloor = new CheckBox();
        readonly ComboBox stairKey = new DarkComboBox(), floorKey = new DarkComboBox();
        readonly NumericUpDown extraDelay = new RoundedNumber();
        readonly NumericUpDown x = new RoundedNumber(), y = new RoundedNumber(), tolerance = new RoundedNumber(), delay = new RoundedNumber();
        readonly NumericUpDown lossDelay = new RoundedNumber();
        readonly NumericUpDown placeDelay = new RoundedNumber();
        readonly ComboBox hold = new DarkComboBox(), buildKey = new DarkComboBox();
        readonly Button first = new ColorSampleButton(), second = new ColorSampleButton();
        readonly Label status = new Label(), feedback = new Label();
        readonly Label detectionStatus = new Label();
        readonly PictureBox barPreview = new PictureBox();
        readonly HealthBarCapture barCapture = new HealthBarCapture();
        HealthBarDetector barDetector;
        HealthBarDetector shotDetector;
        readonly ShotVerifier shotVerifier = new ShotVerifier();
        BarDetection lastDetection;
        readonly System.Diagnostics.Stopwatch scanClock = System.Diagnostics.Stopwatch.StartNew();
        long lastScan = -1000, lastPreview;
        bool releaseRequired = true, hotkeysReady, calibrating, updatingPosition;
        int shots;
        readonly InputSequence sequence = new InputSequence();
        Rectangle captureArea;
        readonly bool preview;
        public static string ConfigPath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PrefireMacro", "config.xml"); } }
        internal bool TimingControlsValid { get { return adaptiveBuild.Enabled == build.Checked && editGuard.Enabled == build.Checked && delay.Enabled == !(build.Checked && adaptiveBuild.Checked) && placeDelay.Enabled == delay.Enabled; } }
        public bool StartupReady { get { return hotkeysReady && recordingKeysReady && TimingControlsValid && !enabled.Checked && hold.SelectedItem != null && buildKey.SelectedItem != null; } }
        public MainForm(Config config, bool previewOnly)
        {
            cfg = config; preview = previewOnly;
            trigger = new PrefireGate(Math.Max(20, Math.Min(200, cfg.LossDelayMs)));
            if (cfg.FirstSamples == null) cfg.FirstSamples = new List<PixelSample>();
            if (cfg.SecondSamples == null) cfg.SecondSamples = new List<PixelSample>();
            Button save = BuildInterface();
            captureArea = new Rectangle(cfg.X - 64, cfg.Y - 64, 128, 128);
            foreach (System.Diagnostics.Process game in System.Diagnostics.Process.GetProcessesByName("FortniteClient-Win64-Shipping"))
            {
                using (game)
                {
                    try
                    {
                        Rectangle client;
                        if (HealthBarCapture.ClientBounds(game.MainWindowHandle, out client))
                        { captureArea = new Rectangle(client.Left + (int)(client.Width * .43) - 64, client.Top + (int)(client.Height * .48) - 64, 128, 128); break; }
                    }
                    catch (InvalidOperationException) { }
                }
            }
            Action updateTimingFields = delegate { adaptiveBuild.Enabled = editGuard.Enabled = extras.Enabled = build.Checked; delay.Enabled = placeDelay.Enabled = !(build.Checked && adaptiveBuild.Checked); extraStair.Enabled = extraFloor.Enabled = extraDelay.Enabled = build.Checked && extras.Checked; stairKey.Enabled = build.Checked && extras.Checked && extraStair.Checked; floorKey.Enabled = build.Checked && extras.Checked && extraFloor.Checked; };
            updateTimingFields();
            trace.LineAdded += recorder.Log;
            save.Click += delegate { Save(); };
            enabled.CheckedChanged += delegate
            {
                if (enabled.Checked && (!cfg.HasFirst || !hotkeysReady))
                {
                    enabled.Checked = false;
                    feedback.Text = !cfg.HasFirst ? "Unter Einrichten zuerst Farbe 1 aus dem Lebensbalken auswählen." : "Hotkeys nicht verfügbar. Zweite Instanz schließen und neu starten.";
                    return;
                }
                if (enabled.Checked && build.Checked && ((Choice)hold.SelectedItem).Key == ((Choice)buildKey.SelectedItem).Key)
                { enabled.Checked = false; feedback.Text = "Haltetaste und Bautaste müssen unterschiedlich sein. Bitte Belegung wählen."; return; }
                if (enabled.Checked && build.Checked && extras.Checked)
                {
                    List<int> configured = new List<int> { ((Choice)hold.SelectedItem).Key, ((Choice)buildKey.SelectedItem).Key };
                    if (extraStair.Checked) configured.Add(((Choice)stairKey.SelectedItem).Key);
                    if (extraFloor.Checked) configured.Add(((Choice)floorKey.SelectedItem).Key);
                    if (new HashSet<int>(configured).Count != configured.Count || (!extraStair.Checked && !extraFloor.Checked))
                    { enabled.Checked = false; feedback.Text = "Für zusätzliche Platzierungen mindestens ein Bauteil und unterschiedliche Tasten wählen."; return; }
                }
                StopPending(); trigger.Reset(); releaseRequired = true; UpdateStatus("Pausiert");
                SetTimerPrecision(enabled.Checked);
                feedback.Text = enabled.Checked ? "AKTIV – Haltetaste gedrückt halten. HUD-Auslöser ist bereit zur Beobachtung." : "PAUSIERT – keine automatischen Eingaben.";
                trace.Add(scanClock.ElapsedMilliseconds, enabled.Checked, Native.Held(((Choice)hold.SelectedItem).Key), lastObservation, trigger.Status, "Aktivierung geändert");
                recorder.SetControls(enabled.Checked, Native.Held(((Choice)hold.SelectedItem).Key), enabled.Checked ? "AKTIV" : "PAUSIERT");
                if (!preview) { if (enabled.Checked) System.Media.SystemSounds.Asterisk.Play(); else System.Media.SystemSounds.Exclamation.Play(); }
            };
            x.ValueChanged += delegate { PositionChanged(); }; y.ValueChanged += delegate { PositionChanged(); };
            hold.SelectedIndexChanged += delegate { enabled.Checked = false; trigger.Reset(); releaseRequired = true; };
            tolerance.ValueChanged += delegate { enabled.Checked = false; tracker.Reset(); trigger.Reset(); barDetector = new HealthBarDetector(cfg, (int)tolerance.Value); shotDetector = new HealthBarDetector(cfg, (int)tolerance.Value); };
            lossDelay.ValueChanged += delegate { enabled.Checked = false; StopPending(); trigger = new PrefireGate((int)lossDelay.Value); };
            build.CheckedChanged += delegate { enabled.Checked = false; updateTimingFields(); };
            adaptiveBuild.CheckedChanged += delegate { enabled.Checked = false; updateTimingFields(); };
            editGuard.CheckedChanged += delegate { enabled.Checked = false; trace.Add(scanClock.ElapsedMilliseconds, false, false, lastObservation, trigger.Status, "Edit-Schutz geändert: " + editGuard.Checked); };
            fastCapture.CheckedChanged += delegate { enabled.Checked = false; barCapture.ResetFastCapture(); tracker.Reset(); trigger.Reset(); ClearPreview(); };
            buildKey.SelectedIndexChanged += delegate { enabled.Checked = false; };
            foreach (CheckBox option in new CheckBox[] { extras, extraStair, extraFloor }) option.CheckedChanged += delegate { enabled.Checked = false; updateTimingFields(); };
            foreach (ComboBox option in new ComboBox[] { stairKey, floorKey }) option.SelectedIndexChanged += delegate { enabled.Checked = false; };
            extraDelay.ValueChanged += delegate { enabled.Checked = false; };
            delay.ValueChanged += delegate { enabled.Checked = false; };
            placeDelay.ValueChanged += delegate { enabled.Checked = false; };
            timer.Interval = 20; timer.Tick += delegate { TickLoop(); };
            UpdateStatus("Pausiert"); feedback.Text = cfg.HasFirst ? "Zuerst stabilen Balken anvisieren, dann Haltetaste halten. F10 pausiert sofort." : "Beginne mit Farbe 1: Wähle grüne Pixel aus dem vollen Lebensbalken. Danach Farbe 2 einrichten und speichern.";
            Shown += delegate { if (!preview) { RegisterKeys(); timer.Start(); if (RecordOnStart) ToggleRecording(); } };
            FormClosing += delegate { timer.Stop(); SetTimerPrecision(false); sequence.Dispose(); if (!sequence.Running) shotVerifier.Dispose(); recorder.Dispose(); barCapture.Dispose(); if (barPreview.Image != null) barPreview.Image.Dispose(); for (int i = 1; i <= 6; i++) Native.UnregisterHotKey(Handle, i); };
        }
        void SetTimerPrecision(bool active)
        {
            if (active && !preciseTimer) preciseTimer = Native.timeBeginPeriod(1) == 0;
            else if (!active && preciseTimer) { Native.timeEndPeriod(1); preciseTimer = false; }
            timer.Interval = active ? 5 : 20;
        }
        static string RecordingRoot { get { string custom = Environment.GetEnvironmentVariable("PREFIRE_RECORDING_ROOT"); return !String.IsNullOrWhiteSpace(custom) && Path.IsPathRooted(custom) ? custom : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Aufnahmen"); } }
        void ToggleRecording()
        {
            try
            {
                if (recorder.Recording) recorder.Stop();
                else { recorder.Start(RecordingRoot, delegate { return scanClock.ElapsedMilliseconds; }, cfg); recorder.Marker("Aufnahme gestartet; aktuelle UI-Werte: Verlustfilter=" + lossDelay.Value + "; Toleranz=" + tolerance.Value + "; HoldKey=" + ((Choice)hold.SelectedItem).Key + "; Build=" + build.Checked + "; BuildKey=" + ((Choice)buildKey.SelectedItem).Key + "; BuildDelay=" + delay.Value + "; PlaceDelay=" + placeDelay.Value + "; AdaptiveBuild=" + (build.Checked && adaptiveBuild.Checked) + "; EditGuard=" + (build.Checked && editGuard.Checked) + "; Version=" + typeof(MainForm).Assembly.GetName().Version + "; GPUCapture=" + fastCapture.Checked + "; MouseMotionGate=False"); }
                RefreshRecordingStatus();
            }
            catch (Exception ex) { feedback.Text = "Aufnahme konnte nicht gestartet werden: " + ex.Message; }
        }
        void MarkRecording()
        {
            if (!recorder.Recording) { feedback.Text = "Zuerst Aufnahme mit F11 starten."; return; }
            trace.Add(scanClock.ElapsedMilliseconds, enabled.Checked, Native.Held(((Choice)hold.SelectedItem).Key), lastObservation, trigger.Status, "MARKER: Fehler vom Nutzer markiert"); feedback.Text = "Fehlerzeitpunkt einschließlich Aktivierung und Haltetaste markiert.";
        }
        void RefreshRecordingStatus()
        {
            recordButton.Text = recorder.Recording ? "Aufnahme stoppen (F11)" : "Aufnahme starten (F11)";
            recordingStatus.Text = recorder.StatusText + (recorder.DirectoryPath == null ? " · Nur Spielbild, ohne Ton" : "\n" + recorder.DirectoryPath);
        }
        void AddLabel(string text, int left, int top, int width, int height, int size, bool bold)
        {
            Label l = new Label(); l.Text = text; l.SetBounds(left, top, width, height);
            l.Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular); Controls.Add(l);
        }
        void SetupNumber(NumericUpDown n, int left, int top, int min, int max, int value, int width)
        { n.Minimum = min; n.Maximum = max; n.Value = Math.Max(min, Math.Min(max, value)); n.SetBounds(left, top, width, 30); Controls.Add(n); }
        void SetupCombo(ComboBox c, int left, int top, int width)
        { c.DropDownStyle = ComboBoxStyle.DropDownList; c.SetBounds(left, top, width, 30); Controls.Add(c); }
        void SetupSwatch(Button l, int left, int top)
        { l.SetBounds(left, top, 339, 46); l.TextAlign = ContentAlignment.MiddleCenter; l.Cursor = Cursors.Hand; l.UseVisualStyleBackColor = false; Controls.Add(l); }
        static void SelectKey(ComboBox combo, int key)
        { combo.SelectedIndex = 0; for (int i = 0; i < combo.Items.Count; i++) if (((Choice)combo.Items[i]).Key == key) combo.SelectedIndex = i; }
        void PositionChanged()
        { if (updatingPosition) return; enabled.Checked = false; cfg.HasFirst = cfg.HasSecond = false; cfg.FirstSamples.Clear(); cfg.SecondSamples.Clear(); captureArea = new Rectangle((int)x.Value - 64, (int)y.Value - 64, 128, 128); RefreshSwatches(); feedback.Text = "Position geändert: Farben neu auswählen."; }
        void RefreshSwatches()
        {
            ClearPreview();
            Swatch(first, cfg.HasFirst, cfg.FirstColor, "Farbe 1 · Voller Balken", cfg.FirstSamples.Count); Swatch(second, cfg.HasSecond, cfg.SecondColor, "Farbe 2 · Beschädigt", cfg.SecondSamples.Count);
            barDetector = new HealthBarDetector(cfg, (int)(tolerance.IsHandleCreated ? tolerance.Value : Math.Max(0, Math.Min(100, cfg.Tolerance))));
            shotDetector = new HealthBarDetector(cfg, (int)(tolerance.IsHandleCreated ? tolerance.Value : Math.Max(0, Math.Min(100, cfg.Tolerance))));
            tracker.Reset(); trigger.Reset();
        }
        void Swatch(Button label, bool has, int rgb, string name, int count)
        {
            ColorSampleButton sample = (ColorSampleButton)label;
            sample.Sample = has ? (Color?)Color.FromArgb((int)(0xFF000000u | (uint)rgb)) : null;
            label.Text = name + "\n" + (has ? "#" + rgb.ToString("X6") + (count > 0 ? " · " + count + " Pixel" : "") : "Einrichten …");
            label.Invalidate();
        }
        void ShowColorWizard(bool primary)
        {
            if (calibrating) return;
            enabled.Checked = false; StopPending(); calibrating = true; Hide();
            if (lastDetection != null && lastDetection.Found)
                captureArea = new Rectangle(lastDetection.Bounds.Left + lastDetection.Bounds.Width / 2 - 64, lastDetection.Bounds.Top + lastDetection.Bounds.Height / 2 - 64, 128, 128);
            try
            {
                bool repeat;
                do
                {
                    repeat = false;
                    using (RegionCapture capture = new RegionCapture(captureArea, primary ? 1 : 2))
                    {
                        if (capture.ShowDialog() != DialogResult.OK) break;
                        captureArea = capture.Area;
                        using (Bitmap bitmap = capture.Screenshot)
                        using (PixelPicker picker = new PixelPicker(bitmap, capture.Area, primary ? 1 : 2))
                        {
                            DialogResult result = picker.ShowDialog();
                            if (result == DialogResult.Retry) { repeat = true; continue; }
                            if (result != DialogResult.OK) break;
                            if (primary)
                            {
                                cfg.FirstSamples = picker.Result; cfg.FirstColor = picker.Result[0].Rgb; cfg.HasFirst = true;
                                updatingPosition = true;
                                try { x.Value = Math.Max(x.Minimum, Math.Min(x.Maximum, picker.Result[0].X)); y.Value = Math.Max(y.Minimum, Math.Min(y.Maximum, picker.Result[0].Y)); }
                                finally { updatingPosition = false; }
                            }
                            else { cfg.SecondSamples = picker.Result; cfg.SecondColor = picker.Result[0].Rgb; cfg.HasSecond = true; }
                            RefreshSwatches(); feedback.Text = "Farbe " + (primary ? "1" : "2") + ": " + picker.Result.Count + " Pixel übernommen. Konfiguration speichern.";
                        }
                    }
                } while (repeat);
            }
            finally { calibrating = false; Show(); Activate(); }
        }
        void RegisterKeys()
        {
            bool ok = true;
            uint[] keys = { 0x75, 0x76, 0x77, 0x79, 0x78 };
            for (int i = 0; i < keys.Length; i++) if (!Native.RegisterHotKey(Handle, i + 1, 0x4000, keys[i])) ok = false;
            hotkeysReady = ok;
            recordingKeysReady = Native.RegisterHotKey(Handle, 6, 0x4000, 0x7A);
            if (!ok) feedback.Text = "Ein Hotkey ist bereits belegt. Andere Instanz / Belegung schließen und neu starten.";
            else if (!recordingKeysReady) feedback.Text = "F11 belegt. Aufnahme über die Schaltfläche bedienen.";
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0312)
            {
                int id = m.WParam.ToInt32();
                if (calibrating && id != 4) { base.WndProc(ref m); return; }
                if (id == 1 || id == 2) Calibrate(id == 1, id == 1);
                else if (id == 3) enabled.Checked = !enabled.Checked;
                else if (id == 4) PauseNow();
                else if (id == 5) Calibrate(true, false);
                else if (id == 6) ToggleRecording();
            }
            base.WndProc(ref m);
        }
        void Calibrate(bool primary, bool atCursor)
        {
            enabled.Checked = false;
            if (primary && atCursor)
            {
                Native.Point p;
                if (!Native.GetCursorPos(out p)) { feedback.Text = "Mausposition konnte nicht gelesen werden."; return; }
                x.Value = Math.Max(x.Minimum, Math.Min(x.Maximum, p.X));
                y.Value = Math.Max(y.Minimum, Math.Min(y.Maximum, p.Y));
                cfg.HasFirst = cfg.HasSecond = false;
            }
            if (!primary && !cfg.HasFirst) { feedback.Text = "Zuerst Farbe 1 mit F6 oder F9 aufnehmen."; return; }
            if (primary) { cfg.HasFirst = cfg.HasSecond = false; cfg.FirstSamples.Clear(); cfg.SecondSamples.Clear(); RefreshSwatches(); }
            IntPtr dc = Native.GetDC(IntPtr.Zero);
            int rgb = 0; bool ok = false;
            try { if (dc != IntPtr.Zero) ok = Native.ReadPixel(dc, (int)x.Value, (int)y.Value, out rgb); }
            finally { if (dc != IntPtr.Zero) Native.ReleaseDC(IntPtr.Zero, dc); }
            if (!ok) { feedback.Text = "Bildschirmpunkt nicht lesbar. Position und Fenstermodus prüfen."; return; }
            if (primary) { cfg.FirstColor = rgb; cfg.HasFirst = true; }
            else { cfg.SecondColor = rgb; cfg.HasSecond = true; cfg.SecondSamples.Clear(); }
            RefreshSwatches(); feedback.Text = (primary ? "Farbe 1" : "Farbe 2") + " aufgenommen: #" + rgb.ToString("X6") + ". Danach Konfiguration speichern.";
        }
        sealed class ForegroundIdentity
        {
            internal IntPtr Window; internal uint Process; internal long CheckedAt; internal bool IsFortnite;
        }
        static volatile ForegroundIdentity foregroundIdentity;
        internal static bool FortniteForeground()
        {
            IntPtr window = Native.GetForegroundWindow(); uint pid;
            Native.GetWindowThreadProcessId(window, out pid);
            long now = ShotVerifier.ClockMs(); ForegroundIdentity cached = foregroundIdentity;
            if (cached != null && cached.Window == window && cached.Process == pid && now - cached.CheckedAt < 500) return cached.IsFortnite;
            bool result = false;
            try
            {
                using (System.Diagnostics.Process p = System.Diagnostics.Process.GetProcessById((int)pid))
                    result = p.ProcessName.StartsWith("FortniteClient-", StringComparison.OrdinalIgnoreCase);
            }
            catch { }
            foregroundIdentity = new ForegroundIdentity { Window = window, Process = pid, CheckedAt = now, IsFortnite = result };
            return result;
        }
        void StopPending()
        {
            attemptGeneration++;
            sequence.Cancel();
        }
        void UpdateStatus(string state)
        { string text = state + "  ·  Schuss-Eingaben: " + shots; if (status.Text != text) { status.Text = text; helpTips.SetToolTip(status, text); } }
        void TickLoop()
        {
            // Windows reserves F12 for debuggers; observe its physical edge instead of registering it.
            bool markerHeld = Native.Held(0x7B);
            if (markerHeld && !markerWasHeld && recorder.Recording && !calibrating && (FortniteForeground() || Native.GetForegroundWindow() == Handle)) MarkRecording();
            markerWasHeld = markerHeld;
            if (scanClock.ElapsedMilliseconds - lastRecorderStatus >= 250) { lastRecorderStatus = scanClock.ElapsedMilliseconds; RefreshRecordingStatus(); }
            if (calibrating) return;
            if (!cfg.HasFirst) { UpdateStatus("Pausiert – zuerst Farbe 1 auswählen"); return; }
            int key = ((Choice)hold.SelectedItem).Key;
            recorder.SetControls(enabled.Checked, Native.Held(key), releaseRequired ? "Haltetaste zunächst loslassen" : trigger.Status);
            if (!FortniteForeground())
            {
                barCapture.ResetFastCapture();
                if (hadForeground) { StopPending(); trigger.Reset(); tracker.Reset(); ClearPreview(); trace.Add(scanClock.ElapsedMilliseconds, enabled.Checked, false, lastObservation, trigger.Status, "Fokusverlust"); }
                hadForeground = false; wasHeld = false; releaseRequired = true;
                UpdateStatus(enabled.Checked ? "Warte auf Fortnite" : "Pausiert"); return;
            }
            hadForeground = true;
            bool held = Native.Held(key);
            if (!held) { if (wasHeld && !sequence.ShotCommitted) StopPending(); releaseRequired = false; trigger.Reset(); }
            wasHeld = held;
            if (sequence.Running) return;
            long now = scanClock.ElapsedMilliseconds;
            int scanInterval = enabled.Checked ? (held ? 0 : 20) : 150;
            if (now - lastScan < scanInterval) return;
            lastScan = now;
            BarObservation observation;
            try
            {
                long captureStarted = Stopwatch.GetTimestamp();
                barCapture.CaptureFrame(Native.GetForegroundWindow(), fastCapture.Checked && enabled.Checked);
                long captureFinished = Stopwatch.GetTimestamp();
                // Use the captured frame's time, not the timer tick before a
                // potentially slow CopyFromScreen, for identity expiry and logs.
                now = scanClock.ElapsedMilliseconds - (ShotVerifier.ClockMs() - barCapture.CapturedAtMs);
                lastScan = now;
                shotVerifier.Prepare(barCapture.LastArea, barCapture.LastClientSize);
                shotDetector.Prepare(barCapture.Frame.Size);
                observation = tracker.Update(barCapture.Frame, barCapture.LastArea, barCapture.LastClientSize, barDetector, now, enabled.Checked && held && !releaseRequired);
                observation.FrameAppMs = now;
                observation.CaptureBackend = barCapture.CaptureBackend;
                observation.CaptureMs = (captureFinished - captureStarted) * 1000.0 / Stopwatch.Frequency;
                observation.DetectMs = (Stopwatch.GetTimestamp() - captureFinished) * 1000.0 / Stopwatch.Frequency;
            }
            catch (Exception ex)
            { StopPending(); trigger.Reset(); tracker.Reset(); releaseRequired = true; ClearPreview(); detectionStatus.Text = "Bildtest fehlgeschlagen: " + ex.Message; trace.Add(now, enabled.Checked, held, null, trigger.Status, "Capture-Fehler: " + ex.Message); UpdateStatus("Aufnahme nicht verfügbar"); return; }
            lastObservation = observation;
            if (!observation.Valid)
            { StopPending(); trigger.Reset(); tracker.Reset(); releaseRequired = true; ClearPreview(); detectionStatus.Text = observation.Reason; trace.Add(now, enabled.Checked, held, observation, trigger.Status, "Ungültige Aufnahme – kein Schuss"); UpdateStatus("Aufnahme nicht verfügbar"); return; }
            BarDetection result = new BarDetection { Found = observation.Present, Bounds = observation.Bounds, ColorNumber = observation.ColorNumber };
            lastDetection = result;
            bool visible = result.Found;
            detectionStatus.Text = observation.Reason;
            if (now - lastPreview >= 200)
            {
                lastPreview = now; Image old = barPreview.Image; barPreview.Image = null;
                frameTiming.Text = barCapture.CaptureBackend + " · Aufnahme " + observation.CaptureMs.ToString("F1") + " ms · Erkennung " + observation.DetectMs.ToString("F1") + " ms";
                frameTiming.AccessibleName = frameTiming.Text;
                if (visible)
                {
                    Rectangle local = new Rectangle(result.Bounds.X - barCapture.LastArea.X, result.Bounds.Y - barCapture.LastArea.Y, result.Bounds.Width, result.Bounds.Height);
                    local = Rectangle.Intersect(new Rectangle(Point.Empty, barCapture.Frame.Size), Rectangle.Inflate(local, 3, 8));
                    barPreview.Image = barCapture.Frame.Clone(local, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                }
                previewHint.Visible = barPreview.Image == null;
                if (old != null) old.Dispose();
            }
            if (!enabled.Checked) { trigger.Reset(); trace.Add(now, false, held, observation, trigger.Status, "Bildtest"); recorder.Observe(barCapture.Frame, barCapture.LastArea, now, observation, trigger.Status); UpdateStatus("Pausiert – Bildtest aktiv"); return; }
            if (!held) { trace.Add(now, true, false, observation, trigger.Status, "Beobachten"); recorder.Observe(barCapture.Frame, barCapture.LastArea, now, observation, trigger.Status); UpdateStatus(observation.Stable ? "Balken bestätigt – Haltetaste drücken" : "Balken wird bestätigt"); return; }
            if (releaseRequired) { trace.Add(now, true, held, observation, trigger.Status, "BLOCK: Haltetaste zunächst loslassen"); UpdateStatus("Haltetaste erst loslassen"); return; }
            if (!HudAimGuard.HudNearAim(observation.Bounds, barCapture.LastArea, barCapture.LastClientSize))
            {
                trigger.Reset();
                string block = "Balken zu weit vom Fadenkreuz – Ziel erneut anvisieren";
                trace.Add(now, true, held, observation, trigger.Status, "BLOCK: " + block);
                recorder.Observe(barCapture.Frame, barCapture.LastArea, now, observation, block); UpdateStatus(block); return;
            }
            if (HudAimGuard.RequestConfirmation(trigger, true, visible, observation.Stable, observation.Bounds, barCapture.LastArea, barCapture.LastClientSize, now))
            {
                // Recheck the target immediately before sending any input.
                if (!FortniteForeground() || !Native.Held(key)) { trigger.Reset(); releaseRequired = true; return; }
                if (Native.Held(1)) { trigger.Reject("Linke Maustaste bereits gehalten."); feedback.Text = trigger.Status; trace.Add(now, true, held, observation, trigger.Status, "LMB-Konflikt"); UpdateStatus(trigger.Status); return; }
                int selectedBuild = ((Choice)buildKey.SelectedItem).Key;
                List<int> extraKeys = new List<int>();
                if (build.Checked && extras.Checked)
                {
                    if (extraStair.Checked) extraKeys.Add(((Choice)stairKey.SelectedItem).Key);
                    if (extraFloor.Checked) extraKeys.Add(((Choice)floorKey.SelectedItem).Key);
                    if (extraKeys.Contains(key) || extraKeys.Contains(selectedBuild) || (extraKeys.Count == 2 && extraKeys[0] == extraKeys[1]))
                    { trigger.Reject("Tasten der Baufolge müssen verschieden sein."); feedback.Text = trigger.Status; return; }
                }
                IntPtr shotWindow = Native.GetForegroundWindow(); Rectangle knownHud = observation.Bounds; HudTransition targetTransition = observation.Transition; HealthBarDetector verifier = shotDetector;
                uint shotProcess; Native.GetWindowThreadProcessId(shotWindow, out shotProcess);
                Rectangle decisionArea = barCapture.LastArea; Size decisionClient = barCapture.LastClientSize;
                long firstFrameAt = barCapture.CapturedAtMs;
                long firstFrameAppMs = scanClock.ElapsedMilliseconds - (ShotVerifier.ClockMs() - firstFrameAt);
                int confirmationDelay = (int)lossDelay.Value;
                string verificationReason = "Noch nicht geprüft";
                bool resumeObservation = false;
                bool retryVerification = false;
                long restoredFrameAt = 0;
                bool checkEditGuard = build.Checked && editGuard.Checked;
                string buildGuardReason = checkEditGuard ? "Noch nicht geprüft" : "Aus";
                AdaptiveBuildTiming adaptiveTiming = null;
                if (build.Checked && adaptiveBuild.Checked)
                    try { adaptiveTiming = new AdaptiveBuildTiming(shotWindow, selectedBuild, (int)delay.Value, (int)placeDelay.Value); }
                    catch (Exception ex) { feedback.Text = "Auto nicht verfügbar, feste Zeiten: " + ex.Message; }
                long generation = ++attemptGeneration;
                if (sequence.StartDetailed(InputSequence.Plan(build.Checked, selectedBuild, adaptiveTiming == null ? (int)delay.Value : 35, adaptiveTiming == null ? (int)placeDelay.Value : 10, extraKeys.ToArray(), (int)extraDelay.Value), delegate { return Native.Held(key) && Native.ForegroundMatches(shotWindow, shotProcess); }, delegate(SequenceResult sequenceResult)
                {
                    string timingMode = adaptiveTiming == null ? "Fixed" : adaptiveTiming.Mode;
                    string timingDetails = adaptiveTiming == null ? "Feste Zeiten" : adaptiveTiming.Details;
                    long checkedFrameAt = shotVerifier.LastCheckedAtMs;
                    if (adaptiveTiming != null) adaptiveTiming.Dispose();
                    if (!IsDisposed && !Disposing && IsHandleCreated)
                        try { BeginInvoke((Action)delegate
                        {
                            if (IsDisposed) return;
                            long firstDownAppMs = sequenceResult.FirstDownTimestamp == 0 ? -1 : scanClock.ElapsedMilliseconds - (System.Diagnostics.Stopwatch.GetTimestamp() - sequenceResult.FirstDownTimestamp) * 1000 / System.Diagnostics.Stopwatch.Frequency;
                            bool retryScheduled = false;
                            bool recoveredArming = false;
                            if (sequenceResult.ShotSent) shots++;
                            if (generation == attemptGeneration)
                            {
                                if (sequenceResult.ShotSent) trigger.ConfirmSent(firstDownAppMs);
                                else if (retryVerification) retryScheduled = trigger.RetryFresh(scanClock.ElapsedMilliseconds);
                                else if (resumeObservation) recoveredArming = trigger.ResumeAfterRestoredHud(restoredFrameAt, ShotVerifier.ClockMs(), enabled.Checked && !releaseRequired && Native.Held(key) && Native.ForegroundMatches(shotWindow, shotProcess));
                                else trigger.Reject(sequenceResult.Message);
                            }
                            feedback.Text = sequenceResult.BuildSuppressed ? sequenceResult.Message : sequenceResult.Outcome == "Completed" ? (sequenceResult.BuildSent && sequenceResult.PlaceSent ? "Schuss und " + sequenceResult.PlacementCount + " Platzierungsklick(s) gesendet. Im Spiel nicht bestätigt." : "Schusseingabe gesendet.") : recoveredArming ? "Balken war wieder sichtbar. Der nächste Verlust wird ohne erneutes Drücken geprüft." : retryScheduled ? "Die Bildprüfung war zu langsam. Ein neues Bild wird geprüft; du musst dafür nicht erneut drücken." : retryVerification ? "Die Bildprüfung blieb zu langsam. Den Lebensbalken kurz erneut anvisieren." : sequenceResult.Message + (sequenceResult.ShotSent ? " Schusseingabe wurde gesendet." : " Keine Schusseingabe gesendet.");
                            double buildGap = sequenceResult.BuildSent ? (sequenceResult.BuildDownTimestamp - sequenceResult.FirstDownTimestamp) * 1000.0 / System.Diagnostics.Stopwatch.Frequency : -1;
                            if (generation == attemptGeneration && (resumeObservation || sequenceResult.ShotSent) && checkedFrameAt > 0 && recorder.Recording && Native.ForegroundMatches(shotWindow, shotProcess))
                            {
                                Rectangle restoredArea;
                                using (Bitmap restoredImage = shotVerifier.CopyRestoredContext(checkedFrameAt, knownHud, out restoredArea))
                                    if (restoredImage != null) recorder.CaptureDetection(restoredImage, restoredArea, scanClock.ElapsedMilliseconds - (ShotVerifier.ClockMs() - checkedFrameAt), (sequenceResult.ShotSent ? "Pruefbild der gesendeten Schusseingabe" : "Nachpruefung: Balken wieder sichtbar") + "; Bereich um bisheriges Ziel; RecoveredArming=" + recoveredArming + "; " + verificationReason);
                            }
                            double placeGap = sequenceResult.PlaceSent ? (sequenceResult.PlaceDownTimestamp - sequenceResult.BuildDownTimestamp) * 1000.0 / System.Diagnostics.Stopwatch.Frequency : -1;
                            trace.Add(scanClock.ElapsedMilliseconds, enabled.Checked, wasHeld, lastObservation, trigger.Status, sequenceResult.Outcome + "; ShotSent=" + sequenceResult.ShotSent + "; BuildSent=" + sequenceResult.BuildSent + "; PlaceSent=" + sequenceResult.PlaceSent + "; PlacementCount=" + sequenceResult.PlacementCount + "; BuildSuppressed=" + sequenceResult.BuildSuppressed + "; EditGuard=" + buildGuardReason + "; TimingMode=" + timingMode + "; TimingDetails=" + timingDetails + "; ActualBuildGapMs=" + buildGap.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "; ActualPlaceGapMs=" + placeGap.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "; BuildDownTimestamp=" + sequenceResult.BuildDownTimestamp + "; PlaceDownTimestamp=" + sequenceResult.PlaceDownTimestamp + "; Steps=" + sequenceResult.Steps + "; FirstDownAppMs=" + firstDownAppMs + "; FirstMissingFrameAppMs=" + firstFrameAppMs + "; ResumeObservation=" + resumeObservation + "; RecoveredArming=" + recoveredArming + "; RestoredFrameAgeMs=" + (restoredFrameAt <= 0 ? -1 : ShotVerifier.ClockMs() - restoredFrameAt) + "; RetryVerification=" + retryVerification + "; Preflight=" + verificationReason + "; Generation=" + generation + "; Active=" + attemptGeneration);
                            UpdateStatus(enabled.Checked ? trigger.Status : "Pausiert");
                        }); } catch (InvalidOperationException) { }
                }, delegate {
                    if (adaptiveTiming != null) adaptiveTiming.Prepare();
                    return shotVerifier.Check(shotWindow, shotProcess, knownHud, verifier, decisionArea, decisionClient, firstFrameAt, confirmationDelay, key, out verificationReason, out resumeObservation, out retryVerification, out restoredFrameAt, targetTransition);
                }, delegate
                {
                    if (!IsDisposed && IsHandleCreated) try { BeginInvoke((Action)delegate { if (!IsDisposed && lastScan == now) recorder.CaptureDetection(barCapture.Frame, barCapture.LastArea, now, "Schussentscheidung vor gesendeter Eingabe: " + observation.Reason); }); } catch (InvalidOperationException) { }
                }, delegate { return Native.ForegroundMatches(shotWindow, shotProcess); }, delegate(InputStep step, SequenceResult resultState, Func<bool> allowed)
                {
                    if (adaptiveTiming != null) adaptiveTiming.BeforeStep(step, resultState, allowed);
                    if (checkEditGuard && resultState.ShotSent && !resultState.BuildSent && step.Down && step.Key == selectedBuild)
                    {
                        if (!allowed()) throw new OperationCanceledException();
                        if (!shotVerifier.CheckBuild(shotWindow, decisionArea, decisionClient, knownHud, verifier, out buildGuardReason, targetTransition)) throw new BuildSuppressedException(buildGuardReason);
                    }
                })) { feedback.Text = "Aktueller Balkenverlust wird vor der Eingabe erneut geprüft …"; }
                else { if (adaptiveTiming != null) adaptiveTiming.Dispose(); trigger.Reject("Eine Eingabefolge läuft bereits."); }
            }
            trace.Add(now, true, held, observation, trigger.Status, trigger.AwaitingResult ? "Pending" : "Verfolgen");
            if (!sequence.Running) recorder.Observe(barCapture.Frame, barCapture.LastArea, now, observation, trigger.Status);
            UpdateStatus(trigger.Status);
        }
        void Save()
        {
            cfg.X = (int)x.Value; cfg.Y = (int)y.Value; cfg.Tolerance = (int)tolerance.Value;
            cfg.HoldKey = ((Choice)hold.SelectedItem).Key; cfg.BuildAfter = build.Checked;
            cfg.BuildKey = ((Choice)buildKey.SelectedItem).Key; cfg.BuildDelay = (int)delay.Value;
            cfg.PlaceDelayMs = (int)placeDelay.Value;
            cfg.AdaptiveBuildAfter = adaptiveBuild.Checked;
            cfg.EditBuildGuard = editGuard.Checked;
            cfg.FastCapture = fastCapture.Checked;
            cfg.ExtraPlacements = extras.Checked; cfg.ExtraStair = extraStair.Checked; cfg.ExtraFloor = extraFloor.Checked;
            cfg.StairKey = ((Choice)stairKey.SelectedItem).Key; cfg.FloorKey = ((Choice)floorKey.SelectedItem).Key; cfg.ExtraDelayMs = (int)extraDelay.Value;
            cfg.LossDelayMs = (int)lossDelay.Value;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
                string temp = ConfigPath + ".tmp";
                using (FileStream stream = File.Create(temp)) new XmlSerializer(typeof(Config)).Serialize(stream, cfg);
                if (File.Exists(ConfigPath)) File.Replace(temp, ConfigPath, null); else File.Move(temp, ConfigPath);
                feedback.Text = "Konfiguration gespeichert.";
            }
            catch (Exception ex) { feedback.Text = "Speichern fehlgeschlagen: " + ex.Message; }
        }
    }

    internal static class Program
    {
        [STAThread] static void Main(string[] args)
        {
            Native.SetProcessDPIAware(); Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length == 3 && args[0] == "--buildguard-probe") { try { if (!HudReplay.GuardProbe(args[1], args[2])) Environment.ExitCode = 1; } catch (Exception ex) { File.WriteAllText(args[2], "FAIL: " + ex); Environment.ExitCode = 1; } return; }
            if (args.Length == 3 && (args[0] == "--hud-probe" || args[0] == "--support-probe")) { try { if (!HudReplay.Probe(args[1], args[2], args[0] == "--support-probe")) Environment.ExitCode = 1; } catch (Exception ex) { File.WriteAllText(args[2], "FAIL: " + ex); Environment.ExitCode = 1; } return; }
            if (args.Length == 3 && args[0] == "--tracker-probe") { try { if (!HudReplay.Track(args[1], args[2])) Environment.ExitCode = 1; } catch (Exception ex) { File.WriteAllText(args[2], "FAIL: " + ex); Environment.ExitCode = 1; } return; }
            if (args.Length == 3 && args[0] == "--feedback-probe") { try { if (!AdaptiveTimingTests.Probe(args[1], args[2])) Environment.ExitCode = 1; } catch (Exception ex) { File.WriteAllText(args[2], "FAIL: " + ex); Environment.ExitCode = 1; } return; }
            if (args.Length == 2 && args[0] == "--recorder-test")
            {
                try { RecorderTests.Run(args[1]); File.WriteAllText(Path.Combine(args[1], "result.txt"), "PASS: continuous fixture video, timing with delayed frames, neutral focus-pause, CSV diagnosis, marker, full-resolution snapshot, clean stop, restart and finalized video on capture failure. No game input or screen capture used."); }
                catch (Exception ex) { Directory.CreateDirectory(args[1]); File.WriteAllText(Path.Combine(args[1], "result.txt"), "FAIL: " + ex); Environment.ExitCode = 1; }
                return;
            }
            if (args.Length == 2 && args[0] == "--watch-diagnose")
            {
                try
                {
                    Config config = LoadConfig(); HealthBarDetector detector = new HealthBarDetector(config, config.Tolerance);
                    File.WriteAllText(args[1], "WAIT: readonly capture test; waiting for Fortnite in foreground. No inputs sent.");
                    using (HealthBarCapture capture = new HealthBarCapture())
                    {
                        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew(); bool sawGame = false;
                        while (watch.ElapsedMilliseconds < 20000)
                        {
                            if (MainForm.FortniteForeground())
                            {
                                sawGame = true; BarDetection result = capture.Read(Native.GetForegroundWindow(), detector);
                                if (result.Found)
                                {
                                    File.WriteAllText(args[1], "PASS: live foreground Fortnite screen capture; Color=" + result.ColorNumber + "; Bounds=" + result.Bounds + "; Search=" + capture.LastArea + ". No inputs sent.");
                                    return;
                                }
                            }
                            System.Threading.Thread.Sleep(100);
                        }
                        File.WriteAllText(args[1], sawGame ? "No matching bar found in live capture. No inputs sent." : "Not tested: Fortnite was not in the foreground during the test window. No inputs sent.");
                        Environment.ExitCode = 2;
                    }
                }
                catch (Exception ex) { File.WriteAllText(args[1], "FAIL: " + ex); Environment.ExitCode = 1; }
                return;
            }
            if (args.Length == 3 && args[0] == "--detect-image")
            {
                try
                {
                    Config config = LoadConfig(); HealthBarDetector detector = new HealthBarDetector(config, config.Tolerance);
                    using (Bitmap full = new Bitmap(args[1]))
                    {
                        Rectangle area = HealthBarDetector.SearchArea(new Rectangle(Point.Empty, full.Size));
                        using (Bitmap crop = full.Clone(area, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                        {
                            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
                            BarDetection result = detector.Find(crop, area, full.Size);
                            File.WriteAllText(args[2], "Found=" + result.Found + "; Color=" + result.ColorNumber + "; Bounds=" + result.Bounds + "; Search=" + area + "; ScanMs=" + watch.Elapsed.TotalMilliseconds.ToString("F1"));
                            if (result.Found)
                            {
                                HealthBarTracker imageTracker = new HealthBarTracker(); PrefireGate imageGate = new PrefireGate();
                                BarObservation observed = null;
                                foreach (int now in new int[] { 0, 40, 80 })
                                { observed = imageTracker.Update(crop, area, full.Size, detector, now); imageGate.Step(true, observed.Present, observed.Stable, now); }
                                Check(observed.Present && observed.Stable, "Captured game HUD is acquired across frames");
                                File.AppendAllText(args[2], "; TrackedStable=True");
                                using (Bitmap bar = full.Clone(result.Bounds, System.Drawing.Imaging.PixelFormat.Format32bppArgb)) bar.Save(args[2] + ".png");
                                Rectangle erase = Rectangle.Intersect(new Rectangle(Point.Empty, full.Size), Rectangle.Inflate(result.Bounds, 7, 18));
                                Rectangle source = new Rectangle(erase.X, Math.Min(full.Height - erase.Height, erase.Bottom + 40), erase.Width, erase.Height);
                                using (Bitmap patch = full.Clone(source, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                                using (Graphics graphics = Graphics.FromImage(full)) graphics.DrawImageUnscaled(patch, erase.Location);
                                using (Bitmap removed = full.Clone(area, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                                {
                                    BarDetection negative = detector.Find(removed, area, full.Size);
                                    File.AppendAllText(args[2], "; AfterSyntheticBarRemoval=" + negative.Found + (negative.Found ? "; OtherBounds=" + negative.Bounds : ""));
                                    if (negative.Found) { Environment.ExitCode = 1; return; }
                                    foreach (int now in new int[] { 100, 120, 140 })
                                    { observed = imageTracker.Update(removed, area, full.Size, detector, now); Check(!observed.Present, "Captured game HUD loss"); Check(!imageGate.Step(true, observed.Present, observed.Stable, now), "Do not trigger before debounce"); }
                                    observed = imageTracker.Update(removed, area, full.Size, detector, 160);
                                    Check(imageGate.Step(true, observed.Present, observed.Stable, 160), "Captured game HUD loss reaches pending");
                                    File.AppendAllText(args[2], "; ConfirmedLossPending=True; NoInputSent=True");
                                }
                            }
                            Environment.ExitCode = result.Found ? 0 : 1;
                        }
                    }
                }
                catch (Exception ex) { File.WriteAllText(args[2], "FAIL: " + ex); Environment.ExitCode = 1; }
                return;
            }
            if (args.Length == 2 && args[0] == "--detector-test")
            {
                try { TestDetector(); File.WriteAllText(args[1], "PASS: green/orange bars, shifted positions, gradient and white health text, empty frame, thick surfaces, thin outlines, noise and HUD search bounds."); }
                catch (Exception ex) { File.WriteAllText(args[1], "FAIL: " + ex); Environment.ExitCode = 1; }
                return;
            }
            if (args.Length == 2 && args[0] == "--self-test")
            {
                try { SelfTest(); File.WriteAllText(args[1], "PASS V27: restored-HUD recovery without an intervening visible timer frame, fresh-proof age/focus/cooldown guards and expired-target veto; target handoff across rear HUD, distinct-frame target recovery veto, guarded building after target loss, camera movement and same-position health recovery; 62ms stale frame discarded and distinct fast frame retried; restored wall vetoes retry; bounded same-hold recovery and expiry; optional edit guard keeps shot and suppresses both later downs with key cleanup; visible/damaged/black guard images; config compatibility and persistence; previous fixed/auto timing, focus/cancellation, material tracking, picker and debounce tests. All inputs mocked; no live placement confirmation."); }
                catch (Exception ex) { File.WriteAllText(args[1], "FAIL: " + ex); Environment.ExitCode = 1; }
                return;
            }
            if (args.Length == 2 && args[0] == "--preview")
            {
                using (MainForm form = new MainForm(new Config(), true))
                { form.Show(); Application.DoEvents(); using (Bitmap bmp = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height)); bmp.Save(args[1]); } }
                return;
            }
            if (args.Length == 2 && args[0] == "--ui-review")
            {
                try { using (MainForm form = new MainForm(new Config { BuildAfter = true, HoldKey = 17 }, true)) form.ReviewInterface(args[1]); }
                catch (Exception ex) { Directory.CreateDirectory(args[1]); File.WriteAllText(Path.Combine(args[1], "failure.txt"), ex.ToString()); Environment.ExitCode = 1; }
                return;
            }
            if (args.Length == 2 && args[0] == "--picker-preview")
            {
                using (Bitmap bitmap = new Bitmap(128, 128))
                {
                    using (Graphics g = Graphics.FromImage(bitmap)) { g.Clear(Color.SlateGray); g.FillRectangle(Brushes.LimeGreen, 15, 50, 100, 15); g.FillRectangle(Brushes.DarkGreen, 15, 65, 100, 3); }
                    using (PixelPicker picker = new PixelPicker(bitmap, new Rectangle(500, 300, 128, 128), 1))
                    { picker.Show(); picker.Canvas.Toggle(new Point(30, 55)); Application.DoEvents(); using (Bitmap image = new Bitmap(picker.Width, picker.Height)) { picker.DrawToBitmap(image, new Rectangle(0, 0, picker.Width, picker.Height)); image.Save(args[1]); } }
                }
                return;
            }
            if (args.Length == 2 && args[0] == "--capture-test")
            {
                try
                {
                    using (Form fixture = new Form())
                    {
                        fixture.FormBorderStyle = FormBorderStyle.None; fixture.BackColor = Color.FromArgb(24, 180, 74);
                        fixture.Bounds = new Rectangle(Screen.PrimaryScreen.WorkingArea.Left + 100, Screen.PrimaryScreen.WorkingArea.Top + 100, 300, 300);
                        fixture.TopMost = true; fixture.Show(); Application.DoEvents();
                        Rectangle area = new Rectangle(fixture.Left + 40, fixture.Top + 40, 128, 128);
                        using (RegionCapture capture = new RegionCapture(area, 1))
                        {
                            capture.Shown += delegate { capture.BeginCapture(); };
                            Check(capture.ShowDialog() == DialogResult.OK, "Capture dialog completes");
                            using (Bitmap screenshot = capture.Screenshot)
                            {
                                int expected = fixture.BackColor.ToArgb() & 0xFFFFFF;
                                Check(screenshot.Size == new Size(128, 128), "Capture dimensions");
                                Check((screenshot.GetPixel(64, 64).ToArgb() & 0xFFFFFF) == expected, "Capture center pixel without overlay");
                                Check((screenshot.GetPixel(0, 0).ToArgb() & 0xFFFFFF) == expected && (screenshot.GetPixel(127, 127).ToArgb() & 0xFFFFFF) == expected, "Capture excludes red frame");
                            }
                        }
                    }
                    File.WriteAllText(args[1], "PASS: live capture countdown, 128x128 screenshot, center and corner colors, overlay excluded (own test window).");
                }
                catch (Exception ex) { File.WriteAllText(args[1], "FAIL: " + ex); Environment.ExitCode = 1; }
                return;
            }
            if (args.Length == 2 && (args[0] == "--startup-test" || args[0] == "--startup-test-clean"))
            {
                using (MainForm form = new MainForm(args[0] == "--startup-test-clean" ? new Config() : LoadConfig(), false))
                using (Timer testTimer = new Timer())
                {
                    testTimer.Interval = 250;
                    testTimer.Tick += delegate
                    {
                        testTimer.Stop();
                        bool ready = form.StartupReady;
                        File.WriteAllText(args[1], ready ? "PASS V27: GUI starts paused, fixed/auto controls consistent, key selections exist, F6/F7/F8/F9/F10/F11 registered; no mouse-motion prerequisite. F12 marker uses physical edge polling." : "FAIL: startup / hotkey / timing UI availability.");
                        Environment.ExitCode = ready ? 0 : 1; form.Close();
                    };
                    form.Shown += delegate { testTimer.Start(); };
                    Application.Run(form);
                }
                return;
            }
            bool firstInstance;
            using (System.Threading.Mutex mutex = new System.Threading.Mutex(true, "Local\\PrefireMacro.Desktop", out firstInstance))
            {
                if (!firstInstance) { MessageBox.Show("Prefire Macro läuft bereits.", "Prefire Macro"); return; }
                Config config = new Config(); string warning = null;
                try { if (File.Exists(MainForm.ConfigPath)) using (FileStream s = File.OpenRead(MainForm.ConfigPath)) config = (Config)new XmlSerializer(typeof(Config)).Deserialize(s); }
                catch { warning = "Die gespeicherte Konfiguration ist nicht lesbar. Standardwerte werden verwendet."; }
                if (warning != null) MessageBox.Show(warning, "Prefire Macro");
                MainForm main = new MainForm(config ?? new Config(), false); main.RecordOnStart = args.Length == 1 && args[0] == "--record"; Application.Run(main);
            }
        }
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        static Config LoadConfig()
        {
            try { if (File.Exists(MainForm.ConfigPath)) using (FileStream file = File.OpenRead(MainForm.ConfigPath)) return (Config)new XmlSerializer(typeof(Config)).Deserialize(file) ?? new Config(); }
            catch { }
            return new Config();
        }
        static void TestDetector()
        {
            Config config = new Config(); config.HasFirst = config.HasSecond = true; config.FirstColor = 0x5CC442; config.SecondColor = 0xDA943D;
            config.FirstSamples.Add(new PixelSample(243, 255, config.FirstColor)); config.SecondSamples.Add(new PixelSample(206, 252, config.SecondColor));
            HealthBarDetector detector = new HealthBarDetector(config, 25);
            Size client = new Size(1920, 1080); Rectangle area = HealthBarDetector.SearchArea(new Rectangle(Point.Empty, client));
            using (Bitmap bitmap = new Bitmap(area.Width, area.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.FromArgb(65, 55, 48));
                Check(!detector.Find(bitmap, area, client).Found, "Empty frame");
                using (System.Drawing.Drawing2D.LinearGradientBrush brush = new System.Drawing.Drawing2D.LinearGradientBrush(new Point(450, 0), new Point(570, 0), Color.FromArgb(92, 196, 66), Color.FromArgb(145, 215, 75))) graphics.FillRectangle(brush, 450, 300, 120, 12);
                graphics.DrawString("150 150", SystemFonts.DefaultFont, Brushes.White, 453, 298);
                BarDetection full = detector.Find(bitmap, area, client);
                Check(full.Found && full.ColorNumber == 1 && full.Bounds.Y >= area.Y + 299 && full.Bounds.Y <= area.Y + 303, "Gradient green bar with white numbers");
                graphics.Clear(Color.FromArgb(65, 55, 48)); graphics.FillRectangle(Brushes.LimeGreen, 90, 90, 110, 12);
                BarDetection shifted = detector.Find(bitmap, area, client);
                Check(shifted.Found && shifted.Bounds.X == area.X + 90, "Moved bar ignores stored pixel coordinates");
                graphics.Clear(Color.FromArgb(65, 55, 48)); using (Brush orange = new SolidBrush(Color.FromArgb(218, 148, 61))) graphics.FillRectangle(orange, 450, 300, 55, 12);
                Check(detector.Find(bitmap, area, client).Found && detector.Find(bitmap, area, client).ColorNumber == 2, "Damaged orange bar");
                graphics.DrawString("75 150", SystemFonts.DefaultFont, Brushes.White, 453, 298);
                Check(detector.Find(bitmap, area, client).Found && detector.Find(bitmap, area, client).ColorNumber == 2, "Damaged orange bar with white health numbers");
                graphics.Clear(Color.FromArgb(65, 55, 48)); graphics.FillRectangle(Brushes.LimeGreen, 100, 100, 240, 130);
                Check(!detector.Find(bitmap, area, client).Found, "Reject large colored surface");
                graphics.Clear(Color.FromArgb(65, 55, 48)); graphics.FillRectangle(Brushes.LimeGreen, 100, 100, 150, 2);
                Check(!detector.Find(bitmap, area, client).Found, "Reject thin build outline");
                graphics.Clear(Color.FromArgb(65, 55, 48)); graphics.FillRectangle(Brushes.LimeGreen, 100, 100, 15, 15);
                Check(!detector.Find(bitmap, area, client).Found, "Reject isolated colored detail");
                graphics.Clear(Color.FromArgb(65, 55, 48)); Random random = new Random(174);
                for (int i = 0; i < 12000; i++) bitmap.SetPixel(random.Next(bitmap.Width), random.Next(bitmap.Height), Color.FromArgb(218, random.Next(110, 180), random.Next(40, 100)));
                Check(!detector.Find(bitmap, area, client).Found, "Reject orange texture noise");
            }
            Rectangle movedClient = new Rectangle(-1920, 80, 1920, 1080);
            Check(HealthBarDetector.SearchArea(movedClient).Location == new Point(-1824, 242), "Expanded search follows client origin");
        }
        static void SelfTest()
        {
            GateSequenceTests.Run(); BarTrackerTests.Run(); MaterialHudTests.Run(); AdaptiveTimingTests.Run();
            Check(!new Config().FastCapture, "GPU capture is opt-in for old and new configurations");
            Check(!DesktopFrameCapture.RecentPresentation(0, 1000, 0), "Pointer-only update cannot supply a desktop frame");
            Check(!DesktopFrameCapture.RecentPresentation(990, 1000, 990), "Previously delivered frame cannot be relabelled fresh");
            Check(!DesktopFrameCapture.RecentPresentation(987, 1000, 980), "13ms desktop image is too old");
            Check(!DesktopFrameCapture.RecentPresentation(1001, 1000, 980), "Future presentation clock rejected");
            Check(DesktopFrameCapture.RecentPresentation(988, 1000, 987), "12ms presentation accepted with forward timeline");
            Check(Trigger.Matches(0x123456, 0x123456, 0), "Exact color");
            Check(Trigger.Matches(0x203040, 0x213141, 1), "Tolerance boundary");
            Check(!Trigger.Matches(0x203040, 0x223141, 1), "Reject out of tolerance");
            Check(!Trigger.Matches(0xFF0000, 0x0000FF, 25), "RGB channel distinction");
            Config c = new Config(); c.HasFirst = true; c.FirstColor = 0xABCDEF; c.X = -200;
            c.FirstSamples.Add(new PixelSample(-200, 140, 0xABCDEF)); c.SecondSamples.Add(new PixelSample(-190, 140, 0xFFA020)); c.BuildKey = 5;
            c.AdaptiveBuildAfter = true; c.PlaceDelayMs = 15; c.EditBuildGuard = true; c.FastCapture = true;
            c.ExtraPlacements = true; c.ExtraFloor = true; c.StairKey = 6; c.FloorKey = 67; c.ExtraDelayMs = 45;
            using (StringReader legacy = new StringReader("<Config><BuildAfter>true</BuildAfter><BuildDelay>35</BuildDelay></Config>"))
            {
                Config old = (Config)new XmlSerializer(typeof(Config)).Deserialize(legacy);
                Check(old.BuildAfter && old.BuildDelay == 35 && !old.EditBuildGuard && !old.ExtraPlacements && !old.FastCapture, "legacy settings retain build timing and leave new options off");
            }
            using (MemoryStream stream = new MemoryStream())
            {
                XmlSerializer serializer = new XmlSerializer(typeof(Config)); serializer.Serialize(stream, c); stream.Position = 0;
                Config read = (Config)serializer.Deserialize(stream);
                Check(read.HasFirst && read.FirstColor == c.FirstColor && read.X == -200, "Config roundtrip");
                Check(read.FirstSamples[0].X == -200 && read.SecondSamples[0].Rgb == 0xFFA020 && read.BuildKey == 5, "Sample lists and mouse button config");
                Check(read.AdaptiveBuildAfter && read.PlaceDelayMs == 15 && read.EditBuildGuard && read.FastCapture, "Adaptive mode, fixed placement delay, GPU capture and optional edit guard persist together");
                Check(read.ExtraPlacements && read.ExtraFloor && read.ExtraStair && read.StairKey == 6 && read.FloorKey == 67 && read.ExtraDelayMs == 45, "extra placement options, keys and timing persist");
            }
            foreach (bool autoTiming in new bool[] { false, true })
                using (MainForm form = new MainForm(new Config { BuildAfter = true, AdaptiveBuildAfter = autoTiming, HoldKey = 17, BuildKey = 5 }, true))
                    Check(form.TimingControlsValid, "Fixed/auto controls are consistent at startup");
            Check(Marshal.SizeOf(typeof(Native.Input)) == (IntPtr.Size == 8 ? 40 : 28), "Win32 INPUT size");
            Check(Native.MakeButton(5, true).data.mouse.dwFlags == 128 && Native.MakeButton(5, true).data.mouse.mouseData == 1, "XButton1 down");
            Check(Native.MakeButton(5, false).data.mouse.dwFlags == 256 && Native.MakeButton(6, false).data.mouse.mouseData == 2, "Side mouse button up");
            Check(Native.MakeButton(0x51, true).type == 1 && Native.MakeButton(0x51, true).data.key.wVk == 0x51, "Keyboard remains supported");
            Check(PixelCanvas.Map(new Point(19, 27), 4, new Size(128, 128)) == new Point(4, 6), "Zoom pixel mapping");
            using (Bitmap bitmap = new Bitmap(128, 128))
            using (PixelPicker picker = new PixelPicker(bitmap, new Rectangle(-300, 100, 128, 128), 1))
            {
                bitmap.SetPixel(5, 7, Color.Orange); picker.Canvas.Toggle(new Point(5, 7)); picker.CommitSelection();
                Check(picker.Result.Count == 1 && picker.Result[0].X == -295 && picker.Result[0].Y == 107 && picker.Result[0].Rgb == (Color.Orange.ToArgb() & 0xFFFFFF), "Screenshot color and absolute pixel position");
            }
            using (Bitmap bitmap = new Bitmap(128, 128))
            using (PixelCanvas canvas = new PixelCanvas(bitmap))
            {
                canvas.Toggle(new Point(1, 1)); canvas.Toggle(new Point(1, 1)); Check(canvas.Selected.Count == 0, "Deselect pixel");
                for (int i = 0; i < 40; i++) canvas.Toggle(new Point(i, 3)); Check(canvas.Selected.Count == 32, "Selection bound");
            }
            TestSequence();
        }
        static void TestSequence()
        {
            List<InputStep> plan = InputSequence.Plan(true, 5, 70);
            Check(plan.Count == 6 && plan[2].Key == 5 && plan[2].Down && plan[3].Key == 1 && plan[3].Down && plan[3].Milliseconds - plan[2].Milliseconds == 10, "Build then left click 10 ms later");
            List<int> keys = new List<int>(); List<bool> downs = new List<bool>(); List<double> times = new List<double>();
            HashSet<int> pressed = new HashSet<int>(); System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
            using (System.Threading.ManualResetEvent done = new System.Threading.ManualResetEvent(false))
            using (InputSequence runner = new InputSequence(delegate(int key, bool down) { keys.Add(key); downs.Add(down); times.Add(watch.Elapsed.TotalMilliseconds); if (down) pressed.Add(key); else pressed.Remove(key); return true; }, delegate(int key) { return pressed.Contains(key); }))
            {
                Check(runner.Start(plan, delegate { return true; }, delegate { done.Set(); }), "Start mock sequence");
                Check(!runner.Start(plan, delegate { return true; }, null), "Do not overlap sequences");
                Check(done.WaitOne(2500), "Mock sequence completes");
                Check(keys.Count == 6 && keys[2] == 5 && keys[3] == 1 && downs[3] && times[3] - times[2] >= 9.5 && pressed.Count == 0, "Mock order, minimum delay and release");
            }
            using (System.Threading.ManualResetEvent started = new System.Threading.ManualResetEvent(false))
            using (System.Threading.ManualResetEvent done = new System.Threading.ManualResetEvent(false))
            using (InputSequence runner = new InputSequence(delegate(int key, bool down) { if (down) { pressed.Add(key); started.Set(); } else pressed.Remove(key); return true; }, delegate { return false; }))
            {
                runner.Start(plan, delegate { return true; }, delegate { done.Set(); });
                Check(started.WaitOne(1000), "Cancellation starts"); runner.Cancel();
                Check(done.WaitOne(1000) && pressed.Count == 0, "Cancellation releases held inputs");
            }
            int sent = 0;
            using (System.Threading.ManualResetEvent done = new System.Threading.ManualResetEvent(false))
            using (InputSequence runner = new InputSequence(delegate { sent++; return true; }, delegate { return false; }))
            {
                runner.Start(plan, delegate { return false; }, delegate { done.Set(); });
                Check(done.WaitOne(1000) && sent == 0, "No inputs without focus/hold authorization");
            }
        }
    }
}
