using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
namespace PrefireMacro
{
    internal static class HudPixels
    {
        internal static PixelFormat LockFormat(Bitmap frame)
        { return frame.PixelFormat == PixelFormat.Format32bppRgb ? PixelFormat.Format32bppRgb : PixelFormat.Format32bppArgb; }
        // Preserve native pixels: no resizing or filtering in the decision path.
        internal static int[] Read(Bitmap frame, Rectangle region)
        {
            int[] pixels = new int[region.Width * region.Height];
            BitmapData data = frame.LockBits(region, ImageLockMode.ReadOnly, LockFormat(frame));
            try
            {
                if (data.Stride == region.Width * 4) Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
                else for (int y = 0; y < region.Height; y++) Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), pixels, y * region.Width, region.Width);
            }
            finally { frame.UnlockBits(data); }
            return pixels;
        }
    }
    // A witnessed discontinuity, carried unchanged into the distinct-frame verifier.
    // A rear HUD is ignored only after a large health reset and independently steady scenery.
    internal sealed class HudTransition
    {
        internal readonly Rectangle OldBounds, NewBounds;
        readonly double oldFill, newFill;
        readonly HudMaximum replacementMaximum;
        readonly bool maximumChanged;
        readonly string evidence;
        internal string Details { get { return "Fuellstand " + oldFill.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + " -> " + newFill.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "; MaximumGewechselt=" + maximumChanged + "; Vorher=" + OldBounds + "; Danach=" + NewBounds + (evidence == null ? "" : "; " + evidence); } }
        internal HudTransition(Rectangle oldBounds, Rectangle newBounds, double before, double after, HudMaximum beforeMaximum = null, HudMaximum afterMaximum = null, string witness = null)
        { maximumChanged = beforeMaximum != null && afterMaximum != null && beforeMaximum.Differs(afterMaximum); replacementMaximum = afterMaximum; OldBounds = oldBounds; NewBounds = newBounds; oldFill = before; newFill = after; evidence = witness; }
        internal static bool IsReset(double before, double after) { return before >= .015 && before <= .65 && after >= .82 && after - before >= .32; }
        internal bool Matches(Rectangle current, double fill, HudMaximum maximum = null)
        {
            if (Math.Abs(current.X - NewBounds.X) > NewBounds.Width || Math.Abs(current.Y - NewBounds.Y) > NewBounds.Width) return false;
            return maximumChanged ? maximum != null && replacementMaximum.Same(maximum) : fill >= Math.Max(oldFill + .22, newFill - .18);
        }
        internal static bool RightEdgeAligned(Bitmap frame, Rectangle area, Rectangle hud)
        {
            Rectangle r = new Rectangle(hud.X - area.X, hud.Y - area.Y, hud.Width, hud.Height);
            if (r.Left < 0 || r.Top < 0 || r.Right + 3 >= frame.Width || r.Bottom > frame.Height || r.Height < 4) return false;
            int contrast = 0;
            for (int row = 1; row <= 3; row++)
            {
                int y = r.Y + r.Height * row / 4;
                Color inside = frame.GetPixel(r.Right - 3, y), outside = frame.GetPixel(r.Right + 2, y);
                contrast += (Math.Abs(inside.R - outside.R) + Math.Abs(inside.G - outside.G) + Math.Abs(inside.B - outside.B)) / 3;
            }
            return contrast >= 36;
        }
        internal static double Fill(Bitmap frame, Rectangle area, Rectangle hud, HealthBarDetector detector)
        {
            Rectangle r = new Rectangle(hud.X - area.X, hud.Y - area.Y, hud.Width, hud.Height);
            if (r.Width < 10 || r.Height < 4 || !new Rectangle(Point.Empty, frame.Size).Contains(r)) return -1;
            int[] pixels = HudPixels.Read(frame, r);
            int[] rows = { (int)(r.Height * .16), (int)(r.Height * .33), (int)(r.Height * .67), (int)(r.Height * .84) };
            int last = -1, gaps = 0;
            for (int x = 1; x < r.Width - 1; x++)
            {
                int matches = 0;
                foreach (int y in rows)
                    if (detector.IsReferenceColor(pixels[Math.Min(r.Height - 1, y) * r.Width + x])) matches++;
                if (matches >= 2) { last = x; gaps = 0; }
                else if (++gaps > Math.Max(12, r.Width / 10)) break;
            }
            return last < 0 ? -1 : (last + 1.0) / r.Width;
        }
    }
    // Keep the two independent witnesses until they expire. An occluded number
    // must not erase the last readable identity, nor a misaligned color sample
    // erase the last aligned fill. Each witness retains its own scene and time.
    internal sealed class HudTargetMemory
    {
        sealed class Witness
        {
            internal Rectangle Bounds; internal long At; internal double Fill;
            internal HudMaximum Maximum; internal SceneSample Scene;
        }
        Witness number, fill;
        internal void Reset() { number = fill = null; }
        static bool CanCompare(Witness old, Rectangle current, long now)
        {
            return old != null && now >= old.At && now - old.At <= 100 &&
                Math.Abs(current.X - old.Bounds.X) + Math.Abs(current.Y - old.Bounds.Y) >= old.Bounds.Height * 2;
        }
        internal HudTransition Observe(Bitmap image, Rectangle bounds, double currentFill, bool aligned, HudMaximum maximum, long now, bool allowed)
        {
            Witness previous = null;
            string kind = null;
            if (allowed && maximum != null && CanCompare(number, bounds, now) && number.Maximum.Differs(maximum) && number.Scene.IsSteady(image)) { previous = number; kind = "Maximalzahl"; }
            if (previous == null && allowed && aligned && CanCompare(fill, bounds, now) && HudTransition.IsReset(fill.Fill, currentFill) && fill.Scene.IsSteady(image)) { previous = fill; kind = "Ausgerichteter Fuellstand"; }
            if (previous != null) return new HudTransition(previous.Bounds, bounds, previous.Fill, currentFill, previous.Maximum, maximum, "Zielmerkmal=" + kind + "; Referenzalter=" + (now - previous.At) + "ms");
            if (maximum != null || (aligned && currentFill >= 0))
            {
                Witness next = new Witness { Bounds = bounds, At = now, Fill = currentFill, Maximum = maximum, Scene = new SceneSample(image) };
                if (maximum != null) number = next;
                if (aligned && currentFill >= 0) fill = next;
            }
            if (number != null && now - number.At > 100) number = null;
            if (fill != null && now - fill.At > 100) fill = null;
            return null;
        }
    }
    // Compare the immutable maximum-health numerals without guessing a numeric OCR value.
    // Missing/occluded glyphs are unknown, never evidence of another structure.
    internal sealed class HudMaximum
    {
        readonly bool[][] digits;
        internal HudMaximum(bool[][] value) { digits = value; }
        static bool White(Color c) { return HealthBarDetector.NumberWhite(c.ToArgb()); }
        internal static HudMaximum Read(Bitmap frame, Rectangle area, Rectangle hud)
        {
            Rectangle r = new Rectangle(hud.X - area.X, hud.Y - area.Y, hud.Width, hud.Height);
            if (r.Height < 6 || !new Rectangle(Point.Empty, frame.Size).Contains(r)) return null;
            int w = (int)(r.Width * .65), h = r.Height; bool[,] white = new bool[w, h]; int[] columns = new int[w];
            int[] pixels = HudPixels.Read(frame, new Rectangle(r.X, r.Y, w, h));
            for (int y = 1; y < h - 1; y++) for (int x = 2; x < w; x++) if (White(Color.FromArgb(pixels[y * w + x]))) { white[x, y] = true; columns[x]++; }
            List<bool[]> values = new List<bool[]>(); int maximumLeft = -1;
            for (int x = w - 1; x >= 2 && values.Count < 3; )
            {
                while (x >= 2 && columns[x] == 0) x--; if (x < 2) break; int right = x;
                while (x >= 2 && columns[x] > 0) x--; int left = x + 1, top = h, bottom = -1;
                for (int yy = 1; yy < h - 1; yy++) for (int xx = left; xx <= right; xx++) if (white[xx, yy]) { top = Math.Min(top, yy); bottom = Math.Max(bottom, yy); }
                int gh = bottom - top + 1, gw = right - left + 1;
                // Antialiasing can join two adjacent digits by one pixel. Keep
                // the trailing digit at its observed font aspect, then revisit the rest.
                if (gw > gh && gw <= gh * 1.8) { left = right - Math.Max(2, (int)Math.Round(gh * .65)) + 1; x = left - 1; gw = right - left + 1; }
                if (gw > gh || gh > h * .75 && gw <= Math.Max(2, h / 5)) continue;
                if (gw > h * .8 || gh < h * .4 || top < h / 6 || bottom >= h - h / 6) return null;
                bool[] mask = new bool[54];
                for (int yy = 0; yy < 9; yy++) for (int xx = 0; xx < 6; xx++) mask[yy * 6 + xx] = white[left + Math.Min(gw - 1, (xx * gw + gw / 2) / 6), top + Math.Min(gh - 1, (yy * gh + gh / 2) / 9)];
                values.Add(mask); maximumLeft = left;
            }
            if (values.Count != 3) return null;
            int gap = 0; for (int x = maximumLeft - 1; x >= 2 && columns[x] == 0; x--) gap++;
            return gap >= h * .3 ? new HudMaximum(values.ToArray()) : null;
        }
        static int Unmatched(bool[] a, bool[] b)
        {
            int count = 0;
            for (int y = 0; y < 9; y++) for (int x = 0; x < 6; x++) if (a[y * 6 + x])
            {
                bool near = b[y * 6 + x] || (x > 0 && b[y * 6 + x - 1]) || (x < 5 && b[y * 6 + x + 1]) || (y > 0 && b[(y - 1) * 6 + x]) || (y < 8 && b[(y + 1) * 6 + x]);
                if (!near) count++;
            }
            return count;
        }
        double Difference(HudMaximum other, int digit)
        { return (Unmatched(digits[digit], other.digits[digit]) + Unmatched(other.digits[digit], digits[digit])) / 54.0; }
        internal bool Same(HudMaximum other)
        { if (other == null) return false; for (int d = 0; d < 3; d++) if (Difference(other, d) > .08) return false; return true; }
        internal bool Differs(HudMaximum other)
        { if (other == null) return false; int same = 0, changed = 0; for (int d = 0; d < 3; d++) { double diff = Difference(other, d); if (diff <= .08) same++; if (diff >= .15) changed++; } return same >= 2 && changed == 1; }
    }
    // Sparse background patches; exclude the central building/reticle and the player.
    // Unknown or textureless scenery does not authorize a target handoff.
    internal sealed class SceneSample
    {
        readonly int[] rgb;
        const int W = 96, H = 54;
        internal SceneSample(Bitmap frame)
        {
            rgb = new int[W * H];
            int[] row = new int[frame.Width];
            BitmapData data = frame.LockBits(new Rectangle(Point.Empty, frame.Size), ImageLockMode.ReadOnly, HudPixels.LockFormat(frame));
            try
            {
                for (int y = 0; y < H; y++)
                {
                    Marshal.Copy(IntPtr.Add(data.Scan0, (y * frame.Height / H) * data.Stride), row, 0, row.Length);
                    for (int x = 0; x < W; x++) rgb[y * W + x] = row[x * frame.Width / W];
                }
            }
            finally { frame.UnlockBits(data); }
        }
        static int Difference(int a, int b) { return (Math.Abs(((a >> 16) & 255) - ((b >> 16) & 255)) + Math.Abs(((a >> 8) & 255) - ((b >> 8) & 255)) + Math.Abs((a & 255) - (b & 255))) / 3; }
        internal bool IsSteady(Bitmap frame)
        {
            SceneSample next = new SceneSample(frame); List<int> dxs = new List<int>(), dys = new List<int>();
            foreach (int cy in new int[] { 9, 21, 33 }) foreach (int cx in new int[] { 10, 25, 70, 85 })
            {
                int contrast = 0;
                for (int y = -2; y <= 2; y++) for (int x = -3; x <= 3; x++) contrast += Difference(rgb[(cy + y) * W + cx + x], rgb[cy * W + cx]);
                if (contrast < 250) continue;
                int best = Int32.MaxValue, bx = 0, by = 0;
                for (int dy = -4; dy <= 4; dy++) for (int dx = -4; dx <= 4; dx++)
                {
                    int error = 0;
                    for (int y = -2; y <= 2; y++) for (int x = -3; x <= 3; x++) error += Difference(rgb[(cy + y) * W + cx + x], next.rgb[(cy + y + dy) * W + cx + x + dx]);
                    if (error < best || (error == best && Math.Abs(dx) + Math.Abs(dy) < Math.Abs(bx) + Math.Abs(by))) { best = error; bx = dx; by = dy; }
                }
                if (best <= 35 * 23) { dxs.Add(bx); dys.Add(by); }
            }
            if (dxs.Count < 4) return false;
            dxs.Sort(); dys.Sort(); return Math.Abs(dxs[dxs.Count / 2]) <= 1 && Math.Abs(dys[dys.Count / 2]) <= 1;
        }
    }    internal static class HudTransitionTests
    {
        static void Check(bool value, string reason) { if (!value) throw new Exception("Target continuity: " + reason); }
        static HudMaximum NativeMaximum(params string[] masks)
        {
            bool[][] glyphs = new bool[masks.Length][];
            for (int d = 0; d < masks.Length; d++)
            { glyphs[d] = new bool[54]; for (int p = 0; p < 54; p++) glyphs[d][p] = masks[d][p] == '1'; }
            return new HudMaximum(glyphs);
        }
        internal static void Run()
        {
            Check(HudTransition.IsReset(.4, .99) && !HudTransition.IsReset(.95, .99) && !HudTransition.IsReset(.5, .6) && !HudTransition.IsReset(.5, -.1), "only a substantial readable health reset qualifies");
            Size client = new Size(1920, 1080); Rectangle area = HealthBarDetector.SearchArea(new Rectangle(Point.Empty, client));
            Config config = new Config { HasFirst = true, HasSecond = true, FirstColor = 0x5CC442, SecondColor = 0xDA943D };
            HealthBarDetector detector = new HealthBarDetector(config, 25);
            using (Bitmap frame = new Bitmap(area.Width, area.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(frame))
            {
                Action<int, int, bool, int> draw = delegate(int x, int y, bool full, int shift)
                {
                    for (int yy = 0; yy < frame.Height; yy += 18) for (int xx = -shift; xx < frame.Width; xx += 18)
                    { int v = ((xx + shift) / 18 * 37 + yy / 18 * 53) % 110; using (Brush b = new SolidBrush(Color.FromArgb(60 + v, 40 + v, 35 + v))) g.FillRectangle(b, xx, yy, 18, 18); }
                    using (Brush bg = new SolidBrush(Color.FromArgb(60, 68, 90))) g.FillRectangle(bg, x, y, 126, 12);
                    using (Brush fill = new SolidBrush(full ? Color.FromArgb(92, 196, 66) : Color.FromArgb(218, 148, 61))) g.FillRectangle(fill, x, y, full ? 126 : 50, 12);
                    BarTrackerTests.DrawDigits(g, full ? "150 150" : "75 150", x + 3, y + 1);
                };
                HealthBarTracker tracker = new HealthBarTracker(); PrefireGate gate = new PrefireGate(20); BarObservation observed = null;
                draw(800, 430, true, 0);
                foreach (int t in new int[] { 0, 20, 40 }) { observed = tracker.Update(frame, area, client, detector, t); gate.RequestFreshConfirmation(true, observed.Present, observed.Stable, t); }
                Check(observed.Stable, "foreground target is acquired");
                Check(HudTransition.RightEdgeAligned(frame, area, observed.Bounds), "real bar has a closed right edge");
                Rectangle shifted = observed.Bounds; shifted.X -= 40;
                Check(!HudTransition.RightEdgeAligned(frame, area, shifted), "a stale bar position inside a full fill cannot supply a low-health reset"); draw(800, 430, false, 0); observed = tracker.Update(frame, area, client, detector, 50);
                draw(830, 340, true, 0); observed = tracker.Update(frame, area, client, detector, 60);
                Check(!observed.Present && observed.Transition != null && gate.RequestFreshConfirmation(true, false, observed.Stable, 60), "rear HUD cannot replace damaged foreground target");
                draw(830, 340, true, 0);
                Check(ShotVerifier.FrameStillMissing(frame, area, client, observed.Bounds, detector, observed.Transition), "distinct rear-HUD frame preserves the witnessed target loss");
                string guardReason; Check(ShotVerifier.BuildMayProceed(frame, area, client, observed.Bounds, detector, new Point(client.Width / 2, client.Height / 2), out guardReason, observed.Transition), "rear HUD must not suppress building after a witnessed foreground loss");
                Check(!ShotVerifier.FrameStillMissing(frame, area, client, observed.Bounds, detector), "without witnessed target loss the same frame remains a conservative veto");
                draw(800, 430, false, 0);
                Check(!ShotVerifier.FrameStillMissing(frame, area, client, observed.Bounds, detector, observed.Transition), "restored original foreground HUD cancels the shot");
                tracker.Reset(); draw(800, 430, true, 0);
                foreach (int t in new int[] { 100, 120, 140 }) observed = tracker.Update(frame, area, client, detector, t);
                Check(observed.Stable, "camera test starts from acquired target"); draw(800, 430, false, 0); tracker.Update(frame, area, client, detector, 150);
                draw(830, 340, true, 120); observed = tracker.Update(frame, area, client, detector, 160);
                Check(observed.Present && observed.Transition == null, "large camera movement does not become a target-loss witness");
                tracker.Reset(); draw(800, 430, true, 0);
                foreach (int t in new int[] { 200, 220, 240 }) tracker.Update(frame, area, client, detector, t);
                draw(800, 430, true, 0); observed = tracker.Update(frame, area, client, detector, 260);
                Check(observed.Present && observed.Transition == null, "health recovery at the same HUD position does not trigger");
                // A transiently occluded maximum must not replace target identity.
                Rectangle oldHud = new Rectangle(area.X + 800, area.Y + 430, 126, 16);
                Rectangle rearHud = new Rectangle(area.X + 830, area.Y + 340, 126, 16);
                Action<int, int, string> panel = delegate(int x, int y, string text)
                {
                    draw(x, y, true, 0);
                    using (Brush green = new SolidBrush(Color.FromArgb(92, 196, 66))) g.FillRectangle(green, x, y, 126, 16);
                    BarTrackerTests.DrawDigits(g, text, x + 3, y + 3);
                };
                panel(800, 430, "140 140");
                // Original 6x9 signatures from native 64/140 and 150/150 crops
                // 000123564-cc22d8.png and 000124086-06d01e.png (17:50 session).
                // Isolate memory behavior from the deliberately simplified test font.
                HudMaximum maximum140 = NativeMaximum("001110011011010011010001110001010001010011011111001110", "000110000110001110001110010110110110111111000110000110", "000111011111111111000111000111000111000111000111000111");
                HudTargetMemory memory = new HudTargetMemory();
                Check(memory.Observe(frame, oldHud, 1, true, maximum140, 0, true) == null, "memory begins without a transition");
                panel(800, 430, "140 140");
                using (Brush green = new SolidBrush(Color.FromArgb(92, 196, 66))) g.FillRectangle(green, 847, 433, 6, 10);
                HudMaximum obscured = HudMaximum.Read(frame, area, oldHud);
                Check(obscured == null, "intermediate panel has an unreadable maximum");
                Check(memory.Observe(frame, oldHud, 1, true, obscured, 20, true) == null, "occlusion alone never authorizes a shot");
                panel(830, 340, "150 150");
                HudMaximum maximum150 = NativeMaximum("011100111110110010100011100011100011110010110110011100", "111111111111110000110000111111000001000001100011111110", "000011011111111111000111000111000111000111000111000111");
                Check(maximum140.Differs(maximum150), "replacement maximum is different");
                HudTransition remembered = memory.Observe(frame, rearHud, 1, true, maximum150, 40, true);
                Check(remembered != null && remembered.OldBounds == oldHud, "readable old identity survives an unknown intermediate frame");
                Check(remembered.Matches(rearHud, 1, maximum150) && !remembered.Matches(oldHud, 1, maximum140), "replacement does not erase original identity");

                memory.Reset(); draw(800, 430, false, 0);
                memory.Observe(frame, oldHud, .4, true, null, 0, true);
                memory.Observe(frame, oldHud, .06, false, null, 20, true);
                draw(830, 340, true, 0);
                Check(memory.Observe(frame, rearHud, 1, true, null, 40, true) != null, "misaligned intermediate fill cannot erase an aligned damaged-target witness");
                memory.Reset(); memory.Observe(frame, oldHud, .06, false, maximum150, 0, true);
                Check(memory.Observe(frame, rearHud, 1, true, maximum150, 20, true) == null, "an unaligned low reading alone cannot fabricate target loss");
                memory.Reset(); draw(800, 430, true, 0);
                memory.Observe(frame, oldHud, 1, true, maximum140, 0, true);
                memory.Observe(frame, oldHud, 1, true, null, 20, true);
                draw(830, 340, true, 0);
                Check(memory.Observe(frame, rearHud, 1, true, maximum150, 101, true) == null, "unknown frames cannot extend identity beyond 100ms");
                memory.Reset(); draw(800, 430, true, 0);
                memory.Observe(frame, oldHud, 1, true, maximum140, 0, true);
                draw(830, 340, true, 120);
                Check(memory.Observe(frame, rearHud, 1, true, maximum150, 40, true) == null, "camera movement vetoes a remembered identity change");
                memory.Reset(); draw(800, 430, true, 0);
                memory.Observe(frame, oldHud, 1, true, maximum140, 0, true);
                draw(830, 340, true, 0);
                Check(memory.Observe(frame, rearHud, 1, true, maximum150, 40, false) == null, "disabled handoff never uses memory to request a shot");
                memory.Reset();
                Check(memory.Observe(frame, rearHud, 1, true, maximum150, 50, true) == null, "reset removes all former target witnesses");
            }
        }
    }

}

