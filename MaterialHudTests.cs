using System;
using System.Drawing;
using System.Drawing.Imaging;
namespace PrefireMacro
{
    // Generated numeral fixtures, not screenshots or an OCR training set.
    internal static class MaterialHudTests
    {
        static readonly string[] digits = {
            "01110/10001/10011/10101/11001/10001/01110", "00100/01100/00100/00100/00100/00100/01110",
            "01110/10001/00001/00010/00100/01000/11111", "11110/00001/00001/01110/00001/00001/11110",
            "00010/00110/01010/10010/11111/00010/00010", "11111/10000/10000/11110/00001/00001/11110",
            "01110/10000/10000/11110/10001/10001/01110", "11111/00001/00010/00100/01000/01000/01000",
            "01110/10001/10001/01110/10001/10001/01110", "01110/10001/10001/01111/00001/00001/01110"
        };
        static void Number(Graphics g, string number, ref int x, int y, int gap)
        {
            foreach (char c in number)
            {
                string[] rows = digits[c - '0'].Split('/');
                for (int yy = 0; yy < 9; yy++) for (int xx = 0; xx < 6; xx++)
                    if (rows[yy * 7 / 9][xx * 5 / 6] == '1') g.FillRectangle(Brushes.White, x + xx, y + yy, 1, 1);
                x += 6 + gap;
            }
        }
        static void Draw(Graphics g, Rectangle panel, int hp, int maximum, bool orange, int gap, bool warm)
        {
            g.Clear(Color.FromArgb(180, 155, 120));
            using (Brush background = new SolidBrush(warm ? Color.FromArgb(100, 78, 60) : Color.FromArgb(48, 63, 90))) g.FillRectangle(background, panel);
            using (Brush fill = new SolidBrush(orange ? Color.FromArgb(218, 148, 61) : Color.FromArgb(92, 196, 66)))
                g.FillRectangle(fill, panel.X, panel.Y, Math.Max(1, panel.Width * hp / maximum), panel.Height);
            int x = panel.X + 3, y = panel.Y + (panel.Height - 9) / 2;
            Number(g, hp.ToString(), ref x, y, gap);
            g.FillRectangle(Brushes.White, x, panel.Y + 1, 1, panel.Height - 2); x += 3;
            Number(g, maximum.ToString(), ref x, y, gap);
        }
        internal static void Run()
        {
            CheckColumnWindows();
            for (int rgb = 0; rgb <= 0xFFFFFF; rgb++)
            {
                int r = (rgb >> 16) & 255, g = (rgb >> 8) & 255, b = rgb & 255;
                bool reference = Math.Min(r, Math.Min(g, b)) >= 170 &&
                    Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) <= 85 && r + g + b >= 600;
                if (HealthBarDetector.NumberWhite(rgb) != reference) throw new Exception("White-pixel equivalence failed at " + rgb.ToString("X6"));
            }
            Config config = new Config { HasFirst = true, HasSecond = true, FirstColor = 0x5CC442, SecondColor = 0xDA943D };
            foreach (int height in new int[] { 1080, 1440 })
            {
                Size client = new Size(height * 16 / 9, height); Rectangle area = HealthBarDetector.SearchArea(new Rectangle(Point.Empty, client));
                using (Bitmap image = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppArgb))
                using (Graphics graphics = Graphics.FromImage(image))
                {
                    HealthBarDetector detector = new HealthBarDetector(config, 25);
                    foreach (int maximum in new int[] { 150, 300, 500 }) foreach (int gap in new int[] { 1, 2 }) foreach (bool warm in new bool[] { false, true })
                    {
                        HealthBarTracker tracker = new HealthBarTracker(); PrefireGate gate = new PrefireGate(20); long now = 0;
                        Rectangle panel = new Rectangle(400, 250, (int)Math.Round(height * .117), (int)Math.Round(height * .0125));
                        Draw(graphics, panel, maximum, maximum, false, gap, warm);
                        for (int i = 0; i < 3; i++) { BarObservation acquired = tracker.Update(image, area, client, detector, now); gate.RequestFreshConfirmation(true, acquired.Present, acquired.Stable, now); now += 20; }
                        foreach (int hp in new int[] { maximum, maximum - 1, maximum / 2, 75, 68, 20, 1 })
                        {
                            panel.Offset(2, 1); Draw(graphics, panel, hp, maximum, hp != maximum, gap, warm);
                            BarObservation observed = tracker.Update(image, area, client, detector, now); now += 20;
                            if (!observed.Present || !observed.Stable || gate.RequestFreshConfirmation(true, observed.Present, observed.Stable, now))
                            {
                                string failure = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "prefire-material-failure");
                                image.Save(failure + ".png"); detector.HudDebug = new System.Collections.Generic.List<string>();
                                detector.Find(image, area, client, null, true, observed.Bounds);
                                System.IO.File.WriteAllLines(failure + ".txt", detector.HudDebug);
                                throw new Exception("Material HUD tracking: " + hp + "/" + maximum + ", client=" + height + ", gap=" + gap + ", warm=" + warm);
                            }
                        }
                        HealthBarTracker lowHpAcquisition = new HealthBarTracker(); BarObservation lowHp = null;
                        for (int i = 0; i < 3; i++) lowHp = lowHpAcquisition.Update(image, area, client, detector, i * 20);
                        if (!lowHp.Present || !lowHp.Stable) throw new Exception("Material HUD cold acquisition at 1/" + maximum);
                        graphics.Clear(Color.FromArgb(180, 155, 120)); BarObservation missing = tracker.Update(image, area, client, detector, now); now += 20;
                        if (missing.Present || !gate.RequestFreshConfirmation(true, false, missing.Stable, now)) throw new Exception("Material HUD disappearance: " + maximum);
                    }
                }
            }
        }
        static void CheckColumnWindows()
        {
            const int columns = 7, rows = 192;
            ulong[] words = new ulong[columns * 3]; Random random = new Random(4126);
            for (int i = 0; i < words.Length; i++)
                words[i] = i % 7 == 0 ? 0 : i % 7 == 1 ? UInt64.MaxValue :
                    (ulong)(uint)random.Next(65536) | ((ulong)(uint)random.Next(65536) << 16) | ((ulong)(uint)random.Next(65536) << 32) | ((ulong)(uint)random.Next(65536) << 48);
            for (int x = 0; x < columns; x++) for (int top = 0; top < rows; top++)
                for (int length = 1; length <= 64 && top + length <= rows; length++)
                {
                    ulong expected = 0; int count = 0, first = -1, last = -1;
                    for (int y = 0; y < length; y++)
                        if ((words[((top + y) >> 6) * columns + x] & (1UL << ((top + y) & 63))) != 0)
                        { expected |= 1UL << y; count++; if (first < 0) first = y; last = y; }
                    ulong actual = HealthBarDetector.ColumnWindow(words, columns, x, top, length);
                    if (actual != expected || HealthBarDetector.BitCount(actual) != count ||
                        HealthBarDetector.FirstBit(actual) != first || HealthBarDetector.LastBit(actual) != last)
                        throw new Exception("Packed numeral-column mismatch at x=" + x + ", top=" + top + ", height=" + length);
                }
        }
    }
}
