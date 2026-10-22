using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;

namespace PrefireMacro
{
    internal sealed class BuildHudSample
    {
        internal bool AmmoPresent, BuildReady;
        internal byte[] Ammo = new byte[48 * 20];
        internal int AmmoPixels, BuildIcons;
    }
    internal sealed class BuildHudReader
    {
        readonly int[] pixels = new int[320 * 110];
        readonly bool[] white = new bool[320 * 110], blue = new bool[320 * 110], purple = new bool[320 * 110];
        sealed class Blob { internal Rectangle Bounds; internal int Count; }
        static void Enqueue(bool[] mask, bool[] seen, int[] queue, int index, ref int write)
        { if (mask[index] && !seen[index]) { seen[index] = true; queue[write++] = index; } }
        static List<Blob> Blobs(bool[] mask, Rectangle region, int w, int h)
        {
            bool[] seen = new bool[w * h]; int[] queue = new int[region.Width * region.Height]; List<Blob> blobs = new List<Blob>();
            for (int y = region.Top; y < region.Bottom; y++) for (int x = region.Left; x < region.Right; x++)
            {
                int start = y * w + x; if (!mask[start] || seen[start]) continue;
                int read = 0, write = 1, minX = x, minY = y, maxX = x, maxY = y; queue[0] = start; seen[start] = true;
                while (read < write)
                {
                    int index = queue[read++], xx = index % w, yy = index / w;
                    minX = Math.Min(minX, xx); maxX = Math.Max(maxX, xx); minY = Math.Min(minY, yy); maxY = Math.Max(maxY, yy);
                    if (xx > region.Left) Enqueue(mask, seen, queue, index - 1, ref write);
                    if (xx + 1 < region.Right) Enqueue(mask, seen, queue, index + 1, ref write);
                    if (yy > region.Top) Enqueue(mask, seen, queue, index - w, ref write);
                    if (yy + 1 < region.Bottom) Enqueue(mask, seen, queue, index + w, ref write);
                }
                blobs.Add(new Blob { Count = write, Bounds = new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1) });
            }
            return blobs;
        }
        // Canonical lower-right HUD crop: x=960..1280, y=580..690 at 1280x720.
        internal BuildHudSample Read(Bitmap hud)
        {
            if (hud.Width != 320 || hud.Height != 110) throw new ArgumentException("HUD crop must be 320x110.");
            BitmapData data = hud.LockBits(new Rectangle(0, 0, 320, 110), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try { for (int y = 0; y < 110; y++) Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), pixels, y * 320, 320); }
            finally { hud.UnlockBits(data); }
            for (int i = 0; i < pixels.Length; i++)
            {
                int rgb = pixels[i], r = (rgb >> 16) & 255, g = (rgb >> 8) & 255, b = rgb & 255;
                white[i] = Math.Min(r, Math.Min(g, b)) > 185 && Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) < 65;
                blue[i] = b - r > 45 && g - r > 25 && g > 115 && b > 160;
                purple[i] = b - r > 70 && b - g > 45 && g > 55 && b > 145;
            }
            BuildHudSample result = new BuildHudSample();
            result.BuildIcons = CountBuildIcons(blue, purple);
            result.BuildReady = result.BuildIcons >= 3;
            List<Blob> digits = Blobs(white, new Rectangle(16, 58, 136, 42), 320, 110);
            digits.RemoveAll(delegate(Blob a) { return a.Count < 14 || a.Bounds.Height < 9 || a.Bounds.Height > 18 || a.Bounds.Width < 4 || a.Bounds.Width > 19; });
            digits.Sort(delegate(Blob a, Blob b) { return a.Bounds.X.CompareTo(b.Bounds.X); });
            if (digits.Count > 0)
            {
                Rectangle number = digits[0].Bounds;
                for (int i = 1; i < digits.Count; i++)
                {
                    Rectangle next = digits[i].Bounds;
                    if (next.X - number.Right > 4 || Math.Abs(next.Y - number.Y) > 2 || Math.Abs(next.Height - number.Height) > 3) break;
                    number = Rectangle.Union(number, next);
                }
                if (number.Width <= 44 && number.Height <= 18)
                    for (int y = 0; y < number.Height; y++) for (int x = 0; x < number.Width; x++)
                        if (white[(number.Y + y) * 320 + number.X + x]) { result.Ammo[y * 48 + x] = 1; result.AmmoPixels++; }
                result.AmmoPresent = result.AmmoPixels >= 14 && result.AmmoPixels <= 350;
            }
            return result;
        }
        static int CountBuildIcons(bool[] color, bool[] alternate)
        {
            int best = 0;
            List<Blob> icons = Blobs(color, new Rectangle(140, 25, 176, 47), 320, 110);
            icons.AddRange(Blobs(alternate, new Rectangle(140, 25, 176, 47), 320, 110));
            icons.RemoveAll(delegate(Blob a) { return a.Count < 45 || a.Bounds.Width < 12 || a.Bounds.Width > 30 || a.Bounds.Height < 11 || a.Bounds.Height > 31; });
            icons.Sort(delegate(Blob a, Blob b) { return a.Bounds.X.CompareTo(b.Bounds.X); });
            for (int i = 0; i < icons.Count; i++)
            {
                if (icons[i].Bounds.X > 235) continue;
                int chain = 1; Rectangle previous = icons[i].Bounds;
                for (int j = i + 1; j < icons.Count; j++)
                {
                    Rectangle next = icons[j].Bounds;
                    double gap = next.X + next.Width * .5 - previous.X - previous.Width * .5;
                    if (gap >= 28 && gap <= 38 && Math.Abs(next.Y + next.Height * .5 - previous.Y - previous.Height * .5) <= 8)
                    { chain++; previous = next; }
                }
                best = Math.Max(best, chain);
            }
            return best;
        }
        static bool Near(byte[] mask, int x, int y)
        {
            for (int yy = Math.Max(0, y - 1); yy <= Math.Min(19, y + 1); yy++)
                for (int xx = Math.Max(0, x - 1); xx <= Math.Min(47, x + 1); xx++) if (mask[yy * 48 + xx] != 0) return true;
            return false;
        }
        internal static bool AmmoChanged(BuildHudSample before, BuildHudSample after)
        {
            if (!before.AmmoPresent || !after.AmmoPresent || after.BuildReady) return false;
            int changed = 0, rawChanged = 0;
            for (int y = 0; y < 20; y++) for (int x = 0; x < 48; x++)
            {
                if (before.Ammo[y * 48 + x] != 0 && !Near(after.Ammo, x, y)) changed++;
                if (after.Ammo[y * 48 + x] != 0 && !Near(before.Ammo, x, y)) changed++;
                if (before.Ammo[y * 48 + x] != after.Ammo[y * 48 + x]) rawChanged++;
            }
            return changed >= 2 && rawChanged >= Math.Max(12, (before.AmmoPixels + after.AmmoPixels) / 5);
        }
    }
    internal sealed class BuildHudCapture : IDisposable
    {
        readonly IntPtr window; readonly Rectangle client;
        readonly BuildHudReader parser = new BuildHudReader();
        Bitmap raw; readonly Bitmap normalized = new Bitmap(320, 110, PixelFormat.Format32bppArgb);
        internal BuildHudCapture(IntPtr window)
        {
            this.window = window;
            Rectangle bounds;
            if (!HealthBarCapture.ClientBounds(window, out bounds)) throw new InvalidOperationException("Spielbereich nicht verfügbar.");
            client = bounds;
        }
        internal BuildHudSample Read()
        {
            Rectangle current;
            if (!HealthBarCapture.ClientBounds(window, out current) || current != client) throw new InvalidOperationException("Spielbereich während Baufolge verändert.");
            Rectangle area = new Rectangle(client.Left + (int)Math.Round(client.Width * .75), client.Top + (int)Math.Round(client.Height * 580.0 / 720), (int)Math.Round(client.Width * .25), (int)Math.Round(client.Height * 110.0 / 720));
            if (raw == null) raw = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(raw)) g.CopyFromScreen(area.Location, Point.Empty, area.Size);
            using (Graphics g = Graphics.FromImage(normalized)) { g.InterpolationMode = InterpolationMode.HighQualityBilinear; g.DrawImage(raw, new Rectangle(0, 0, 320, 110)); }
            return parser.Read(normalized);
        }
        public void Dispose() { if (raw != null) raw.Dispose(); normalized.Dispose(); }
    }
    internal sealed class AdaptiveBuildTiming : IDisposable
    {
        readonly Func<BuildHudSample> read;
        readonly Func<long> clock;
        readonly Action<int> wait;
        readonly int key, fixedBuild, fixedPlace;
        readonly IDisposable capture;
        BuildHudSample baseline;
        internal string Mode = "Adaptive", Details = "Noch nicht geprüft";
        internal bool Usable;
        internal AdaptiveBuildTiming(IntPtr window, int buildKey, int buildMs, int placeMs)
        {
            BuildHudCapture live = new BuildHudCapture(window); capture = live;
            read = live.Read; clock = ShotVerifier.ClockMs; wait = Thread.Sleep;
            key = buildKey; fixedBuild = buildMs; fixedPlace = placeMs;
        }
        internal AdaptiveBuildTiming(Func<BuildHudSample> reader, Func<long> timer, Action<int> sleeper, int buildKey, int buildMs, int placeMs)
        { read = reader; clock = timer; wait = sleeper; key = buildKey; fixedBuild = buildMs; fixedPlace = placeMs; }
        internal void Prepare()
        {
            try { baseline = read(); Usable = baseline.AmmoPresent && !baseline.BuildReady; }
            catch (Exception ex) { Details = "HUD-Aufnahme: " + ex.Message; Usable = false; }
            if (!Usable) { Mode = "FixedFallback"; Details = "Ausgangs-HUD nicht lesbar oder bereits im Baumodus; feste Zeiten."; }
            else Details = "Auto";
        }
        internal void WaitFor(bool building, long startedAt, Func<bool> allowed)
        {
            int minimum = building ? 10 : 35;
            int fixedDelay = Math.Max(minimum, building ? fixedPlace : fixedBuild);
            // HUD animations lag the actual input. They may shorten a user-set
            // wait, but must not hold up the sequence for another 200 ms.
            int deadline = Usable ? fixedDelay + 20 : fixedDelay;
            int consecutive = 0;
            while (true)
            {
                if (!allowed()) throw new OperationCanceledException();
                long elapsed = clock() - startedAt;
                if (!Usable) { if (elapsed >= deadline) return; wait(1); continue; }
                if (elapsed >= deadline) { UseBoundedFallback(building, elapsed); return; }
                if (elapsed >= minimum)
                {
                    BuildHudSample sample = read();
                    bool ready = building ? sample.BuildReady : BuildHudReader.AmmoChanged(baseline, sample);
                    consecutive = ready ? consecutive + 1 : 0;
                    if (consecutive >= 2 && clock() - startedAt < deadline)
                    {
                        Details += "; " + (building ? "Baumodus" : "Schussanzeige") + " nach " + (clock() - startedAt) + " ms";
                        return;
                    }
                }
                if (clock() - startedAt >= deadline) { UseBoundedFallback(building, clock() - startedAt); return; }
                wait(4);
            }
        }
        void UseBoundedFallback(bool building, long elapsed)
        {
            Mode = "AdaptiveBoundedFallback";
            Details += "; " + (building ? "Baumodus" : "Schussanzeige") + " unbestätigt, begrenzte Wartezeit " + elapsed + " ms; Eingabefolge fortgesetzt";
        }
        internal void BeforeStep(InputStep step, SequenceResult result, Func<bool> allowed)
        {
            if (!step.Down || !result.ShotSent) return;
            if (step.Key == key && !result.BuildSent) WaitFor(false, (long)Math.Ceiling(result.FirstDownTimestamp * (1000.0 / Stopwatch.Frequency)), allowed);
            else if (step.Key == 1 && result.BuildSent && !result.PlaceSent) WaitFor(true, (long)Math.Ceiling(result.BuildDownTimestamp * (1000.0 / Stopwatch.Frequency)), allowed);
        }
        public void Dispose() { if (capture != null) capture.Dispose(); }
    }
}
