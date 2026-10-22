using System;
using System.Drawing;
using System.Drawing.Imaging;

namespace PrefireMacro
{
    internal sealed class BarObservation
    {
        public bool Valid = true, Present, Stable, RawFound;
        public Rectangle Bounds;
        internal HudTransition Transition;
        public int ColorNumber;
        public double TemplateSupport;
        public string Reason;
        internal long FrameAppMs = -1;
        internal double CaptureMs = -1, DetectMs = -1;
        internal string CaptureBackend;
    }

    // Track verified numeric building HUD panels as their screen position moves.
    internal sealed class HealthBarTracker
    {
        Rectangle target, pending, lastArea;
        Size lastClient;
        bool locked;
        long pendingSince = -1, pendingAt = -1, lastPresent;
        int confirmations, colorNumber;

        HudTransition transition;
        long transitionAt;
        readonly HudTargetMemory memory = new HudTargetMemory();
        internal BarObservation Update(Bitmap image, Rectangle area, Size client, HealthBarDetector detector, long now, bool allowHandoff = true)
        {
            if (transition != null && (!allowHandoff || now - transitionAt > 300 || area != lastArea || client != lastClient)) Reset();
            if (transition != null)
            {
                BarDetection next = detector.Find(image, area, client, null, true, transition.OldBounds);
                if (next.InvalidFrame) { Reset(); return new BarObservation { Valid = false, Reason = "Aufnahme nicht verfügbar." }; }
                if (!next.Found || transition.Matches(next.Bounds, HudTransition.Fill(image, area, next.Bounds, detector), HudMaximum.Read(image, area, next.Bounds)))
                    return new BarObservation { Present = false, Stable = true, Bounds = transition.OldBounds, Transition = transition, Reason = "Vorderes Bauteil fehlt; neuer Balken dahinter ersetzt das Ziel nicht." };
                Reset(); // An old/different readable target returned: acquire it before another decision.
            }
            BarObservation result = UpdateCore(image, area, client, detector, now);
            double fill = result.Present ? HudTransition.Fill(image, area, result.Bounds, detector) : -1;
            HudMaximum maximum = result.Present ? HudMaximum.Read(image, area, result.Bounds) : null;
            bool fillAligned = result.Present && HudTransition.RightEdgeAligned(image, area, result.Bounds);
            HudTransition witnessed = result.Present && result.Stable ? memory.Observe(image, result.Bounds, fill, fillAligned, maximum, now, allowHandoff) : null;
            if (witnessed != null)
            {
                transition = witnessed;
                transitionAt = now; target = transition.OldBounds;
                return new BarObservation { Present = false, Stable = true, Bounds = target, Transition = transition, Reason = "Zielwechsel: Balkenposition und Lebensanzeige wechseln bei ruhiger Umgebung; vorderes Ziel verloren. " + transition.Details };
            }
            return result;
        }
        public void Reset()
        {
            transition = null; memory.Reset();
            target = pending = Rectangle.Empty; locked = false; pendingSince = pendingAt = -1;
            confirmations = 0;
        }
        internal BarObservation UpdateCore(Bitmap image, Rectangle area, Size client, HealthBarDetector detector, long now)
        {
            if (lastArea != area || lastClient != client) { Reset(); lastArea = area; lastClient = client; }
            // The selected building's HUD moves across the screen with camera/target motion.
            // Require numeric HP-panel structure rather than a scene-colored strip.
            BarDetection candidate;
            Point reticleCenter = new Point(area.X + (int)(client.Width * .45), area.Y + (int)(client.Height * .35));
            if (locked)
            {
                Rectangle region = Rectangle.Intersect(new Rectangle(Point.Empty, image.Size), new Rectangle(target.X - area.X - 120, target.Y - area.Y - 80, target.Width + 240, target.Height + 160));
                Rectangle localArea = new Rectangle(area.X + region.X, area.Y + region.Y, region.Width, region.Height);
                candidate = detector.Find(image, localArea, client, null, true, target, region, reticleCenter);
                // A permissive occlusion candidate must not move a strictly
                // readable old panel onto an orange scene fragment below it.
                Rectangle relocated;
                if ((!candidate.Found || candidate.ReticleOccluded) && detector.FindKnownHudPanel(target, localArea, client, out relocated, reticleCenter))
                { target = relocated; lastPresent = now; return new BarObservation { Present = true, Stable = true, Bounds = target, ColorNumber = colorNumber, TemplateSupport = 1, Reason = "Lesbares bekanntes HUD hat Vorrang vor verdecktem Kandidaten." }; }
                if (!candidate.Found)
                {
                    if (!candidate.InvalidFrame && detector.HasHudPanel(target, localArea, client))
                    { lastPresent = now; return new BarObservation { Present = true, Stable = true, Bounds = target, ColorNumber = colorNumber, TemplateSupport = 1, Reason = "Bekanntes Zahlen-HUD bleibt im lokalen Suchbereich sichtbar." }; }
                    candidate = detector.Find(image, area, client, null, true, target, null, reticleCenter);
                }
            }
            else candidate = detector.Find(image, area, client, null, true);
            if (candidate.InvalidFrame)
            { Reset(); return new BarObservation { Valid = false, Reason = "Schwarze oder nicht verfügbare Bildschirmaufnahme." }; }
            if (!locked)
            {
                if (!candidate.Found)
                {
                    pendingSince = pendingAt = -1; confirmations = 0;
                    return new BarObservation { Reason = "Kein bestätigter Balken." };
                }
                // Position can move quickly with a close wall and an upward camera
                // turn. Fresh consecutive observations, rather than a stationary
                // screen position, are required for initial acquisition.
                // The paused diagnostic preview samples at 150 ms. Permit that
                // cadence while refusing unrelated images after a longer gap.
                bool continuous = pendingAt >= 0 && now >= pendingAt && now - pendingAt <= 250;
                if (pendingSince < 0 || !continuous)
                { pendingSince = now; confirmations = 0; pending = candidate.Bounds; }
                else pending = candidate.Bounds;
                pendingAt = now;
                confirmations++;
                if (confirmations >= 3 && now - pendingSince >= 35)
                {
                    locked = true; target = pending; colorNumber = candidate.ColorNumber; lastPresent = now;
                }
                return new BarObservation { Present = true, Stable = locked, RawFound = true, Bounds = locked ? target : candidate.Bounds, ColorNumber = candidate.ColorNumber, Reason = locked ? "Balken über Zeit bestätigt." : "Balkenkandidat wird bestätigt." };
            }
            if (candidate.Found)
            {
                lastPresent = now; colorNumber = candidate.ColorNumber;
                target = candidate.Bounds;
                return new BarObservation { Present = true, Stable = true, RawFound = true, Bounds = target, ColorNumber = colorNumber, Reason = candidate.ReticleOccluded ? "Bekannter HUD-Balken bleibt sichtbar; Fadenkreuz verdeckt einen Teil der Zahlen." : "Lebenszahlen und vollständiger HUD-Balken erkannt." };
            }
            double support = 0;
            if (target.Left - area.Left < target.Width / 3 || area.Right - target.Right < target.Width / 3 || target.Top - area.Top < target.Height * 2 || area.Bottom - target.Bottom < target.Height * 2)
            { Reset(); return new BarObservation { Valid = false, Reason = "HUD am Aufnahmerand verloren – kein Schuss; Haltetaste loslassen." }; }
            if (detector.HasHudPanel(target, area, client))
            {
                lastPresent = now;
                return new BarObservation { Present = true, Stable = true, Bounds = target, ColorNumber = colorNumber, TemplateSupport = 1, Reason = "HUD mit Lebenszahlen bleibt sichtbar; Färbung allein löst nicht aus." };
            }
            BarObservation lost = new BarObservation { Present = false, Stable = true, Bounds = target, ColorNumber = colorNumber, TemplateSupport = support, Reason = "Bestätigtes Lebensbalken-HUD mit Zahlen fehlt." };
            if (now - lastPresent > 350) { Reset(); lost.Stable = false; }
            return lost;
        }
    }

    internal static class BarTrackerTests
    {
        static void Check(bool value, string message) { if (!value) throw new Exception("HealthBarTracker: " + message); }
        internal static void Run()
        {
            HudTransitionTests.Run();
            CheckDamagedTranslucentHud();
            Config config = new Config { HasFirst = true, HasSecond = true, FirstColor = 0x5CC442, SecondColor = 0xDA943D };
            HealthBarDetector detector = new HealthBarDetector(config, 25);
            Size client = new Size(1920, 1080); Rectangle area = HealthBarDetector.SearchArea(new Rectangle(Point.Empty, client));
            using (Bitmap image = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppArgb))
            using (Graphics graphics = Graphics.FromImage(image))
            {
                Action<int, bool> draw = delegate(int fill, bool orange)
                {
                    graphics.Clear(Color.FromArgb(65, 55, 48)); using (Brush background = new SolidBrush(Color.FromArgb(60, 68, 90))) graphics.FillRectangle(background, 450, 300, 126, 12);
                    using (Brush brush = new SolidBrush(orange ? Color.FromArgb(218, 148, 61) : Color.FromArgb(92, 196, 66))) graphics.FillRectangle(brush, 450, 300, fill, 12);
                    DrawDigits(graphics, fill == 120 ? "150 150" : "1 150", 453, 301);
                };
                HealthBarTracker tracker = new HealthBarTracker(); PrefireGate gate = new PrefireGate();
                draw(120, false);
                using (Bitmap panel = image.Clone(new Rectangle(450, 300, 126, 12), PixelFormat.Format32bppArgb))
                {
                    HealthBarTracker acquisition = new HealthBarTracker(); BarObservation acquiring = null;
                    acquisition.Reset(); acquisition.Update(image, area, client, detector, 200);
                    acquisition.Update(image, area, client, detector, 220);
                    acquiring = acquisition.Update(image, area, client, detector, 500);
                    Check(!acquiring.Stable, "widely spaced images cannot complete initial acquisition");
                    acquisition.Update(image, area, client, detector, 520);
                    acquiring = acquisition.Update(image, area, client, detector, 540);
                    Check(acquiring.Stable, "fresh acquisition recovers after a long image gap");
                    acquisition.Reset(); acquisition.Update(image, area, client, detector, 700);
                    acquisition.Update(image, area, client, detector, 850);
                    acquiring = acquisition.Update(image, area, client, detector, 1000);
                    Check(acquiring.Stable, "paused 150 ms preview cadence can still confirm a HUD");
                }
                draw(120, false);
                BarObservation observation = null;
                foreach (int now in new int[] { 0, 40, 80 }) { observation = tracker.Update(image, area, client, detector, now); gate.Step(true, observation.Present, observation.Stable, now); }
                Check(observation.Stable, "acquire the same stable bar");
                int originalWidth = observation.Bounds.Width;
                foreach (int fill in new int[] { 110, 100, 90, 80, 70, 60, 55, 40, 30, 20, 10, 1 })
                {
                    draw(fill, true); observation = tracker.Update(image, area, client, detector, 100 + 120 - fill);
                    Check(observation.Present && observation.Stable && observation.Bounds.Width >= originalWidth, "successive damage cannot shrink the saved HUD or discard the number template");
                }
                draw(1, true); observation = tracker.Update(image, area, client, detector, 240);
                Check(observation.Present && observation.Stable, "one-pixel fill remains a visible HUD via white numerals");
                Check(!gate.Step(true, observation.Present, observation.Stable, 240), "low HP must not trigger");
                draw(120, false);
                using (Bitmap moved = image.Clone(new Rectangle(450, 300, 126, 12), PixelFormat.Format32bppArgb))
                { graphics.Clear(Color.FromArgb(65, 55, 48)); graphics.DrawImageUnscaled(moved, 900, 450); }
                observation = tracker.Update(image, area, client, detector, 250);
                Check(observation.Present && observation.Stable && observation.Bounds.X == area.X + 900, "verified HUD follows a large screen displacement rather than reporting loss");
                using (Brush blue = new SolidBrush(Color.FromArgb(50, 140, 210))) graphics.FillRectangle(blue, 900, 450, 126, 12);
                DrawDigits(graphics, "150 150", 903, 451);
                observation = tracker.Update(image, area, client, detector, 255);
                Check(observation.Present && observation.Stable && !observation.RawFound, "blue tint alone preserves a verified numeric HUD");
                Check(!ShotVerifier.FrameStillMissing(image, area, client, observation.Bounds, detector), "fresh verification rejects a click while the tinted HUD is still present");
                Check(!gate.Step(true, observation.Present, observation.Stable, 255), "blue tint cannot request a shot");
                graphics.Clear(Color.FromArgb(65, 55, 48)); graphics.FillRectangle(Brushes.LimeGreen, 750, 100, 120, 12);
                Check(detector.Find(image, area, client).Found, "distractor exists in global search");
                foreach (int now in new int[] { 260, 280, 300 })
                { observation = tracker.Update(image, area, client, detector, now); Check(!observation.Present, "remote stripe cannot replace the locked target"); Check(!gate.Step(true, observation.Present, observation.Stable, now), "wait for loss debounce"); }
                observation = tracker.Update(image, area, client, detector, 320);
                Check(gate.Step(true, observation.Present, observation.Stable, 320), "actual loss fires despite remote stripe");
                Check(ShotVerifier.FrameStillMissing(image, area, client, observation.Bounds, detector), "fresh verification allows a genuinely missing numeric HUD");
                gate.ConfirmSent(321);
                tracker.Reset(); gate.Reset(); draw(120, false);
                foreach (int now in new int[] { 0, 40, 80 }) { observation = tracker.Update(image, area, client, detector, now); gate.Step(true, observation.Present, observation.Stable, now); }
                graphics.Clear(Color.FromArgb(65, 55, 48)); observation = tracker.Update(image, area, client, detector, 100); Check(!gate.Step(true, observation.Present, observation.Stable, 100), "first dropout");
                observation = tracker.Update(image, area, client, detector, 130); Check(!gate.Step(true, observation.Present, observation.Stable, 130), "short dropout");
                draw(120, false); observation = tracker.Update(image, area, client, detector, 150); Check(!gate.Step(true, observation.Present, observation.Stable, 150), "bar returned without click");
                graphics.Clear(Color.Black); observation = tracker.Update(image, area, client, detector, 170); Check(!observation.Valid, "black capture is invalid, not a wall disappearance");
                Check(!ShotVerifier.FrameStillMissing(image, area, client, new Rectangle(area.X + 450, area.Y + 300, 126, 12), detector), "fresh verification refuses a black capture");
                tracker.Reset(); gate.Reset(); draw(120, false);
                using (Bitmap edge = image.Clone(new Rectangle(450, 300, 126, 12), PixelFormat.Format32bppArgb))
                { graphics.Clear(Color.FromArgb(65, 55, 48)); graphics.DrawImageUnscaled(edge, 10, 300); }
                foreach (int now in new int[] { 200, 220, 240 }) { observation = tracker.Update(image, area, client, detector, now); gate.Step(true, observation.Present, observation.Stable, now); }
                Check(observation.Present && observation.Stable, "edge fixture is initially visible");
                graphics.Clear(Color.FromArgb(65, 55, 48)); observation = tracker.Update(image, area, client, detector, 260);
                Check(!observation.Valid, "loss at the capture edge is uncertainty, not a firing decision");
                tracker.Reset();
                using (Bitmap moving = new Bitmap(126, 12, PixelFormat.Format32bppArgb))
                {
                    draw(120, false);
                    using (Graphics movingGraphics = Graphics.FromImage(moving)) movingGraphics.DrawImage(image, new Rectangle(0, 0, 126, 12), new Rectangle(450, 300, 126, 12), GraphicsUnit.Pixel);
                    foreach (int now in new int[] { 300, 320, 340 })
                    {
                        graphics.Clear(Color.FromArgb(65, 55, 48)); graphics.DrawImageUnscaled(moving, 300 + (now - 300) * 10, 300);
                        observation = tracker.Update(image, area, client, detector, now);
                    }
                    Check(observation.Present && observation.Stable, "a genuine numeric HUD can be acquired while its screen position moves");
                }
            }
        }
        static void CheckDamagedTranslucentHud()
        {
            Config config = new Config { HasFirst = true, HasSecond = true, FirstColor = 0x5CC442, SecondColor = 0xDA943D };
            HealthBarDetector detector = new HealthBarDetector(config, 25);
            Size client = new Size(2560, 1440); Rectangle area = HealthBarDetector.SearchArea(new Rectangle(Point.Empty, client));
            using (Bitmap image = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppArgb))
            using (Graphics graphics = Graphics.FromImage(image))
            {
                foreach (int hp in new int[] { 75, 20 })
                {
                    HealthBarTracker tracker = new HealthBarTracker(); PrefireGate gate = new PrefireGate(20); BarObservation observation = null;
                    graphics.Clear(Color.FromArgb(175, 145, 110));
                    using (Brush full = new SolidBrush(Color.FromArgb(92, 196, 66))) graphics.FillRectangle(full, 800, 400, 168, 18);
                    DrawDigits(graphics, "150 150", 803, 404);
                    foreach (int now in new int[] { 0, 20, 40 })
                    { observation = tracker.Update(image, area, client, detector, now); gate.RequestFreshConfirmation(true, observation.Present, observation.Stable, now); }
                    Check(observation.Stable, "full HUD seed must be confirmed before the damage and camera shift");
                    int left = hp == 20 ? 817 : 800, top = hp == 20 ? 408 : 400;
                    graphics.Clear(Color.FromArgb(175, 145, 110));
                    using (Brush remainder = new SolidBrush(Color.FromArgb(104, 82, 62))) graphics.FillRectangle(remainder, left, top, 168, 18);
                    using (Brush fill = new SolidBrush(Color.FromArgb(218, 148, 61))) graphics.FillRectangle(fill, left, top, 168 * hp / 150, 18);
                    DrawDigits(graphics, hp == 75 ? "75 150" : "20 150", left + 3, top + 4);
                    graphics.FillRectangle(Brushes.White, left + 20, top + 1, 1, 16);
                    foreach (int now in new int[] { 60, 80, 100 })
                    {
                        observation = tracker.Update(image, area, client, detector, now);
                        Check(observation.Present && !gate.RequestFreshConfirmation(true, observation.Present, observation.Stable, now), "damaged translucent HUD and tall divider must remain present at " + hp + " HP");
                    }
                    Check(observation.Stable && Math.Abs(observation.Bounds.X - area.X - left) <= 2, "damaged low-fill HUD follows the camera shift; HP=" + hp + "; actual=" + observation.Bounds + "; expectedLeft=" + (area.X + left));
                    Check(!ShotVerifier.FrameStillMissing(image, area, client, observation.Bounds, detector), "second-frame check vetoes a click on the damaged translucent HUD");
                    string buildReason;
                    Check(!ShotVerifier.BuildMayProceed(image, area, client, observation.Bounds, detector, new Point(client.Width / 2, client.Height / 2), out buildReason) && buildReason.Contains("Bauteilbalken wieder sichtbar"), "edit guard suppresses building when the damaged target HUD remains visible");
                    graphics.Clear(Color.FromArgb(175, 145, 110));
                    observation = tracker.Update(image, area, client, detector, 120);
                    Check(!observation.Present && gate.RequestFreshConfirmation(true, false, observation.Stable, 120), "actual disappearance still requests a fresh confirmation");
                    Check(ShotVerifier.BuildMayProceed(image, area, client, observation.Bounds, detector, new Point(client.Width / 2, client.Height / 2), out buildReason), "actual missing target still permits building");
                    graphics.Clear(Color.Black);
                    Check(!ShotVerifier.BuildMayProceed(image, area, client, observation.Bounds, detector, new Point(client.Width / 2, client.Height / 2), out buildReason) && buildReason.Contains("nicht eindeutig"), "unreadable scene cannot authorize guarded building");
                }
            }
        }
        internal static void DrawDigits(Graphics graphics, string text, int x, int y)
        {
            foreach (char c in text)
            {
                if (c == ' ') { x += 4; continue; }
                string pattern = c == '1' ? "010110010010111" : c == '5' ? "111100111001111" : "111101101101111";
                for (int yy = 0; yy < 5; yy++) for (int xx = 0; xx < 3; xx++) if (pattern[yy * 3 + xx] == '1') graphics.FillRectangle(Brushes.White, x + xx * 2, y + yy * 2, 2, 2);
                x += 8;
            }
        }
    }
}
