using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;

namespace PrefireMacro
{
    internal sealed class ShotVerifier : IDisposable
    {
        readonly HealthBarCapture capture = new HealthBarCapture();
        internal long LastCheckedAtMs { get; private set; }
        internal static long ClockMs() { return (long)(Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency)); }
        internal void Prepare(Rectangle area, Size client) { capture.Prepare(area, client); }
        internal static bool FrameStillMissing(Bitmap frame, Rectangle area, Size client, Rectangle known, HealthBarDetector detector)
        { bool restored; return FrameStillMissing(frame, area, client, known, detector, out restored); }
        static bool FrameStillMissing(Bitmap frame, Rectangle area, Size client, Rectangle known, HealthBarDetector detector, out bool restored, HudTransition transition = null)
        { return FrameStillMissing(frame, area, client, known, detector, new Point(area.X + (int)(client.Width * .45), area.Y + (int)(client.Height * .35)), out restored, transition); }
        static bool FrameStillMissing(Bitmap frame, Rectangle area, Size client, Rectangle known, HealthBarDetector detector, Point reticle, out bool restored, HudTransition transition = null)
        {
            BarDetection current = detector.Find(frame, area, client, null, true, known, null, reticle);
            Rectangle relocated;
            restored = !current.InvalidFrame && (current.Found || detector.FindKnownHudPanel(known, area, client, out relocated, reticle));
            if (restored && transition != null && current.Found && transition.Matches(current.Bounds, HudTransition.Fill(frame, area, current.Bounds, detector), HudMaximum.Read(frame, area, current.Bounds)))
            {
                bool original = detector.FindKnownHudPanel(known, area, client, out relocated, reticle) && !transition.Matches(relocated, HudTransition.Fill(frame, area, relocated, detector), HudMaximum.Read(frame, area, relocated));
                restored = original;
            }
            return !current.InvalidFrame && !restored;
        }
        internal static bool FrameStillMissing(Bitmap frame, Rectangle area, Size client, Rectangle known, HealthBarDetector detector, HudTransition transition)
        { bool restored; return FrameStillMissing(frame, area, client, known, detector, out restored, transition); }
        // Processing the first image counts toward the filter. Confirmation uses a
        // distinct capture, without waiting for another window-message timer tick.
        internal static bool Confirm(long firstFrameAt, int delayMs, Func<long> clock, Action<int> wait, Func<bool> allowed, Func<bool> freshMissing, out string reason)
        {
            reason = "Nicht geprueft";
            if (firstFrameAt <= 0 || clock() < firstFrameAt) { reason = "Ungueltiger Bildzeitpunkt"; return false; }
            while (clock() - firstFrameAt < delayMs)
            {
                if (!allowed()) { reason = "Haltetaste oder Fokus geaendert"; return false; }
                wait(1);
            }
            if (!allowed()) { reason = "Haltetaste oder Fokus geaendert"; return false; }
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (!allowed()) { reason = "Haltetaste oder Fokus geaendert"; return false; }
                long started = clock();
                if (!freshMissing()) { reason = "HUD wieder sichtbar oder Aufnahme ungueltig"; return false; }
                long elapsed = clock() - started;
                if (elapsed > 50) { reason = "Frische Pruefung zu langsam: " + elapsed + " ms"; continue; }
                if (!allowed()) { reason = "Haltetaste oder Fokus waehrend Pruefung geaendert"; return false; }
                reason = "Zweites fehlendes HUD bestaetigt; Bildabstand mindestens " + delayMs + " ms; frische Pruefung " + elapsed + " ms; Nachpruefungen=" + attempt;
                return true;
            }
            return false;
        }
        internal bool Check(IntPtr expectedWindow, uint expectedProcess, Rectangle known, HealthBarDetector detector, Rectangle expectedArea, Size expectedClient, long firstFrameAt, int delayMs, int holdKey, out string reason, out bool resumeObservation, out bool retryVerification, out long restoredAt, HudTransition transition = null)
        {
            reason = "Nicht geprueft";
            LastCheckedAtMs = 0;
            resumeObservation = retryVerification = false;
            restoredAt = 0;
            if (known.IsEmpty) { reason = "Kein zuvor bestaetigtes HUD"; return false; }
            string frameReason = null, frameTimings = "";
            bool restoredHud = false;
            try
            {
                bool result = Confirm(firstFrameAt, delayMs, ClockMs, Thread.Sleep,
                    delegate { return Native.Held(holdKey) && Native.ForegroundMatches(expectedWindow, expectedProcess); },
                    delegate
                    {
                        long captureStarted = ClockMs(); capture.CaptureFrame(expectedWindow); long captured = ClockMs();
                        if (!Native.ForegroundMatches(expectedWindow, expectedProcess))
                        { frameReason = "Fokus waehrend Aufnahme gewechselt"; restoredHud = false; return false; }
                        if (capture.LastArea != expectedArea || capture.LastClientSize != expectedClient)
                        { frameReason = "Spielbereich seit der Entscheidung verschoben oder skaliert"; return false; }
                        bool missing = FrameStillMissing(capture.Frame, capture.LastArea, capture.LastClientSize, known, detector, out restoredHud, transition);
                        frameTimings += " [Aufnahme=" + (captured - captureStarted) + "ms; HUD=" + (ClockMs() - captured) + "ms]";
                        return missing;
                    }, out reason);
                resumeObservation = !result && restoredHud;
                if (frameReason == null) LastCheckedAtMs = capture.CapturedAtMs;
                if (resumeObservation && frameReason == null) restoredAt = capture.CapturedAtMs;
                retryVerification = !result && !restoredHud && reason.StartsWith("Frische Pruefung zu langsam:");
                if (frameReason != null) reason = frameReason;
                if (frameTimings.Length != 0) reason += "; Bildzeiten:" + frameTimings;
                if (result && transition != null) reason += "; Zielwechsel bestaetigt: " + transition.Details;
                return result;
            }
            catch (Exception ex) { reason = "Aufnahmefehler: " + ex.Message; return false; }
        }
        internal Bitmap CopyRestoredContext(long expectedAt, Rectangle known, out Rectangle area)
        {
            area = Rectangle.Empty;
            if (expectedAt <= 0 || capture.CapturedAtMs != expectedAt || capture.Frame == null || known.IsEmpty) return null;
            Rectangle local = new Rectangle(known.X - capture.LastArea.X, known.Y - capture.LastArea.Y, known.Width, known.Height);
            local = Rectangle.Intersect(new Rectangle(Point.Empty, capture.Frame.Size), Rectangle.Inflate(local, 120, 80));
            if (local.IsEmpty) return null;
            area = new Rectangle(capture.LastArea.X + local.X, capture.LastArea.Y + local.Y, local.Width, local.Height);
            try { return capture.Frame.Clone(local, System.Drawing.Imaging.PixelFormat.Format32bppArgb); }
            catch { area = Rectangle.Empty; return null; }
        }
        internal static bool BuildMayProceed(Bitmap frame, Rectangle area, Size client, Rectangle known, HealthBarDetector detector, Point reticle, out string reason, HudTransition transition = null)
        {
            bool restored;
            bool absent = FrameStillMissing(frame, area, client, known, detector, reticle, out restored, transition);
            reason = absent ? "Edit-Schutz: kein Bauteilbalken im geprüften Bereich, Baufolge frei." : restored ? "Edit-Schutz: Bauteilbalken wieder sichtbar (mögliches Edit oder neue Wand). Nur Schuss, keine Baufolge." : "Edit-Schutz: Bild nicht eindeutig lesbar. Nur Schuss, keine Baufolge.";
            return absent;
        }
        internal bool CheckBuild(IntPtr window, Rectangle expectedArea, Size expectedClient, Rectangle known, HealthBarDetector detector, out string reason, HudTransition transition = null)
        {
            reason = "Edit-Schutz: Spielbereich nicht verfügbar. Nur Schuss, keine Baufolge.";
            Rectangle current;
            if (!HealthBarCapture.ClientBounds(window, out current) || current.Size != expectedClient || HealthBarDetector.SearchArea(current) != expectedArea) return false;
            Rectangle region = Rectangle.Intersect(expectedArea, Rectangle.Inflate(known, Math.Max(120, expectedClient.Width * 15 / 100), Math.Max(100, expectedClient.Height * 15 / 100)));
            if (region.IsEmpty) return false;
            try
            {
                using (Bitmap image = new Bitmap(region.Width, region.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                {
                    using (Graphics graphics = Graphics.FromImage(image)) graphics.CopyFromScreen(region.Location, Point.Empty, region.Size);
                    return BuildMayProceed(image, region, expectedClient, known, detector, new Point(current.Left + current.Width / 2, current.Top + current.Height / 2), out reason, transition);
                }
            }
            catch (Exception ex) { reason = "Edit-Schutz: Bildprüfung fehlgeschlagen. Baufolge ausgelassen: " + ex.Message; return false; }
        }
        public void Dispose() { capture.Dispose(); }
    }
}
