using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;

namespace PrefireMacro
{
    internal static class HudReplay
    {
        internal static bool GuardProbe(string manifest, string report, bool turbo = false)
        {
            Config config = new Config { HasFirst = true, HasSecond = true, FirstColor = 0x5CC442, SecondColor = 0xDA943D };
            HealthBarDetector detector = new HealthBarDetector(config, 25) { Turbo = turbo }; int count = 0, failed = 0;
            using (StreamWriter output = new StreamWriter(report))
            {
                output.WriteLine("file,build_permitted,ms,reason");
                foreach (string line in File.ReadAllLines(manifest))
                {
                    if (line.StartsWith("file,") || String.IsNullOrWhiteSpace(line)) continue;
                    string[] p = line.Split(',');
                    using (Bitmap image = new Bitmap(Path.Combine(Path.GetDirectoryName(manifest), p[0])))
                    {
                        Rectangle area = new Rectangle(Int32.Parse(p[1]), Int32.Parse(p[2]), image.Width, image.Height);
                        Size client = new Size(Int32.Parse(p[3]), Int32.Parse(p[4])); Rectangle known = new Rectangle(Int32.Parse(p[6]), Int32.Parse(p[7]), Int32.Parse(p[8]), Int32.Parse(p[9]));
                        detector.HudDebug = File.ReadAllLines(manifest).Length < 4 ? new System.Collections.Generic.List<string>() : null;
                        string reason; Stopwatch watch = Stopwatch.StartNew();
                        bool permitted = ShotVerifier.BuildMayProceed(image, area, client, known, detector, new Point(client.Width / 2, client.Height / 2), out reason);
                        if (permitted != Boolean.Parse(p[5])) failed++;
                        if (detector.HudDebug != null) File.WriteAllLines(report + ".debug.txt", detector.HudDebug);
                        output.WriteLine(Path.GetFileName(p[0]) + "," + permitted + "," + watch.Elapsed.TotalMilliseconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + ",\"" + reason.Replace("\"", "\"\"") + "\""); count++;
                    }
                }
            }
            File.WriteAllText(report + ".result.txt", "GuardFrames=" + count + "; Mismatches=" + failed + "; " + (failed == 0 ? "PASS" : "FAIL") + "; visible HUD is a conservative build veto, not an edit-geometry classifier; no inputs sent.");
            return failed == 0;
        }
        internal static bool Track(string manifest, string report, bool turbo = false)
        {
            Config config = new Config { HasFirst = true, HasSecond = true, FirstColor = 0x5CC442, SecondColor = 0xDA943D };
            HealthBarDetector detector = new HealthBarDetector(config, 25) { Turbo = turbo }; HealthBarTracker tracker = new HealthBarTracker();
            PrefireGate gate = new PrefireGate(20);
            int failures = 0, count = 0; long now = 0; SceneSample previousScene = null;
            using (StreamWriter output = new StreamWriter(report))
            {
                output.WriteLine("file,present,stable,scan_ms,reason,confirmation_requested,fresh_missing,fill,steady,bounds");
                foreach (string line in File.ReadAllLines(manifest))
                {
                    if (line.StartsWith("file,") || String.IsNullOrWhiteSpace(line)) continue;
                    string[] p = line.Split(','); Size client = new Size(Int32.Parse(p[3]), Int32.Parse(p[4]));
                    Rectangle area = HealthBarDetector.SearchArea(new Rectangle(Point.Empty, client));
                    using (Bitmap source = new Bitmap(Path.Combine(Path.GetDirectoryName(manifest), p[0])))
                    using (Bitmap canvas = new Bitmap(area.Width, area.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                    {
                        // A small original HUD context is placed at its recorded screen position.
                        // Outside that context the seed frame is neutral synthetic background.
                        using (Graphics graphics = Graphics.FromImage(canvas)) { graphics.Clear(Color.FromArgb(65, 55, 48)); graphics.DrawImageUnscaled(source, Int32.Parse(p[1]) - area.X, Int32.Parse(p[2]) - area.Y); }
                        BarObservation observation = null; Stopwatch watch = Stopwatch.StartNew();
                        bool expectedRequest = false;
                        bool checkDecision = p.Length >= 8 && Boolean.TryParse(p[6], out expectedRequest);
                        long frameTime = checkDecision ? Int64.Parse(p[7]) : now;
                        int repeats = count == 0 ? 3 : 1;
                        for (int i = 0; i < repeats; i++) { observation = tracker.Update(canvas, area, client, detector, checkDecision ? frameTime - (repeats - 1 - i) * 20 : now); now += 20; }
                        if (observation.Present != Boolean.Parse(p[5]) || (observation.Present && !observation.Stable)) failures++;
                        bool request = false, freshMissing = false;
                        if (checkDecision)
                        {
                            request = observation.Valid && HudAimGuard.RequestConfirmation(gate, true, observation.Present, observation.Stable, observation.Bounds, area, client, frameTime);
                            if (request) freshMissing = ShotVerifier.FrameStillMissing(canvas, area, client, observation.Bounds, detector, observation.Transition);
                            if (request != expectedRequest || (request && !freshMissing)) failures++;
                            // Simulate acknowledgement only to exercise the next cycle.
                            // This replay never starts an input sequence or captures the screen.
                            if (request) gate.ConfirmSent(frameTime);
                        }
                        output.WriteLine(Path.GetFileName(p[0]) + "," + observation.Present + "," + observation.Stable + "," + watch.Elapsed.TotalMilliseconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + ",\"" + observation.Reason.Replace("\"", "\"\"") + "\"," + request + "," + freshMissing + "," + HudTransition.Fill(canvas, area, observation.Bounds, detector).ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + "," + (previousScene != null && previousScene.IsSteady(canvas)) + ",\"" + observation.Bounds + "\""); previousScene = new SceneSample(canvas); count++;
                    }
                }
            }
            File.WriteAllText(report + ".result.txt", "TrackingFrames=" + count + "; Mismatches=" + failures + (failures == 0 ? "; PASS" : "; FAIL") + "; first HUD context repeated for acquisition, neutral background outside context; no inputs sent");
            return failures == 0;
        }
        // Manifest: filename,screen_x,screen_y,client_width,client_height (generated regression fixtures).
        internal static bool Probe(string manifest, string report, bool includeTrackedSupport = false, bool turbo = false)
        {
            Config config = new Config { HasFirst = true, HasSecond = true, FirstColor = 0x5CC442, SecondColor = 0xDA943D };
            HealthBarDetector detector = new HealthBarDetector(config, 25) { Turbo = turbo };
            string[] lines = File.ReadAllLines(manifest); bool debug = lines.Length < 4;
            int failures = 0, labelled = 0, total = 0;
            using (StreamWriter output = new StreamWriter(report))
            {
                output.WriteLine("file,found,x,y,width,height,scan_ms,raw_found,known_panel");
                foreach (string line in lines)
                {
                    if (line.StartsWith("file,") || String.IsNullOrWhiteSpace(line)) continue;
                    string[] p = line.Split(',');
                    using (Bitmap image = new Bitmap(Path.Combine(Path.GetDirectoryName(manifest), p[0])))
                    {
                        Rectangle area = new Rectangle(Int32.Parse(p[1]), Int32.Parse(p[2]), image.Width, image.Height);
                        Stopwatch watch = Stopwatch.StartNew();
                        detector.HudDebug = debug ? new System.Collections.Generic.List<string>() : null;
                        Size client = new Size(Int32.Parse(p[3]), Int32.Parse(p[4]));
                        Rectangle? verified = p.Length >= 10 ? (Rectangle?)new Rectangle(Int32.Parse(p[6]), Int32.Parse(p[7]), Int32.Parse(p[8]), Int32.Parse(p[9])) : null;
                        // The fixtures use client origin 0,0. A cropped HUD
                        // context must not shift the reticle along with the crop.
                        Point reticle = new Point(client.Width / 2, client.Height / 2);
                        BarDetection found = detector.Find(image, area, client, null, true, verified, null, reticle);
                        Rectangle relocated;
                        bool knownPanel = verified.HasValue && !found.InvalidFrame && detector.FindKnownHudPanel(verified.Value, area, client, out relocated, reticle);
                        bool present = found.Found || (includeTrackedSupport && knownPanel);
                        total++;
                        if (p.Length > 5) { labelled++; if (present != Boolean.Parse(p[5])) failures++; }
                        if (debug) File.WriteAllLines(report + ".rejections.txt", detector.HudDebug);
                        output.WriteLine(p[0] + "," + present + "," + found.Bounds.X + "," + found.Bounds.Y + "," + found.Bounds.Width + "," + found.Bounds.Height + "," + watch.Elapsed.TotalMilliseconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "," + found.Found + "," + knownPanel);
                    }
                }
            }
            File.WriteAllText(report + ".result.txt", "Processed=" + total + "; Labelled=" + labelled + "; Mismatches=" + failures + (labelled > 0 ? (failures == 0 ? "; PASS" : "; FAIL") : "; no ground-truth labels supplied"));
            return failures == 0;
        }
    }
}
