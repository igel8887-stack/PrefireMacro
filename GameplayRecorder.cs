using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Xml.Serialization;

namespace PrefireMacro
{
    // Video encoding and file writes run on their own worker; no changes to the trigger's decision.
    internal sealed class GameplayRecorder : IDisposable
    {
        const int Fps = 30, Width = 1280, Height = 720;
        readonly object sync = new object();
        readonly Queue<string> events = new Queue<string>();
        readonly Queue<Snapshot> crops = new Queue<Snapshot>();
        readonly ManualResetEvent stop = new ManualResetEvent(false);
        Thread worker;
        Func<long> clock;
        Func<Bitmap> testCapture;
        long origin, lastCrop = -1000, lastContext = -1000;
        bool lastPresent, lastRaw, hasObservation;
        string state = "Noch keine Erkennung", status = "Aufnahme aus";
        string controls = "PAUSIERT | HALTETASTE=NEIN";
        bool macroEnabled, holdPressed;
        Rectangle target;
        string directory;
        int droppedEvents, droppedCrops;
        internal bool Recording { get { return worker != null && worker.IsAlive; } }
        internal string DirectoryPath { get { return directory; } }
        internal string StatusText { get { lock (sync) return status; } }
        sealed class Snapshot { internal Bitmap Image; internal long Now; internal string Reason; internal Rectangle Area; }
        internal void Start(string root, Func<long> getClock, Config config) { Start(root, getClock, config, null); }
        internal void Start(string root, Func<long> getClock, Config config, Func<Bitmap> fixture)
        {
            if (Recording) throw new InvalidOperationException("Aufnahme läuft oder wird abgeschlossen.");
            clock = getClock; origin = clock(); testCapture = fixture;
            directory = Path.Combine(root, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
            Directory.CreateDirectory(directory); Directory.CreateDirectory(Path.Combine(directory, "Bildausschnitte"));
            using (FileStream file = File.Create(Path.Combine(directory, "config-at-start.xml"))) new XmlSerializer(typeof(Config)).Serialize(file, config);
            File.WriteAllText(Path.Combine(directory, "Sitzung.txt"),
                "StartUTC=" + DateTime.UtcNow.ToString("o") + "\r\nAppVersion=" + typeof(GameplayRecorder).Assembly.GetName().Version + "\r\nAppPath=" + typeof(GameplayRecorder).Assembly.Location + "\r\nAppClockOriginMs=" + origin +
                "\r\nVideo: 1280x720, Ziel 30 fps, MJPEG-AVI, ohne Ton.\r\n" +
                "Nur Fortnite im Vordergrund; sonst neutrale Pause. Keine Desktopaufnahme beim Fensterwechsel.\r\n" +
                "Videozeit = app_ms minus AppClockOriginMs. events.csv enthält alle Diagnosen ab Aufnahmestart.\r\n" +
                "Dateinamen in Bildausschnitte verwenden video_ms; zum Vergleich mit events.csv das Feld app_ms aus Bildausschnitte.csv verwenden.\r\n" +
                "frames.csv ordnet Video-Frames dem Aufnahmezeitpunkt zu; duplicate=True ist ein wiederholtes Bild bei Verzögerung.\r\n" +
                "Bildausschnitte zeigen den Erkennungsbereich bei Zustandswechseln und Schussentscheidungen in voller Auflösung.\r\n" +
                "Große Übersichtsbilder: JPEG Qualität 92; kleine HUD-Kontexte: PNG. Bei AKTIV und gehaltener Taste höchstens alle 40 ms, sonst alle 500 ms; Warteschlange begrenzt, verworfene Bilder werden gezählt.\r\n" +
                "Video und frames.csv enthalten AKTIV/PAUSIERT und Haltetaste. Marker in events.csv enthalten den aktuellen Bedienzustand.\r\n" +
                "F11 Start/Stopp, F12 Fehlermarkierung. Video wird bei 5 Minuten oder 512 MiB geteilt.\r\n" +
                "Automatischer Stopp unter 2 GiB freiem Speicher. Kein Upload.\r\n", new UTF8Encoding(true));
            lock (sync)
            {
                events.Clear(); while (crops.Count > 0) crops.Dequeue().Image.Dispose();
                droppedEvents = droppedCrops = 0; state = "Noch keine Erkennung"; target = Rectangle.Empty;
                hasObservation = false; lastCrop = lastContext = -1000; status = "Aufnahme läuft – F11 stoppt, F12 markiert";
            }
            stop.Reset(); worker = new Thread(Run); worker.IsBackground = true; worker.Name = "Prefire gameplay recorder"; worker.Priority = ThreadPriority.BelowNormal; worker.Start();
        }
        internal void Log(string csvRow)
        {
            if (!Recording) return;
            lock (sync) { if (events.Count >= 12000) { droppedEvents++; return; } events.Enqueue(csvRow); }
        }
        internal void SetControls(bool enabled, bool held, string reason)
        {
            lock (sync) { macroEnabled = enabled; holdPressed = held; controls = (enabled ? "AKTIV" : "PAUSIERT") + " | HALTETASTE=" + (held ? "JA" : "NEIN") + " | " + reason; }
        }
        internal void Marker(string text)
        {
            if (!Recording) return;
            Log(clock().ToString(CultureInfo.InvariantCulture) + ",,,,,,,,,,,\"MARKER: " + text.Replace("\"", "\"\"") + "\",,,,,");
        }
        internal void Observe(Bitmap frame, Rectangle area, long now, BarObservation observation, string gate)
        {
            if (!Recording) return;
            bool changed, dense;
            lock (sync)
            {
                state = "Present=" + observation.Present + " Stable=" + observation.Stable + " Raw=" + observation.RawFound + " | " + gate;
                target = observation.Bounds;
                changed = !hasObservation || observation.Present != lastPresent || observation.RawFound != lastRaw;
                hasObservation = true; lastPresent = observation.Present; lastRaw = observation.RawFound;
                dense = macroEnabled && holdPressed;
            }
            if (changed && now - lastCrop >= 200) CaptureDetection(frame, area, now, observation.Reason);
            else if (observation.Present && !observation.Bounds.IsEmpty && now - lastContext >= (dense ? 40 : 500))
            {
                Rectangle local = Rectangle.Intersect(new Rectangle(Point.Empty, frame.Size), new Rectangle(observation.Bounds.X - area.X - 70, observation.Bounds.Y - area.Y - 35, observation.Bounds.Width + 140, observation.Bounds.Height + 70));
                if (local.Width > 0 && local.Height > 0)
                {
                    lock (sync)
                    {
                        if (crops.Count < 8)
                        {
                            try { crops.Enqueue(new Snapshot { Image = frame.Clone(local, PixelFormat.Format32bppArgb), Now = now, Area = new Rectangle(area.X + local.X, area.Y + local.Y, local.Width, local.Height), Reason = dense ? "HUD-Kontext dicht: aktiv und gehalten, vor möglichem Ausfall" : "HUD-Kontext vor einem möglichen Ausfall" }); lastContext = now; }
                            catch (Exception) { droppedCrops++; }
                        }
                        else droppedCrops++;
                    }
                }
            }
        }
        internal void CaptureDetection(Bitmap frame, Rectangle area, long now, string reason)
        {
            if (!Recording || frame == null) return;
            lock (sync)
            {
                if (crops.Count >= 8) { droppedCrops++; return; }
                try { crops.Enqueue(new Snapshot { Image = (Bitmap)frame.Clone(), Now = now, Area = area, Reason = reason }); lastCrop = now; }
                catch (Exception) { droppedCrops++; }
            }
        }
        void Drain(StreamWriter log, StreamWriter images, bool all = false)
        {
            List<string> rows = new List<string>(); List<Snapshot> shots = new List<Snapshot>();
            lock (sync) { while (events.Count > 0) rows.Add(events.Dequeue()); while (crops.Count > 0 && (all || shots.Count < 2)) shots.Add(crops.Dequeue()); }
            try
            {
                foreach (string row in rows) log.WriteLine(row);
                for (int i = 0; i < shots.Count; i++)
                {
                    Snapshot shot = shots[i];
                    bool large = shot.Image.Width * shot.Image.Height > 400000;
                    string filename = (shot.Now - origin).ToString("D9", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 6) + (large ? ".jpg" : ".png");
                    if (large)
                    {
                        ImageCodecInfo jpeg = null; foreach (ImageCodecInfo codec in ImageCodecInfo.GetImageEncoders()) if (codec.MimeType == "image/jpeg") jpeg = codec;
                        using (EncoderParameters quality = new EncoderParameters(1)) { quality.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 92L); shot.Image.Save(Path.Combine(directory, "Bildausschnitte", filename), jpeg, quality); }
                    }
                    else shot.Image.Save(Path.Combine(directory, "Bildausschnitte", filename), ImageFormat.Png);
                    images.WriteLine((shot.Now - origin) + "," + shot.Now + "," + filename + ",\"" + shot.Area + "\",\"" + (shot.Reason ?? "").Replace("\"", "\"\"") + "\"");
                }
            }
            finally { foreach (Snapshot shot in shots) shot.Image.Dispose(); }
        }
        void Run()
        {
            string outcome = "Aufnahme gespeichert"; int part = 0; MjpegAviWriter video = null;
            try
            {
                using (StreamWriter log = new StreamWriter(Path.Combine(directory, "events.csv"), false, new UTF8Encoding(true)))
                using (StreamWriter frames = new StreamWriter(Path.Combine(directory, "frames.csv"), false, new UTF8Encoding(true)))
                using (StreamWriter images = new StreamWriter(Path.Combine(directory, "Bildausschnitte.csv"), false, new UTF8Encoding(true)))
                using (ScreenFrameSource screen = new ScreenFrameSource())
                using (Bitmap canvas = new Bitmap(Width, Height, PixelFormat.Format24bppRgb))
                using (Graphics graphics = Graphics.FromImage(canvas))
                using (Font font = new Font("Consolas", 11))
                using (EncoderParameters quality = new EncoderParameters(1))
                {
                    log.WriteLine(DetectionTrace.Header); frames.WriteLine("part,frame,video_ms,app_ms,source,duplicate,enabled,held"); images.WriteLine("video_ms,app_ms,file,screen_area,reason");
                    ImageCodecInfo jpegCodec = null;
                    foreach (ImageCodecInfo codec in ImageCodecInfo.GetImageEncoders()) if (codec.MimeType == "image/jpeg") jpegCodec = codec;
                    if (jpegCodec == null) throw new InvalidOperationException("JPEG-Encoder fehlt.");
                    quality.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 65L);
                    long segmentOrigin = origin, lastFlush = origin, lastDiskCheck = origin - 5000;
                    byte[] previous = null; long previousAt = origin; string previousSource = "Pause";
                    bool previousEnabled = false, previousHeld = false;
                    while (!stop.WaitOne(0))
                    {
                        long now = clock();
                        if (now - lastDiskCheck >= 2000)
                        {
                            lastDiskCheck = now;
                            if (new DriveInfo(Path.GetPathRoot(directory)).AvailableFreeSpace < 2L * 1024 * 1024 * 1024) { outcome = "Aufnahme gestoppt: weniger als 2 GiB frei"; break; }
                        }
                        if (video == null || video.FrameCount >= Fps * 300 || video.Length >= 512L * 1024 * 1024)
                        {
                            if (video != null) { segmentOrigin += (long)video.FrameCount * 1000 / Fps; video.Dispose(); }
                            part++; video = new MjpegAviWriter(Path.Combine(directory, "Gameplay-" + part.ToString("D3") + ".avi"), Width, Height, Fps); previous = null;
                        }
                        int expected = (int)((now - segmentOrigin) * Fps / 1000);
                        if (expected < video.FrameCount) { stop.WaitOne(3); continue; }
                        // Keep real-time playback even if capture/encoding briefly misses its budget.
                        while (previous != null && video.FrameCount < expected && !stop.WaitOne(0))
                        {
                            frames.WriteLine(part + "," + video.FrameCount + "," + (segmentOrigin - origin + (long)video.FrameCount * 1000 / Fps) + "," + previousAt + "," + previousSource + ",True," + previousEnabled + "," + previousHeld); video.Add(previous);
                        }
                        if (stop.WaitOne(0)) break;
                        Bitmap source = testCapture == null ? screen.Capture() : testCapture();
                        long capturedAt = clock(); string sourceName = source == null ? "Pause" : (testCapture == null ? "Fortnite" : "Fixture");
                        graphics.Clear(Color.FromArgb(24, 28, 35)); graphics.InterpolationMode = InterpolationMode.Bilinear;
                        Rectangle draw = Rectangle.Empty, hud; string info;
                        string controlLine; bool enabledAtFrame, heldAtFrame;
                        lock (sync) { info = state; hud = target; controlLine = controls; enabledAtFrame = macroEnabled; heldAtFrame = holdPressed; }
                        if (source != null)
                        {
                            double scale = Math.Min((double)Width / source.Width, (double)Height / source.Height);
                            draw = new Rectangle((Width - (int)(source.Width * scale)) / 2, (Height - (int)(source.Height * scale)) / 2, (int)(source.Width * scale), (int)(source.Height * scale));
                            graphics.DrawImage(source, draw);
                            if (testCapture == null && !hud.IsEmpty)
                            {
                                Rectangle client = screen.Bounds;
                                graphics.DrawRectangle(Pens.Yellow, draw.X + (int)((hud.X - client.X) * scale), draw.Y + (int)((hud.Y - client.Y) * scale), Math.Max(1, (int)(hud.Width * scale)), Math.Max(1, (int)(hud.Height * scale)));
                            }
                        }
                        else graphics.DrawString("Aufnahme pausiert – Fortnite ist nicht im Vordergrund", font, Brushes.White, 90, 330);
                        graphics.FillRectangle(Brushes.Black, 0, 0, Width, 65);
                        graphics.DrawString("REC " + TimeSpan.FromMilliseconds(capturedAt - origin).ToString(@"hh\:mm\:ss\.fff") + " | " + sourceName + " | F12: Fehler markieren", font, Brushes.White, 8, 3);
                        graphics.DrawString(controlLine, font, enabledAtFrame ? Brushes.LimeGreen : Brushes.Orange, 8, 23);
                        graphics.DrawString(info, font, Brushes.White, 8, 43);
                        using (MemoryStream memory = new MemoryStream()) { canvas.Save(memory, jpegCodec, quality); previous = memory.ToArray(); }
                        previousAt = capturedAt; previousSource = sourceName;
                        previousEnabled = enabledAtFrame; previousHeld = heldAtFrame;
                        frames.WriteLine(part + "," + video.FrameCount + "," + (segmentOrigin - origin + (long)video.FrameCount * 1000 / Fps) + "," + capturedAt + "," + sourceName + ",False," + enabledAtFrame + "," + heldAtFrame); video.Add(previous);
                        Drain(log, images);
                        if (capturedAt - lastFlush >= 1000) { log.Flush(); frames.Flush(); images.Flush(); lastFlush = capturedAt; }
                    }
                    Drain(log, images, true);
                }
            }
            catch (Exception ex) { outcome = "Aufnahmefehler: " + ex.Message; }
            finally
            {
                try { if (video != null) video.Dispose(); } catch (Exception ex) { outcome = "Videoabschluss fehlgeschlagen: " + ex.Message; }
                lock (sync) { while (crops.Count > 0) crops.Dequeue().Image.Dispose(); status = outcome; }
                try { File.AppendAllText(Path.Combine(directory, "Sitzung.txt"), "EndUTC=" + DateTime.UtcNow.ToString("o") + "\r\n" + outcome + "\r\nDroppedEvents=" + droppedEvents + "\r\nDroppedCrops=" + droppedCrops + "\r\n"); } catch { }
            }
        }
        internal void Stop()
        {
            stop.Set();
            if (worker != null && worker.IsAlive && !worker.Join(5000)) { lock (sync) status = "Aufnahme wird noch abgeschlossen …"; }
        }
        public void Dispose() { Stop(); if (!Recording) stop.Dispose(); }
    }

    internal sealed class ScreenFrameSource : IDisposable
    {
        Bitmap image;
        internal Rectangle Bounds;
        internal Bitmap Capture()
        {
            IntPtr window = Native.GetForegroundWindow();
            if (!MainForm.FortniteForeground() || window != Native.GetForegroundWindow()) return null;
            Rectangle bounds;
            if (!HealthBarCapture.ClientBounds(window, out bounds)) return null;
            if (image == null || image.Size != bounds.Size)
            { if (image != null) image.Dispose(); image = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb); }
            try { using (Graphics graphics = Graphics.FromImage(image)) graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size); }
            catch (System.ComponentModel.Win32Exception) { return null; }
            if (window != Native.GetForegroundWindow() || !MainForm.FortniteForeground()) return null;
            Bounds = bounds; return image;
        }
        public void Dispose() { if (image != null) image.Dispose(); }
    }

    internal static class RecorderTests
    {
        static void Check(bool ok, string why) { if (!ok) throw new Exception("Recorder: " + why); }
        internal static void Run(string root)
        {
            Directory.CreateDirectory(root);
            System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
            using (Bitmap fixture = new Bitmap(320, 180, PixelFormat.Format24bppRgb))
            using (Bitmap crop = new Bitmap(200, 80, PixelFormat.Format32bppArgb))
            using (GameplayRecorder recorder = new GameplayRecorder())
            {
                int number = 0;
                Func<Bitmap> capture = delegate
                {
                    number++;
                    // Simulate a missed frame budget to exercise repeated-frame time alignment.
                    if (number == 8) Thread.Sleep(140);
                    using (Graphics g = Graphics.FromImage(fixture))
                    { g.Clear(Color.FromArgb(35, 70, 150)); g.FillRectangle(Brushes.LimeGreen, 80, 70, Math.Max(1, 150 - number), 12); g.DrawString("FRAME " + number, SystemFonts.DefaultFont, Brushes.White, 20, 110); }
                    return number % 9 == 0 ? null : fixture;
                };
                recorder.Start(root, delegate { return 5000 + clock.ElapsedMilliseconds; }, new Config(), capture);
                recorder.SetControls(true, true, "fixture active hold");
                Thread.Sleep(160);
                BarObservation observation = new BarObservation { Present = true, Stable = true, RawFound = true, Bounds = new Rectangle(80, 70, 150, 12), Reason = "fixture bar", FrameAppMs = 5000 + clock.ElapsedMilliseconds, CaptureMs = 3.25, DetectMs = 4.5 };
                using (Graphics g = Graphics.FromImage(crop)) g.Clear(Color.Green);
                recorder.Observe(crop, new Rectangle(20, 30, 200, 80), 5000 + clock.ElapsedMilliseconds, observation, "fixture gate");
                DetectionTrace trace = new DetectionTrace(); trace.LineAdded += recorder.Log;
                trace.Add(5000 + clock.ElapsedMilliseconds, true, true, observation, "fixture", "Pending");
                recorder.Marker("Testfehler");
                using (Bitmap large = new Bitmap(1000, 600, PixelFormat.Format32bppArgb))
                { using (Graphics g = Graphics.FromImage(large)) g.Clear(Color.SteelBlue); recorder.CaptureDetection(large, new Rectangle(20, 30, 1000, 600), 5000 + clock.ElapsedMilliseconds, "Schussentscheidung Testfixture"); }
                Thread.Sleep(200);
                recorder.Observe(crop, new Rectangle(20, 30, 200, 80), 5000 + clock.ElapsedMilliseconds, observation, "fixture gate");
                long denseAt = 5000 + clock.ElapsedMilliseconds;
                for (int i = 1; i <= 4; i++) recorder.Observe(crop, new Rectangle(20, 30, 200, 80), denseAt + i * 20, observation, "fixture dense sampling");
                recorder.SetControls(false, false, "fixture paused");
                recorder.Observe(crop, new Rectangle(20, 30, 200, 80), denseAt + 120, observation, "fixture paused cadence");
                recorder.Observe(crop, new Rectangle(20, 30, 200, 80), denseAt + 400, observation, "fixture paused cadence");
                Thread.Sleep(1050); recorder.Stop();
                Check(!recorder.Recording, "stop joins the writer");
                Check(recorder.StatusText == "Aufnahme gespeichert", recorder.StatusText);
                string session = recorder.DirectoryPath;
                Check(File.ReadAllText(Path.Combine(session, "events.csv")).Contains("MARKER: Testfehler"), "marker is persisted");
                Check(File.ReadAllText(Path.Combine(session, "events.csv")).Contains("Pending"), "diagnosis row is persisted");
                Check(File.ReadAllText(Path.Combine(session, "events.csv")).Contains(",3.25,4.50,"), "capture/detection timings survive recording with invariant decimals");
                Check(File.ReadAllText(Path.Combine(session, "Sitzung.txt")).Contains("AppVersion=" + typeof(GameplayRecorder).Assembly.GetName().Version), "recording identifies its actual executable version");
                Check(Directory.GetFiles(Path.Combine(session, "Bildausschnitte"), "*.png").Length >= 4, "full-resolution context and dense active-held HUD samples saved");
                Check(File.ReadAllText(Path.Combine(session, "Bildausschnitte.csv")).Contains("HUD-Kontext"), "stable HUD context is saved before a possible loss");
                int denseCount = 0;
                foreach (string line in File.ReadAllLines(Path.Combine(session, "Bildausschnitte.csv"))) if (line.Contains("HUD-Kontext dicht:")) denseCount++;
                Check(denseCount == 3, "active held observations use 40 ms native contexts; paused observations keep slower cadence");
                Check(Directory.GetFiles(Path.Combine(session, "Bildausschnitte"), "*.jpg").Length == 1, "large overview is saved as fast JPEG rather than PNG");
                Check(File.ReadAllText(Path.Combine(session, "frames.csv")).Contains(",True,True") && File.ReadAllText(Path.Combine(session, "frames.csv")).Contains(",False,False"), "video timeline preserves active/paused and physical hold state");
                Check(File.ReadAllLines(Path.Combine(session, "frames.csv")).Length > 20, "continuous frames written");
                Check(File.ReadAllText(Path.Combine(session, "Sitzung.txt")).Contains("DroppedEvents=0"), "no log overflow");
                File.WriteAllText(Path.Combine(root, "main-session.txt"), session);
                // Restart and simulate a capture failure: the AVI must still be finalized.
                int failingFrames = 0;
                recorder.Start(root, delegate { return 5000 + clock.ElapsedMilliseconds; }, new Config(), delegate { if (++failingFrames > 4) throw new IOException("fixture capture error"); return fixture; });
                Thread.Sleep(600); recorder.Stop();
                Check(!recorder.Recording && recorder.StatusText.Contains("fixture capture error"), "capture failure is surfaced");
                Check(File.Exists(Path.Combine(recorder.DirectoryPath, "Gameplay-001.avi")), "partial video retained on failure");
                File.WriteAllText(Path.Combine(root, "failure-session.txt"), recorder.DirectoryPath);
            }
        }
    }
}
