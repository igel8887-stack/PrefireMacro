using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Threading;

namespace PrefireMacro
{
    internal sealed class BarDetection
    {
        public bool Found;
        public int ColorNumber;
        public Rectangle Bounds;
        public double Score;
        public bool InvalidFrame;
        public bool ReticleOccluded;
    }

    // Detect a thin, filled HUD bar. Calibration supplies colors, never screen coordinates.
    internal sealed class HealthBarDetector
    {
        readonly byte[] lookup = new byte[32768];
        byte[] mask;
        int[] pixels;
        int width, height;
        HealthBarDetector[] workers;
        sealed class SearchFloor
        {
            double score = Double.NegativeInfinity;
            internal volatile bool Ready;
            internal double Score { get { return Volatile.Read(ref score); } }
            internal void Raise(double value)
            {
                double previous = Score;
                while (value > previous)
                { double observed = Interlocked.CompareExchange(ref score, value, previous); if (observed == previous) { Ready = true; return; } previous = observed; }
            }
        }
        SearchFloor searchFloor;
        readonly int[] nonBlackCounts = new int[4];
        int[] textColumns, columnTop, columnBottom;
        Rectangle seedReticleInk;
        readonly HashSet<long> seedChecks = new HashSet<long>();
        readonly Dictionary<long, bool> hudChecks = new Dictionary<long, bool>();
        int[] tailIntegral, referenceIntegral; Rectangle tailArea; int tailStride;
        ulong[] whiteColumnBits;
        internal List<string> HudDebug;
        bool HudReject(string reason, int x, int y, int h) { if (HudDebug != null && HudDebug.Count < 300) HudDebug.Add(x + "," + y + ",h=" + h + ": " + reason); return false; }
        internal HealthBarDetector(Config config, int tolerance)
        {
            List<Color> first = Palette(config.HasFirst, config.FirstColor, config.FirstSamples);
            List<Color> second = Palette(config.HasSecond, config.SecondColor, config.SecondSamples);
            for (int i = 0; i < lookup.Length; i++)
            {
                int r = ((i >> 10) & 31) * 8 + 4, g = ((i >> 5) & 31) * 8 + 4, b = (i & 31) * 8 + 4;
                Color color = Color.FromArgb(r, g, b);
                if (MatchesPalette(color, first, tolerance)) lookup[i] |= 1;
                if (MatchesPalette(color, second, tolerance)) lookup[i] |= 2;
            }
        }
        static List<Color> Palette(bool enabled, int reference, List<PixelSample> samples)
        {
            List<Color> colors = new List<Color>();
            if (!enabled) return colors;
            List<int> values = new List<int>(); values.Add(reference);
            if (samples != null) foreach (PixelSample sample in samples) if (sample != null) values.Add(sample.Rgb);
            foreach (int rgb in values)
            {
                Color color = Color.FromArgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
                // White health numbers and the dark bar background are not color references.
                int max = Math.Max(color.R, Math.Max(color.G, color.B)), min = Math.Min(color.R, Math.Min(color.G, color.B));
                if (max < 45 || max - min < 35) continue;
                bool duplicate = false;
                foreach (Color existing in colors)
                    if (Math.Abs(existing.R - color.R) + Math.Abs(existing.G - color.G) + Math.Abs(existing.B - color.B) < 48) duplicate = true;
                if (!duplicate) colors.Add(color);
                if (colors.Count == 8) break;
            }
            return colors;
        }
        static bool MatchesPalette(Color color, List<Color> references, int tolerance)
        {
            int max = Math.Max(color.R, Math.Max(color.G, color.B)), min = Math.Min(color.R, Math.Min(color.G, color.B));
            if (max < 45 || max - min < 35) return false;
            float saturation = max == 0 ? 0 : (float)(max - min) / max;
            float hue = color.GetHue();
            foreach (Color reference in references)
            {
                if (Math.Abs(color.R - reference.R) <= tolerance + 4 && Math.Abs(color.G - reference.G) <= tolerance + 4 && Math.Abs(color.B - reference.B) <= tolerance + 4) return true;
                int refMax = Math.Max(reference.R, Math.Max(reference.G, reference.B)), refMin = Math.Min(reference.R, Math.Min(reference.G, reference.B));
                float hueDistance = Math.Abs(hue - reference.GetHue()); hueDistance = Math.Min(hueDistance, 360 - hueDistance);
                // Accommodate the bar gradient and differences between recording and live HUD.
                if (hueDistance <= Math.Min(28, 8 + tolerance * .55) && Math.Abs(saturation - (float)(refMax - refMin) / refMax) <= .28 + tolerance / 500.0 && Math.Abs(max - refMax) <= 35 + tolerance * 2) return true;
            }
            return false;
        }
        internal static Rectangle SearchArea(Rectangle client)
        {
            // Looking up at a nearby wall or ramp moves its HP panel toward the
            // bottom of the screen. Keep that panel inside the capture region.
            return new Rectangle(client.Left + (int)(client.Width * .05), client.Top + (int)(client.Height * .15), Math.Max(1, (int)Math.Round(client.Width * .90)), Math.Max(1, (int)Math.Round(client.Height * .80)));
        }
        byte At(int x, int y) { return mask[y * width + x]; }
        bool RowSupports(int y, int start, int length, byte color)
        {
            if (y < 0 || y >= height) return false;
            int count = 0;
            for (int i = 1; i <= 7; i++) if ((At(start + length * i / 8, y) & color) != 0) count++;
            return count >= 4;
        }
        internal bool IsReferenceColor(int rgb)
        {
            int index = (((rgb >> 19) & 31) << 10) | (((rgb >> 11) & 31) << 5) | ((rgb >> 3) & 31);
            return lookup[index] != 0;
        }
        internal void Prepare(Size size)
        {
            int count = size.Width * size.Height;
            if (pixels == null || pixels.Length < count) { pixels = new int[count]; mask = new byte[count]; }
        }
        internal BarDetection Find(Bitmap image, Rectangle screenArea, Size clientSize, Rectangle? tracked = null, bool requireHud = false, Rectangle? verifiedHud = null, Rectangle? sourceRegion = null, Point? reticleCenter = null)
        {
            Rectangle source = sourceRegion ?? new Rectangle(Point.Empty, image.Size);
            hudChecks.Clear();
            tailArea = Rectangle.Empty;
            width = source.Width; height = source.Height;
            int size = width * height;
            Prepare(source.Size);
            BitmapData data = image.LockBits(source, ImageLockMode.ReadOnly, HudPixels.LockFormat(image));
            int nonBlack = 0;
            try
            {
                if (data.Stride == width * 4 && size >= 500000 && Environment.ProcessorCount >= 4)
                {
                    // Each worker owns the copied pixels and mask in its slice.
                    // Search starts only after all four slices are complete.
                    Parallel.For(0, 4, new ParallelOptions { MaxDegreeOfParallelism = 4 }, delegate(int n)
                    {
                        int first = size * n / 4, end = size * (n + 1) / 4;
                        Marshal.Copy(IntPtr.Add(data.Scan0, first * 4), pixels, first, end - first);
                        nonBlackCounts[n] = BuildMask(first, end);
                    });
                    nonBlack = nonBlackCounts[0] + nonBlackCounts[1] + nonBlackCounts[2] + nonBlackCounts[3];
                }
                else
                {
                    if (data.Stride == width * 4) Marshal.Copy(data.Scan0, pixels, 0, size);
                    else for (int y = 0; y < height; y++) Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), pixels, y * width, width);
                    nonBlack = BuildMask(0, size);
                }
            }
            finally { image.UnlockBits(data); }
            if ((long)nonBlack * 100 < (long)size * 2) return new BarDetection { InvalidFrame = true };
            if (size >= 500000 && Environment.ProcessorCount >= 4 && HudDebug == null)
            {
                const int parts = 16;
                if (workers == null)
                { workers = new HealthBarDetector[parts]; for (int n = 0; n < parts; n++) workers[n] = new HealthBarDetector(); }
                BarDetection[] found = new BarDetection[parts];
                int rows = (height - 1) / 2;
                SearchFloor floor = new SearchFloor();
                int[] order = new int[parts]; for (int n = 0; n < parts; n++) order[n] = n;
                double aimY = reticleCenter.HasValue ? reticleCenter.Value.Y - screenArea.Y : clientSize.Height * .35;
                Array.Sort(order, delegate(int a, int b) { return Math.Abs(height * (a + .5) / parts - aimY).CompareTo(Math.Abs(height * (b + .5) / parts - aimY)); });
                Parallel.For(0, parts, new ParallelOptions { MaxDegreeOfParallelism = 4 }, delegate(int n)
                {
                    int part = order[n]; HealthBarDetector worker = workers[part]; worker.pixels = pixels; worker.mask = mask; worker.width = width; worker.height = height; worker.searchFloor = floor;
                    found[part] = worker.Scan(screenArea, clientSize, tracked, requireHud, verifiedHud, reticleCenter, 1 + 2 * (rows * part / parts), Math.Min(height - 1, 1 + 2 * (rows * (part + 1) / parts)));
                    worker.searchFloor = null;
                });
                BarDetection best = new BarDetection();
                foreach (BarDetection candidate in found) if (candidate.Found && (!best.Found || candidate.Score > best.Score)) best = candidate;
                return best;
            }
            return Scan(screenArea, clientSize, tracked, requireHud, verifiedHud, reticleCenter, 1, height - 1);
        }
        int BuildMask(int start, int end)
        {
            int nonBlack = 0;
            for (int i = start; i < end; i++)
            {
                int rgb = pixels[i];
                if ((rgb & 0x00F0F0F0) != 0) nonBlack++;
                int index = ((rgb >> 9) & 0x7C00) | ((rgb >> 6) & 0x3E0) | ((rgb >> 3) & 31);
                mask[i] = lookup[index];
            }
            return nonBlack;
        }
        HealthBarDetector() { }
        BarDetection Scan(Rectangle screenArea, Size clientSize, Rectangle? tracked, bool requireHud, Rectangle? verifiedHud, Point? reticleCenter, int startRow, int endRow)
        {
            int minWidth = requireHud ? 1 : Math.Max(20, clientSize.Width / 64), maxWidth = Math.Max(100, clientSize.Width / 6);
            int minHeight = requireHud ? Math.Max(5, clientSize.Height / 90) : Math.Max(4, clientSize.Height / 300), maxHeight = requireHud ? Math.Max(10, clientSize.Height / 70) : Math.Max(18, clientSize.Height / 48);
            BarDetection best = new BarDetection(); seedChecks.Clear(); hudChecks.Clear(); tailArea = Rectangle.Empty;
            Point aim = reticleCenter ?? new Point((int)(screenArea.X + clientSize.Width * .45), (int)(screenArea.Y + clientSize.Height * .35));
            int inkRadius = Math.Max(14, clientSize.Height * 35 / 1000);
            seedReticleInk = new Rectangle(aim.X - screenArea.X - inkRadius, aim.Y - screenArea.Y - inkRadius, inkRadius * 2, inkRadius * 2);
            double expectedRow = reticleCenter.HasValue ? reticleCenter.Value.Y - screenArea.Y : clientSize.Height * .35;
            for (int y = startRow; y < endRow; y += 2)
            {
                // Keep reticle-recovery rows in their original order: recovery
                // has a per-worker memo. Other rows have no such scan side effect.
                if (HudDebug == null && (best.Found || searchFloor != null && searchFloor.Ready) &&
                    (y < seedReticleInk.Top - maxHeight - 2 || y > seedReticleInk.Bottom + maxHeight + 2))
                {
                    double bound = 1000 + maxWidth * .3 - Math.Max(0, Math.Abs(y - expectedRow) - maxHeight - 2);
                    double floor = searchFloor == null ? best.Score : searchFloor.Score;
                    if (bound < floor) continue;
                }
                int x = 0;
                while (x < width)
                {
                    byte bits = At(x, y);
                    if (bits == 0) { x++; continue; }
                    byte color = (byte)((bits & 1) != 0 ? 1 : 2);
                    int start = x++, smooth = 0, adjacent = 0, colored = 1, lastColored = start, textGap = 0;
                    int maxTextGap = Math.Max(4, clientSize.Width / 192);
                    while (x < width)
                    {
                        int b = pixels[y * width + x];
                        if ((At(x, y) & color) == 0)
                        {
                            int r = (b >> 16) & 255, g = (b >> 8) & 255, blue = b & 255;
                            int min = Math.Min(r, Math.Min(g, blue)), max = Math.Max(r, Math.Max(g, blue));
                            // Health-number strokes may interrupt the fill; other gaps end the run.
                            if (++textGap > maxTextGap || min < 145 || max - min > 70) break;
                            x++; continue;
                        }
                        if (textGap == 0)
                        {
                            int a = pixels[y * width + x - 1]; adjacent++;
                            if (Math.Abs(((a >> 16) & 255) - ((b >> 16) & 255)) + Math.Abs(((a >> 8) & 255) - ((b >> 8) & 255)) + Math.Abs((a & 255) - (b & 255)) <= 42) smooth++;
                        }
                        textGap = 0; colored++; lastColored = x;
                        x++;
                    }
                    int length = lastColored - start + 1;
                    if (length < minWidth || length > maxWidth || colored * 10 < length * 6 || (length > 3 && (adjacent == 0 || smooth * 10 < adjacent * 7))) continue;
                    int top = y, bottom = y;
                    while (top > 0 && y - top <= maxHeight && RowSupports(top - 1, start, length, color)) top--;
                    while (bottom < height - 1 && bottom - top <= maxHeight && RowSupports(bottom + 1, start, length, color)) bottom++;
                    int thickness = bottom - top + 1;
                    if (requireHud && verifiedHud.HasValue && thickness >= 2 && thickness < minHeight && length < clientSize.Height * .117 * .75 &&
                        Math.Abs(screenArea.X + start - verifiedHud.Value.X) <= 40 && Math.Abs(screenArea.Y + top - verifiedHud.Value.Y) <= 40)
                    {
                        // At low HP, digits cover most of the short colored fill. Use the
                        // verified strip height, then still require the complete numeric HUD.
                        Rectangle narrowHud;
                        if (ValidateHud(start, top, length, verifiedHud.Value.Height, clientSize, out narrowHud, true)) thickness = verifiedHud.Value.Height;
                    }
                    bool recoveredSeed = false; Rectangle seed = Rectangle.Empty;
                    if (requireHud && (thickness < minHeight || thickness > maxHeight))
                    {
                        recoveredSeed = RecoverOccludedSeed(start, y, length, clientSize, out seed);
                        if (recoveredSeed) { top = seed.Top; bottom = seed.Bottom - 1; thickness = seed.Height; }
                    }
                    if (!recoveredSeed && (thickness < minHeight || thickness > maxHeight || (!requireHud && length < thickness * 2.5))) continue;
                    // Reject a large green/orange surface and clipped surfaces at capture edges.
                    if (!recoveredSeed && (top == 0 || bottom == height - 1 || !RowSupports(y, start, length, color))) continue;
                    Rectangle candidate = recoveredSeed ? new Rectangle(screenArea.X + seed.X, screenArea.Y + seed.Y, seed.Width, seed.Height) : new Rectangle(screenArea.X + start, screenArea.Y + top, length, thickness);
                    bool reticleOccluded = recoveredSeed;
                    if (requireHud && !recoveredSeed)
                    {
                        Rectangle hud;
                        if (!ValidateHud(start, top, length, thickness, clientSize, out hud))
                        {
                            Rectangle proposed = new Rectangle(screenArea.X + start, screenArea.Y + top, (int)Math.Round(clientSize.Height * .117), thickness);
                            bool nearVerified = verifiedHud.HasValue && Math.Abs(proposed.Left - verifiedHud.Value.Left) <= Math.Max(40, proposed.Width * .4) && Math.Abs(proposed.Top - verifiedHud.Value.Top) <= Math.Max(40, proposed.Height * 3);
                            int radius = Math.Max(20, (int)(clientSize.Height * .06));
                            Rectangle reticleArea = reticleCenter.HasValue ? new Rectangle(reticleCenter.Value.X - radius, reticleCenter.Value.Y - radius, radius * 2, radius * 2) : Rectangle.Empty;
                            // The unfilled strip is translucent: wood behind it can make it brown.
                            // Keep strict numeral geometry when following an already verified HUD.
                            if (!nearVerified)
                            {
                                if (!RecoverOccludedSeed(start, y, length, clientSize, out hud)) continue;
                                reticleOccluded = true;
                            }
                            else if (!ValidateHud(start, top, length, thickness, clientSize, out hud, true))
                            {
                                if (!reticleArea.IntersectsWith(proposed) || !ValidateHud(start, top, length, thickness, clientSize, out hud, true, true)) continue;
                                reticleOccluded = true;
                            }
                        }
                        candidate = new Rectangle(screenArea.X + hud.X, screenArea.Y + hud.Y, hud.Width, hud.Height);
                    }
                    if (tracked.HasValue)
                    {
                        Rectangle target = tracked.Value, allowed = Rectangle.Inflate(target, Math.Max(10, target.Width / 5), Math.Max(6, target.Height / 2));
                        Rectangle overlap = Rectangle.Intersect(allowed, candidate);
                        if (overlap.IsEmpty || overlap.Width * 2 < Math.Min(candidate.Width, target.Width) || Math.Abs(candidate.Top + candidate.Height / 2 - target.Top - target.Height / 2) > Math.Max(8, target.Height)) continue;
                    }
                    double centerX = screenArea.X + start + length / 2.0, centerY = recoveredSeed ? candidate.Y + candidate.Height / 2.0 : screenArea.Y + (top + bottom) / 2.0;
                    double expectedX = reticleCenter.HasValue ? reticleCenter.Value.X : screenArea.X - clientSize.Width * .05 + clientSize.Width * .50;
                    double expectedY = reticleCenter.HasValue ? reticleCenter.Value.Y : screenArea.Y - clientSize.Height * .15 + clientSize.Height * .50;
                    double score = 1000 - Math.Abs(centerX - expectedX) * .4 - Math.Abs(centerY - expectedY) + length * .3;
                    if (!best.Found || score > best.Score)
                    {
                        best.Found = true; best.Score = score; best.ColorNumber = color == 1 ? 1 : 2;
                        best.ReticleOccluded = reticleOccluded;
                        best.Bounds = candidate;
                        if (searchFloor != null) searchFloor.Raise(score);
                    }
                }
            }
            return best;
        }
        static int ColorDifference(int a, int b)
        { return (Math.Abs(((a >> 16) & 255) - ((b >> 16) & 255)) + Math.Abs(((a >> 8) & 255) - ((b >> 8) & 255)) + Math.Abs((a & 255) - (b & 255))) / 3; }
        bool TailHasEdges(int left, int top, int w, int h)
        {
            if (top < 2 || top + h + 1 >= height || left < 0 || left + w + 1 >= width) return false;
            int above = 0, below = 0;
            for (int sampleX = 0; sampleX < 2; sampleX++)
            {
                int dx = w - 6 - sampleX * 5;
                int upper = pixels[(top + 2) * width + left + dx], lower = pixels[(top + h - 3) * width + left + dx];
                if (ColorDifference(upper, lower) > 40) return false;
                int mid = pixels[(top + h / 2) * width + left + dx];
                if (ColorDifference(upper, mid) > 40) return false;
                above += ColorDifference(upper, pixels[(top - 2) * width + left + dx]);
                below += ColorDifference(lower, pixels[(top + h + 1) * width + left + dx]);
            }
            if (above < 48 || below < 48) return false;
            int side = 0;
            for (int sample = 0; sample < 3; sample++)
            {
                int y = top + (sample == 0 ? 2 : sample == 1 ? h / 2 : h - 3);
                side += ColorDifference(pixels[y * width + left + w - 2], pixels[y * width + left + w + 1]);
            }
            return side >= 72;
        }
        // Recover a damaged HUD whose fill merges with scene color. Require
        // a bounded quiet tail and four aligned glyph groups; only reticle
        // intrusions may be ignored. Applies to acquisition and following.
        bool RecoverOccludedSeed(int left, int row, int fillLength, Size client, out Rectangle hud)
        {
            hud = Rectangle.Empty; int w = (int)Math.Round(client.Height * .117);
            int minH = Math.Max(5, client.Height / 90), maxH = Math.Max(10, client.Height / 70);
            if (fillLength < 4 || fillLength > w * .9 || left + w >= width ||
                !seedReticleInk.IntersectsWith(new Rectangle(left, row - maxH, w, maxH * 2))) return false;
            for (int top = Math.Max(2, row - maxH + 3); top <= row - 2; top++) for (int h = minH; h <= maxH; h++)
            {
                long key = ((long)left << 32) | ((long)top << 8) | (uint)h;
                if (!seedChecks.Add(key) || !TailHasEdges(left, top, w, h)) continue;
                if (ValidateHud(left, top, fillLength, h, client, out hud, false, true, true)) return true;
            }
            return false;
        }
        bool PanelPixel(int x, int y, bool allowTint)
        {
            if (At(x, y) != 0) return true;
            int rgb = pixels[y * width + x], r = (rgb >> 16) & 255, g = (rgb >> 8) & 255, b = rgb & 255;
            // Translucent blue-grey background of the unfilled HP strip.
            return (r < 140 && g < 150 && b < 170 && b >= r + 3 && b >= g - 8) ||
                (r < 130 && g < 140 && b < 160 && Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) <= 80) ||
                (allowTint && b - r >= 25 && b - g >= 5 && r < 190 && g < 220 && b >= 110);
        }
        internal static bool NumberWhite(int rgb)
        {
            int r = (rgb >> 16) & 255; if (r < 170) return false;
            int g = (rgb >> 8) & 255; if (g < 170) return false;
            int b = rgb & 255;
            // Antialiased white numerals inherit the green/orange fill beneath their edges.
            // With every channel >= 170, max-min <= 255-170 = 85 automatically.
            return b >= 170 && r + g + b >= 600;
        }
        bool ValidateHud(int left, int top, int fillLength, int h, Size client, out Rectangle hud, bool allowTint = false, bool allowOcclusion = false, bool strictSeed = false, int groupsRequired = 0)
        {
            int w = (int)Math.Round(client.Height * .117);
            // fillLength only affects this early bound; the pixel/glyph checks
            // are otherwise identical for the same rectangle and strictness.
            if (fillLength > w * 1.18 || left + w > width || top + h > height)
            { hud = Rectangle.Empty; return false; }
            bool cache = HudDebug == null && (uint)left <= 65535 && (uint)top <= 65535 &&
                (uint)w <= 65535 && (uint)h <= 255 && (uint)groupsRequired <= 31;
            long key = ((long)left << 48) | ((long)top << 32) | ((long)w << 16) | ((long)h << 8) |
                (allowTint ? 1L : 0) | (allowOcclusion ? 2L : 0) | (strictSeed ? 4L : 0) | ((long)groupsRequired << 3);
            bool result;
            if (cache && hudChecks.TryGetValue(key, out result))
            { hud = result ? new Rectangle(left, top, w, h) : Rectangle.Empty; return result; }
            int textWidth = Math.Min(w, Math.Max(48, (int)(w * .65)));
            Rectangle text = new Rectangle(left + 2, top + 1, textWidth - 2, h - 2);
            if (HudDebug == null && tailArea.Contains(text) && TailCount(text) < h * 2)
            {
                // Every accepting path below requires at least h*2 numeral
                // pixels. The existing integral gives that exact count.
                hud = Rectangle.Empty;
                if (cache && hudChecks.Count < 4096) hudChecks[key] = false;
                return false;
            }
            result = ValidateHudCore(left, top, fillLength, h, client, out hud, allowTint, allowOcclusion, strictSeed, groupsRequired);
            if (cache && hudChecks.Count < 4096) hudChecks[key] = result;
            return result;
        }
        bool ValidateHudCore(int left, int top, int fillLength, int h, Size client, out Rectangle hud, bool allowTint, bool allowOcclusion, bool strictSeed, int groupsRequired)
        {
            hud = Rectangle.Empty;
            int w = (int)Math.Round(client.Height * .117);
            if (fillLength > w * 1.18 || left + w > width || top + h > height) return false;
            // A label is a narrow rectangle with a flat fill/background, not arbitrary scene texture.
            for (int row = 0; row < 3; row++)
            {
                int dy = row == 0 ? 2 : row == 1 ? h / 2 : h - 3;
                int count = 0;
                for (int dx = 1; dx < w - 1; dx++) if (PanelPixel(left + dx, top + dy, allowTint) || NumberWhite(pixels[(top + dy) * width + left + dx])) count++;
                if (count < (w - 2) * .80) return HudReject("panel " + count + "/" + w + " row " + dy, left, top, h);
            }
            int textWidth = Math.Min(w, Math.Max(48, (int)(w * .65)));
            if (textColumns == null || textColumns.Length < textWidth)
            { textColumns = new int[textWidth]; columnTop = new int[textWidth]; columnBottom = new int[textWidth]; }
            for (int xx = 0; xx < textWidth; xx++) { columnTop[xx] = h; columnBottom[xx] = -1; }
            int[] columns = textColumns; Array.Clear(columns, 0, textWidth); int whites = 0, minY = h, maxY = -1, minX = textWidth, maxX = -1;
            Rectangle numeralArea = new Rectangle(left + 2, top + 1, textWidth - 2, h - 2);
            if (h - 2 <= 64 && tailArea.Contains(numeralArea))
            {
                for (int xx = 2; xx < textWidth; xx++)
                {
                    ulong bits = ColumnWindow(whiteColumnBits, tailArea.Width, left + xx - tailArea.Left, top + 1 - tailArea.Top, h - 2);
                    int count = BitCount(bits); columns[xx] = count; if (count == 0) continue;
                    columnTop[xx] = FirstBit(bits) + 1; columnBottom[xx] = LastBit(bits) + 1;
                    whites += count; minY = Math.Min(minY, columnTop[xx]); maxY = Math.Max(maxY, columnBottom[xx]); minX = Math.Min(minX, xx); maxX = Math.Max(maxX, xx);
                }
            }
            else for (int yy = 1; yy < h - 1; yy++) for (int xx = 2; xx < textWidth; xx++)
                    if (NumberWhite(pixels[(top + yy) * width + left + xx]))
                    { if (columns[xx]++ == 0) columnTop[xx] = yy; columnBottom[xx] = yy; whites++; minY = Math.Min(minY, yy); maxY = Math.Max(maxY, yy); minX = Math.Min(minX, xx); maxX = Math.Max(maxX, xx); }
            if (allowOcclusion && !strictSeed && groupsRequired == 0 && whites >= h * 2 && maxX - minX >= h * 1.3)
            {
                // Only for a previously verified panel: tolerate the reticle obscuring its digits.
                int reference = 0;
                for (int yy = 1; yy < h - 1; yy++) for (int xx = 1; xx < w - 1; xx++) if (At(left + xx, top + yy) != 0) reference++;
                if (reference >= w * h * .2 || (reference >= h * 2 && ValidateTrailingDigits(columns, textWidth, left, top, h)))
                { hud = new Rectangle(left, top, w, h); return true; }
            }
            if (whites < Math.Max(12, h * 2) || maxY - minY + 1 < h * .4 || (!strictSeed && groupsRequired == 0 && maxY - minY + 1 > h * .94) || maxX - minX < h * 1.3 || maxX - minX > h * 6) return HudReject("text white=" + whites + " textH=" + (maxY - minY + 1) + " textW=" + (maxX - minX), left, top, h);
            // First preserve the real spaces between digits (notably 300/500).
            // Repair a one-pixel hole only as a fallback for faint numeral strokes.
            if (ValidateNumberGroups(columns, textWidth, left, top, h, allowOcclusion, strictSeed, groupsRequired)) { hud = new Rectangle(left, top, w, h); return true; }
            if (h >= 12) for (int xx = 1; xx < textWidth - 1; xx++) if (columns[xx] == 0 && columns[xx - 1] > 0 && columns[xx + 1] > 0) columns[xx] = 1;
            if (!ValidateNumberGroups(columns, textWidth, left, top, h, allowOcclusion, strictSeed, groupsRequired)) return false;
            hud = new Rectangle(left, top, w, h); return true;
        }
        bool ValidateNumberGroups(int[] columns, int textWidth, int left, int top, int h, bool allowOcclusion = false, bool strictSeed = false, int groupsRequired = 0)
        {
            int groups = 0, run = 0, firstTop = -1, firstBottom = -1;
            for (int xx = 0; xx <= textWidth; xx++)
            {
                if (xx < textWidth && columns[xx] > 0) { run++; continue; }
                if (run > 0)
                {
                    int glyphTop = h, glyphBottom = -1;
                    for (int gx = xx - run; gx < xx; gx++)
                    { glyphTop = Math.Min(glyphTop, columnTop[gx]); glyphBottom = Math.Max(glyphBottom, columnBottom[gx]); }
                    // The HP divider and the highlight at the fill boundary can span the
                    // strip's height. They are not digits and need not share a baseline.
                    if (glyphBottom - glyphTop + 1 > h * .75 && run <= Math.Max(2, h / 5))
                    { run = 0; continue; }
                    // Shotgun-reticle corners can touch the panel edge and join
                    // one HP digit. Ignore only these intrusive groups, and still
                    // require at least three complete, aligned numeral groups.
                    if (allowOcclusion && (glyphTop < h / 6 || glyphBottom >= h - h / 6) &&
                        (!strictSeed || seedReticleInk.IntersectsWith(new Rectangle(left + xx - run, top + glyphTop, run, glyphBottom - glyphTop + 1)))) { run = 0; continue; }
                    if (run > h * .8) return HudReject("wide glyph " + run, left, top, h);
                    if (h >= 16 && (glyphTop < h / 6 || glyphBottom >= h - h / 6))
                        return HudReject("text touches panel edge", left, top, h);
                    if (glyphBottom - glyphTop + 1 < h * .4)
                    { if (run <= 2) { run = 0; continue; } return HudReject("short glyph " + (glyphBottom - glyphTop + 1), left, top, h); }
                    if (firstTop < 0) { firstTop = glyphTop; firstBottom = glyphBottom; }
                    else if (Math.Abs(firstTop - glyphTop) > 2 || Math.Abs(firstBottom - glyphBottom) > 2) return HudReject("unaligned glyph " + glyphTop + ":" + glyphBottom + " versus " + firstTop + ":" + firstBottom, left, top, h);
                    groups++; run = 0;
                }
            }
            if (groups < (groupsRequired > 0 ? groupsRequired : strictSeed ? 4 : 3) || groups > 8) return HudReject("glyph groups=" + groups, left, top, h);
            return true;
        }
        bool ValidateTrailingDigits(int[] columns, int textWidth, int left, int top, int h)
        {
            // A reticle can cover the small current-HP fill while the three
            // maximum-HP digits remain readable. Only used at a known HUD near
            // the reticle, after the complete panel/background validation.
            int groups = 0, firstTop = -1, firstBottom = -1, right = -1, leftmost = -1;
            for (int x = textWidth - 1; x >= 2 && groups < 3; )
            {
                while (x >= 2 && columns[x] == 0) x--;
                if (x < 2) break;
                int end = x;
                while (x >= 2 && columns[x] > 0) x--;
                int start = x + 1, run = end - start + 1, glyphTop = h, glyphBottom = -1;
                for (int xx = start; xx <= end; xx++)
                { glyphTop = Math.Min(glyphTop, columnTop[xx]); glyphBottom = Math.Max(glyphBottom, columnBottom[xx]); }
                if (glyphBottom - glyphTop + 1 > h * .75 && run <= Math.Max(2, h / 5)) continue;
                if (run > h * .8 || glyphBottom - glyphTop + 1 < h * .4 || glyphTop < h / 6 || glyphBottom >= h - h / 6) return false;
                if (firstTop < 0) { firstTop = glyphTop; firstBottom = glyphBottom; right = end; }
                else if (Math.Abs(firstTop - glyphTop) > 2 || Math.Abs(firstBottom - glyphBottom) > 2) return false;
                leftmost = start; groups++;
            }
            return groups == 3 && right - leftmost >= h * .9 && right - leftmost <= h * 2.5;
        }
        internal bool HasHudPanel(Rectangle bounds, Rectangle area, Size client)
        {
            Rectangle local = new Rectangle(bounds.X - area.X, bounds.Y - area.Y, bounds.Width, bounds.Height), found;
            return local.Width >= 8 && local.Height >= Math.Max(5, client.Height / 90) && local.Height <= Math.Max(10, client.Height / 70) && local.X >= 0 && local.Y >= 0 && local.Right <= width && local.Bottom <= height && ValidateHud(local.X, local.Y, 0, local.Height, client, out found, true);
        }
        internal bool FindKnownHudPanel(Rectangle known, Rectangle area, Size client, out Rectangle panel, Point? reticleCenter = null)
        {
            panel = known;
            if (HasHudPanel(known, area, client)) return true;
            if (known.IsEmpty) return false;
            int minHeight = Math.Max(5, client.Height / 90), maxHeight = Math.Max(10, client.Height / 70);
            int radiusX = Math.Max(12, client.Height / 45), radiusY = Math.Max(12, client.Height / 45);
            int reticleRadius = Math.Max(20, (int)(client.Height * .06));
            Rectangle reticleArea = reticleCenter.HasValue ? new Rectangle(reticleCenter.Value.X - reticleRadius, reticleCenter.Value.Y - reticleRadius, reticleRadius * 2, reticleRadius * 2) : Rectangle.Empty;
            int innerRadius = Math.Max(14, client.Height * 35 / 1000);
            Rectangle reticleInk = reticleCenter.HasValue ? new Rectangle(reticleCenter.Value.X - area.X - innerRadius, reticleCenter.Value.Y - area.Y - innerRadius, innerRadius * 2, innerRadius * 2) : Rectangle.Empty;
            Rectangle found;
            PrepareTailIntegral(Rectangle.Intersect(new Rectangle(0, 0, width, height), new Rectangle(known.X - area.X - radiusX, known.Y - area.Y - radiusY, known.Width + radiusX * 2, maxHeight + radiusY * 2)));
            // Matching brick texture can merge with the colored fill. Relocate
            // only near a verified panel, keeping strict numeral geometry.
            for (int distance = 0; distance <= radiusX + radiusY; distance++)
                for (int dy = -Math.Min(radiusY, distance); dy <= Math.Min(radiusY, distance); dy++)
                {
                    int dx = distance - Math.Abs(dy);
                    if (dx > radiusX) continue;
                    for (int side = 0; side < (dx == 0 ? 1 : 2); side++)
                    {
                        int left = known.X - area.X + (side == 0 ? dx : -dx), top = known.Y - area.Y + dy;
                        if (left < 0 || top < 0 || left + known.Width > width) continue;
                        for (int h = minHeight; h <= maxHeight; h++)
                            if (top + h <= height && HasFillStart(left, top, h) && HasQuietTail(left, top, h, known.Width, reticleInk) &&
                                (ValidateHud(left, top, 0, h, client, out found, true) ||
                                 (reticleArea.IntersectsWith(new Rectangle(area.X + left, area.Y + top, known.Width, h)) && ValidateHud(left, top, 0, h, client, out found, true, true)) ||
                                 KnownUiOcclusion(left, top, known.Width, h, area, client, reticleCenter, out found)))
                            { panel = new Rectangle(area.X + found.X, area.Y + found.Y, found.Width, found.Height); if (HudDebug != null) HudDebug.Add("RELOCATED " + panel); return true; }
                    }
                }
            return false;
        }
        bool HasFillStart(int left, int top, int h)
        {
            if (left >= tailArea.Left && left + 4 <= tailArea.Right &&
                top + 1 >= tailArea.Top && top + h - 1 <= tailArea.Bottom)
            {
                int x = left - tailArea.Left;
                int first = (top + 1 - tailArea.Top) * tailStride;
                int last = (top + h - 1 - tailArea.Top) * tailStride;
                // Every column count is bounded by the sum of the four columns.
                // If even their sum cannot reach the threshold, none can pass.
                int total = referenceIntegral[last + x + 4] - referenceIntegral[first + x + 4] -
                    referenceIntegral[last + x] + referenceIntegral[first + x];
                if (total < h * .6) return false;
                for (int dx = 0; dx < 4; dx++)
                {
                    int at = x + dx;
                    int count = referenceIntegral[last + at + 1] - referenceIntegral[first + at + 1] -
                        referenceIntegral[last + at] + referenceIntegral[first + at];
                    if (count >= h * .6) return true;
                }
                return false;
            }
            for (int dx = 0; dx < 4; dx++)
            {
                int count = 0;
                for (int dy = 1; dy < h - 1; dy++) if (At(left + dx, top + dy) != 0) count++;
                if (count >= h * .6) return true;
            }
            return false;
        }
        void PrepareTailIntegral(Rectangle region)
        {
            tailArea = region; tailStride = region.Width + 1;
            int length = tailStride * (region.Height + 1);
            if (tailIntegral == null || tailIntegral.Length < length) { tailIntegral = new int[length]; referenceIntegral = new int[length]; }
            Array.Clear(tailIntegral, 0, length); Array.Clear(referenceIntegral, 0, length);
            int wordCount = ((region.Height + 63) / 64) * region.Width;
            if (whiteColumnBits == null || whiteColumnBits.Length < wordCount) whiteColumnBits = new ulong[wordCount];
            Array.Clear(whiteColumnBits, 0, wordCount);
            for (int y = 0; y < region.Height; y++)
            {
                int row = 0, reference = 0;
                int wordRow = (y >> 6) * region.Width; ulong bit = 1UL << (y & 63);
                for (int x = 0; x < region.Width; x++)
                { if (NumberWhite(pixels[(region.Y + y) * width + region.X + x])) { row++; whiteColumnBits[wordRow + x] |= bit; } if (At(region.X + x, region.Y + y) != 0) reference++; tailIntegral[(y + 1) * tailStride + x + 1] = tailIntegral[y * tailStride + x + 1] + row; referenceIntegral[(y + 1) * tailStride + x + 1] = referenceIntegral[y * tailStride + x + 1] + reference; }
            }
        }
        internal static ulong ColumnWindow(ulong[] words, int columns, int x, int top, int count)
        {
            int offset = top & 63, row = (top >> 6) * columns;
            ulong bits = words[row + x] >> offset;
            if (offset + count > 64) bits |= words[row + columns + x] << (64 - offset);
            return count == 64 ? bits : bits & ((1UL << count) - 1);
        }
        internal static int BitCount(ulong bits)
        {
            bits -= (bits >> 1) & 0x5555555555555555UL;
            bits = (bits & 0x3333333333333333UL) + ((bits >> 2) & 0x3333333333333333UL);
            bits = (bits + (bits >> 4)) & 0x0F0F0F0F0F0F0F0FUL;
            return (int)(unchecked(bits * 0x0101010101010101UL) >> 56);
        }
        internal static int FirstBit(ulong bits)
        {
            if (bits == 0) return -1; int n = 0;
            if ((bits & 0xFFFFFFFFUL) == 0) { n += 32; bits >>= 32; }
            if ((bits & 0xFFFFUL) == 0) { n += 16; bits >>= 16; }
            if ((bits & 0xFFUL) == 0) { n += 8; bits >>= 8; }
            if ((bits & 15) == 0) { n += 4; bits >>= 4; }
            if ((bits & 3) == 0) { n += 2; bits >>= 2; }
            return n + ((bits & 1) == 0 ? 1 : 0);
        }
        internal static int LastBit(ulong bits)
        {
            if (bits == 0) return -1; int n = 0;
            if (bits > 0xFFFFFFFFUL) { n += 32; bits >>= 32; }
            if (bits > 0xFFFFUL) { n += 16; bits >>= 16; }
            if (bits > 0xFFUL) { n += 8; bits >>= 8; }
            if (bits > 15) { n += 4; bits >>= 4; }
            if (bits > 3) { n += 2; bits >>= 2; }
            return n + (bits > 1 ? 1 : 0);
        }
        int TailCount(Rectangle r) { return IntegralCount(tailIntegral, r); }
        int IntegralCount(int[] integral, Rectangle r)
        {
            r = Rectangle.Intersect(r, tailArea); if (r.IsEmpty) return 0;
            int l = r.Left - tailArea.Left, t = r.Top - tailArea.Top, rr = r.Right - tailArea.Left, b = r.Bottom - tailArea.Top;
            return integral[b * tailStride + rr] - integral[t * tailStride + rr] - integral[b * tailStride + l] + integral[t * tailStride + l];
        }
        bool KnownUiOcclusion(int left, int top, int w, int h, Rectangle area, Size client, Point? reticle, out Rectangle found)
        {
            found = Rectangle.Empty; if (!reticle.HasValue) return false;
            int radius = client.Height * 16 / 100;
            if (!new Rectangle(reticle.Value.X - radius, reticle.Value.Y - radius, radius * 2, radius * 2).IntersectsWith(new Rectangle(area.X + left, area.Y + top, w, h))) return false;
            if (IntegralCount(referenceIntegral, new Rectangle(left, top, w, h)) < w * h / 5) return false;
            int intrusiveWhite = 0;
            for (int edge = 0; edge < 2; edge++)
            {
                int y = edge == 0 ? top : top + h - 2;
                int dy = area.Y + y + 1 - reticle.Value.Y;
                if (Math.Abs(dy) >= radius) continue;
                int half = (int)Math.Sqrt((long)radius * radius - (long)dy * dy);
                int x1 = Math.Max(left, reticle.Value.X - area.X - half), x2 = Math.Min(left + w, reticle.Value.X - area.X + half);
                if (x2 > x1) intrusiveWhite += TailCount(new Rectangle(x1, y, x2 - x1, 2));
            }
            if (intrusiveWhite < 3) return false;
            return ValidateHud(left, top, 0, h, client, out found, true, true, false, 3);
        }
        bool HasQuietTail(int left, int top, int h, int w, Rectangle reticleInk)
        {
            Rectangle tail = new Rectangle(left + (int)(w * .67), top + 3, w - 4 - (int)(w * .67), h - 6);
            return TailCount(tail) - TailCount(Rectangle.Intersect(tail, reticleInk)) <= h;
        }

    }

    internal sealed class HealthBarCapture : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr window, out Rect rect);
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr window, ref Native.Point point);
        Bitmap frame;
        DesktopFrameCapture fastCapture;
        long fastRetryAt;
        internal string CaptureBackend { get; private set; }
        internal Bitmap Frame { get { return frame; } }
        internal Rectangle LastArea { get; private set; }
        internal Size LastClientSize { get; private set; }
        internal long CapturedAtMs { get; private set; }
        internal static bool ClientBounds(IntPtr window, out Rectangle bounds)
        {
            Rect rect; Native.Point point = new Native.Point(); bounds = Rectangle.Empty;
            if (!GetClientRect(window, out rect) || !ClientToScreen(window, ref point)) return false;
            bounds = new Rectangle(point.X, point.Y, rect.Right - rect.Left, rect.Bottom - rect.Top);
            return bounds.Width >= 320 && bounds.Height >= 200;
        }
        internal BarDetection Read(IntPtr window, HealthBarDetector detector)
        {
            CaptureFrame(window);
            return detector.Find(frame, LastArea, LastClientSize);
        }
        internal void CaptureFrame(IntPtr window, bool preferFast = false)
        {
            Rectangle client;
            if (!ClientBounds(window, out client)) throw new InvalidOperationException("Fortnite-Bildbereich nicht verfügbar.");
            Prepare(HealthBarDetector.SearchArea(client), client.Size);
            if (preferFast && LastArea.Width >= 1200 && ShotVerifier.ClockMs() >= fastRetryAt)
            {
                try {
                    if (fastCapture == null) fastCapture = new DesktopFrameCapture(LastArea);
                    // RGB preserves opaque preview/recorder clones when the desktop texture's alpha is zero.
                    if (frame.PixelFormat != PixelFormat.Format32bppRgb) { frame.Dispose(); frame = new Bitmap(LastArea.Width, LastArea.Height, PixelFormat.Format32bppRgb); }
                    if (fastCapture.Capture(LastArea, frame, CapturedAtMs)) {
                        Rectangle after;
                        if (Native.GetForegroundWindow() != window || !ClientBounds(window, out after) || after != client) throw new InvalidOperationException("Spielbereich während GPU-Aufnahme geändert");
                        CapturedAtMs = fastCapture.ValidatedAtMs; CaptureBackend = "GPU"; return;
                    }
                    CaptureBackend = "GDI · kein frisches GPU-Bild";
                }
                catch (Exception ex) {
                    ResetFastCapture(); fastRetryAt = ShotVerifier.ClockMs() + 5000;
                    CaptureBackend = "GDI · GPU nicht verfügbar (" + ex.GetType().Name + ")";
                }
            }
            else { if (!preferFast) ResetFastCapture(); CaptureBackend = preferFast ? "GDI · Kompatibilität" : "GDI"; }
            using (Graphics graphics = Graphics.FromImage(frame)) graphics.CopyFromScreen(LastArea.Location, Point.Empty, LastArea.Size);
            CapturedAtMs = ShotVerifier.ClockMs();
        }
        internal void ResetFastCapture() { if (fastCapture != null) { fastCapture.Dispose(); fastCapture = null; } }
        internal void Prepare(Rectangle area, Size client)
        {
            if (LastArea != area || LastClientSize != client) CapturedAtMs = 0;
            LastArea = area; LastClientSize = client;
            if (frame == null || frame.Size != LastArea.Size)
            { if (frame != null) frame.Dispose(); frame = new Bitmap(LastArea.Width, LastArea.Height, PixelFormat.Format32bppArgb); }
        }
        public void Dispose() { ResetFastCapture(); if (frame != null) frame.Dispose(); }
    }
}

