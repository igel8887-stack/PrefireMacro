using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;

namespace PrefireMacro
{
    internal static class AdaptiveTimingTests
    {
        static void Check(bool value, string reason) { if (!value) throw new Exception("Adaptive timing: " + reason); }
        static BuildHudSample Ammo(bool changed)
        {
            BuildHudSample value = new BuildHudSample { AmmoPresent = true };
            for (int y = 0; y < 12; y++) for (int x = 0; x < 10; x++)
                if ((changed && (x == y / 2 || x == 9 - y / 2)) || (!changed && (x <= 1 || y < 2 || y >= 10)))
                { value.Ammo[y * 48 + x] = 1; value.AmmoPixels++; }
            return value;
        }
        internal static void Run()
        {
            BuildHudSample baseline = Ammo(false), changed = Ammo(true);
            Check(BuildHudReader.AmmoChanged(baseline, changed) && !BuildHudReader.AmmoChanged(baseline, baseline), "glyph change versus unchanged count");
            long now = 0; int reads = 0;
            foreach (int response in new int[] { 40, 76, 132 })
            {
                now = 0; reads = 0;
                using (AdaptiveBuildTiming timing = new AdaptiveBuildTiming(delegate { return ++reads == 1 || now < response ? baseline : changed; }, delegate { return now; }, delegate(int ms) { now += ms; }, 5, 150, 15))
                {
                    timing.Prepare(); timing.WaitFor(false, 0, delegate { return true; });
                    Check(now >= response + 4 && now < response + 9 && timing.Usable, "shot wait follows actual feedback at " + response);
                }
            }
            now = 0; reads = 0;
            using (AdaptiveBuildTiming timing = new AdaptiveBuildTiming(delegate { return ++reads == 1 ? baseline : new BuildHudSample { BuildReady = now >= 60 }; }, delegate { return now; }, delegate(int ms) { now += ms; }, 5, 35, 15))
            { timing.Prepare(); timing.WaitFor(true, 0, delegate { return true; }); Check(now >= 35 && now <= 39 && timing.Mode == "AdaptiveBoundedFallback", "late build animation cannot stall placement for 200 ms"); }
            now = 0;
            using (AdaptiveBuildTiming timing = new AdaptiveBuildTiming(delegate { return baseline; }, delegate { return now; }, delegate(int ms) { now += ms; }, 5, 35, 15))
            {
                timing.Prepare(); timing.WaitFor(false, 0, delegate { return true; });
                Check(now >= 55 && now <= 59 && timing.Mode == "AdaptiveBoundedFallback" && timing.Details.Contains("unbestätigt"), "unchanged ammo uses bounded fallback without claiming acknowledgement or suppressing the build");
            }
            now = 0;
            using (AdaptiveBuildTiming timing = new AdaptiveBuildTiming(delegate { return new BuildHudSample(); }, delegate { return now; }, delegate(int ms) { now += ms; }, 5, 70, 15))
            { timing.Prepare(); timing.WaitFor(false, 0, delegate { return true; }); Check(now == 70 && timing.Mode == "FixedFallback", "unreadable baseline keeps configured fixed shot delay"); timing.WaitFor(true, 70, delegate { return true; }); Check(now == 85, "unreadable baseline keeps configured fixed placement delay"); }
            now = 0;
            using (AdaptiveBuildTiming timing = new AdaptiveBuildTiming(delegate { return baseline; }, delegate { return now; }, delegate(int ms) { now += ms; }, 5, 35, 15))
            { timing.Prepare(); bool cancelled = false; try { timing.WaitFor(true, 0, delegate { return now < 9; }); } catch (OperationCanceledException) { cancelled = true; } Check(cancelled && now < 20, "focus/emergency predicate interrupts adaptive wait"); }
            using (Bitmap blank = new Bitmap(320, 110, PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(blank))
            {
                BuildHudReader reader = new BuildHudReader();
                foreach (Color color in new Color[] { Color.Black, Color.White, Color.FromArgb(30, 175, 240) })
                { g.Clear(color); BuildHudSample sample = reader.Read(blank); Check(!sample.AmmoPresent && !sample.BuildReady, "flat color cannot acknowledge either phase"); }
            }
            foreach (bool stopAtPlacement in new bool[] { false, true })
            {
                SequenceResult result = null; int owned = 0;
                using (ManualResetEvent done = new ManualResetEvent(false))
                using (InputSequence sequence = new InputSequence(delegate(int key, bool down) { owned += down ? 1 : -1; return true; }, delegate { return false; }))
                {
                    sequence.StartDetailed(InputSequence.Plan(true, 5, 35, 10), delegate { return true; }, delegate(SequenceResult r) { result = r; done.Set(); }, null, null, delegate { return true; }, delegate(InputStep step, SequenceResult current, Func<bool> allowed)
                    { if (step.Down && current.ShotSent && ((!stopAtPlacement && step.Key == 5) || (stopAtPlacement && step.Key == 1))) throw new InvalidOperationException("fixture feedback timeout"); });
                    Check(done.WaitOne(2000) && result.ShotSent && result.BuildSent == stopAtPlacement && !result.PlaceSent && result.Outcome == "Failed" && owned == 0, "feedback timeout sends no later down and releases owned buttons");
                }
            }
            // The reported failure is the whole sequence stopping when HUD feedback
            // never arrives. Exercise real worker ordering with mocked inputs.
            SequenceResult fallbackResult = null; int balance = 0;
            using (ManualResetEvent finished = new ManualResetEvent(false))
            using (AdaptiveBuildTiming timing = new AdaptiveBuildTiming(delegate { return baseline; }, ShotVerifier.ClockMs, Thread.Sleep, 5, 35, 15))
            using (InputSequence sequence = new InputSequence(delegate(int key, bool down) { balance += down ? 1 : -1; return true; }, delegate { return false; }))
            {
                sequence.StartDetailed(InputSequence.Plan(true, 5, 35, 10), delegate { return true; }, delegate(SequenceResult r) { fallbackResult = r; finished.Set(); }, delegate { timing.Prepare(); return true; }, null, delegate { return true; }, timing.BeforeStep);
                Check(finished.WaitOne(2000) && fallbackResult.Outcome == "Completed" && fallbackResult.ShotSent && fallbackResult.BuildSent && fallbackResult.PlaceSent && fallbackResult.Steps.Split('|').Length == 6 && balance == 0, "missing HUD feedback still completes shot, build selection and placement with all keys released");
                Check(timing.Mode == "AdaptiveBoundedFallback" && timing.Details.Contains("Schussanzeige unbestätigt") && timing.Details.Contains("Baumodus unbestätigt"), "both unconfirmed phases are reported honestly");
            }
            using (ManualResetEvent finished = new ManualResetEvent(false))
            using (AdaptiveBuildTiming timing = new AdaptiveBuildTiming(delegate { return baseline; }, ShotVerifier.ClockMs, Thread.Sleep, 5, 35, 15))
            using (InputSequence sequence = new InputSequence(delegate(int key, bool down) { balance += down ? 1 : -1; return true; }, delegate { return false; }))
            {
                sequence.StartDetailed(InputSequence.Plan(true, 5, 35, 10), delegate { return true; }, delegate(SequenceResult r) { fallbackResult = r; finished.Set(); }, delegate { timing.Prepare(); return true; }, null, delegate { return true; }, delegate(InputStep step, SequenceResult result, Func<bool> allowed)
                { timing.BeforeStep(step, result, allowed); if (step.Key == 5 && step.Down) throw new BuildSuppressedException("Edit-Schutz after adaptive wait"); });
                Check(finished.WaitOne(2000) && fallbackResult.Outcome == "ShotOnly" && fallbackResult.ShotSent && !fallbackResult.BuildSent && !fallbackResult.PlaceSent && balance == 0, "edit veto also prevents building after an adaptive feedback fallback");
            }
        }
        internal static bool Probe(string manifest, string report)
        {
            int mismatches = 0, count = 0; BuildHudReader reader = new BuildHudReader(); BuildHudSample baseline = null;
            using (StreamWriter output = new StreamWriter(report))
            {
                output.WriteLine("file,build_ready,ammo_present,ammo_pixels,build_icons,shape_changed");
                foreach (string line in File.ReadAllLines(manifest))
                {
                    if (line.StartsWith("file,") || String.IsNullOrWhiteSpace(line)) continue;
                    string[] p = line.Split(',');
                    using (Bitmap source = new Bitmap(Path.Combine(Path.GetDirectoryName(manifest), p[0])))
                    using (Bitmap hud = new Bitmap(320, 110, PixelFormat.Format32bppArgb))
                    {
                        using (Graphics g = Graphics.FromImage(hud))
                        {
                            if (source.Size == hud.Size) g.DrawImageUnscaled(source, 0, 0);
                            else g.DrawImage(source, new Rectangle(0, 0, 320, 110), new Rectangle((int)Math.Round(source.Width * .75), (int)Math.Round(source.Height * 580.0 / 720), (int)Math.Round(source.Width * .25), (int)Math.Round(source.Height * 110.0 / 720)), GraphicsUnit.Pixel);
                        }
                        BuildHudSample sample = reader.Read(hud);
                        if (p.Length > 1 && p[1] != "" && sample.BuildReady != Boolean.Parse(p[1])) mismatches++;
                        if (p.Length > 2 && p[2] != "" && sample.AmmoPresent != Boolean.Parse(p[2])) mismatches++;
                        bool buildState = sample.BuildReady; sample.BuildReady = false;
                        bool shapeChanged = baseline != null && BuildHudReader.AmmoChanged(baseline, sample); sample.BuildReady = buildState;
                        if (baseline == null && sample.AmmoPresent) baseline = sample;
                        output.WriteLine(p[0] + "," + sample.BuildReady + "," + sample.AmmoPresent + "," + sample.AmmoPixels + "," + sample.BuildIcons + "," + shapeChanged); count++;
                    }
                }
            }
            File.WriteAllText(report + ".result.txt", "Frames=" + count + "; Mismatches=" + mismatches + "; " + (mismatches == 0 ? "PASS" : "FAIL") + "; offline HUD classification only, not server/placement confirmation");
            return mismatches == 0;
        }
    }
}
