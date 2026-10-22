using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using PrefireMacro;

namespace PrefireDistribution
{
    internal sealed class Release
    {
        internal Version Version;
        internal Uri Url;
        internal string Hash, Text;
        internal int Size;
        internal static Release Read(string text)
        { return Read(text, null); }
        internal static Release Read(string text, string expectedName)
        {
            string[] p = text.Replace("\r", "").TrimEnd('\n').Split('\n');
            if (p.Length != 5) throw new InvalidDataException("Ungültige Update-Datei.");
            byte[] body = Encoding.UTF8.GetBytes(String.Join("\n", p, 0, 4) + "\n");
            using (RSACryptoServiceProvider rsa = new RSACryptoServiceProvider())
            {
                rsa.PersistKeyInCsp = false; rsa.FromXmlString(LoaderSettings.PublicKey);
                if (!rsa.VerifyData(body, CryptoConfig.MapNameToOID("SHA256"), Convert.FromBase64String(p[4]))) throw new InvalidDataException("Die Update-Signatur ist ungültig.");
            }
            Release r = new Release { Version = new Version(p[0]), Url = new Uri(p[1]), Hash = p[2], Size = Int32.Parse(p[3]), Text = text };
            if (r.Url.Scheme != "https" || r.Size < 1024 || r.Size > 32 * 1024 * 1024 || r.Hash.Length != 64) throw new InvalidDataException("Ungültige Update-Angaben.");
            foreach (char c in r.Hash) if (!Uri.IsHexDigit(c)) throw new InvalidDataException("Ungültige Prüfsumme.");
            string[] path = r.Url.AbsolutePath.Split('/');
            string name = path[path.Length - 1];
            if (!r.Url.AbsoluteUri.StartsWith(LoaderSettings.DownloadPrefix, StringComparison.Ordinal) ||
                path.Length != 7 || String.IsNullOrEmpty(path[5]) || r.Url.Query.Length != 0 || r.Url.Fragment.Length != 0 ||
                (name != "PrefireMacro.exe" && name != "PrefireLoader.exe") || (expectedName != null && name != expectedName))
                throw new InvalidDataException("Die Freigabe gehört nicht zur erwarteten Prefire-Datei auf GitHub.");
            return r;
        }
        internal void Check(byte[] bytes)
        { if (bytes.Length != Size || !String.Equals(HashBytes(bytes), Hash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Der Download ist unvollständig oder wurde verändert."); }
        internal static string HashBytes(byte[] bytes)
        { using (SHA256 sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
    }
    internal sealed class UpdateEngine
    {
        internal readonly string Root;
        readonly Func<Uri, int, byte[]> fetch;
        internal string AppPath { get; private set; }
        internal Action BeforeActivation;
        string PayloadPath(Release release) { return Path.Combine(Root, "App", release.Version + "-" + release.Hash.ToLowerInvariant(), "PrefireMacro.exe"); }
        string BundledPath { get { return Path.Combine(Root, "App", LoaderSettings.BundledVersion + "-" + LoaderSettings.BundledHash, "PrefireMacro.exe"); } }
        internal UpdateEngine(string root, Func<Uri, int, byte[]> transport) { Root = root; fetch = transport; }
        internal static byte[] Download(Uri uri, int limit)
        {
            if (uri.Scheme != "https") throw new InvalidDataException("Nur HTTPS-Downloads sind erlaubt.");
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(uri); request.Timeout = request.ReadWriteTimeout = 12000;
            request.UserAgent = "PrefireLoader/1"; request.CachePolicy = new System.Net.Cache.RequestCachePolicy(System.Net.Cache.RequestCacheLevel.NoCacheNoStore);
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            {
                if (response.ResponseUri.Scheme != "https" || response.ContentLength > limit) throw new InvalidDataException("Ungültige Download-Antwort.");
                using (Stream input = response.GetResponseStream()) using (MemoryStream output = new MemoryStream())
                { byte[] buffer = new byte[16384]; int n; while ((n = input.Read(buffer, 0, buffer.Length)) != 0) { if (output.Length + n > limit) throw new InvalidDataException("Download zu groß."); output.Write(buffer, 0, n); } return output.ToArray(); }
            }
        }
        static byte[] Bundled()
        {
            using (Stream input = Assembly.GetExecutingAssembly().GetManifestResourceStream("main.gz"))
            using (GZipStream gzip = new GZipStream(input, CompressionMode.Decompress)) using (MemoryStream output = new MemoryStream())
            { gzip.CopyTo(output); byte[] bytes = output.ToArray(); if (Release.HashBytes(bytes) != LoaderSettings.BundledHash) throw new InvalidDataException("Die mitgelieferte Version ist beschädigt."); return bytes; }
        }
        internal static void AtomicWrite(string path, byte[] bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)); string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllBytes(temporary, bytes); if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        internal string Install(Action<string> status)
        {
            string manifestPath = Path.Combine(Root, "installed.release"); Release installed = null;
            if (File.Exists(manifestPath)) try { installed = Release.Read(File.ReadAllText(manifestPath), "PrefireMacro.exe"); installed.Check(File.ReadAllBytes(PayloadPath(installed))); AppPath = PayloadPath(installed); } catch { installed = null; }
            Version local = new Version(LoaderSettings.BundledVersion);
            bool validLocal = installed != null;
            if (validLocal) local = installed.Version;
            else if (File.Exists(BundledPath)) { validLocal = Release.HashBytes(File.ReadAllBytes(BundledPath)) == LoaderSettings.BundledHash; if (validLocal) AppPath = BundledPath; }
            string outcome = "Mitgelieferte Version bereit.";
            if (!String.IsNullOrEmpty(LoaderSettings.Feed))
            {
                try
                {
                    status("Neueste Version prüfen …"); byte[] manifest = fetch(new Uri(LoaderSettings.Feed), 16384);
                    if (manifest.Length > 16384) throw new InvalidDataException("Update-Datei zu groß."); Release latest = Release.Read(Encoding.UTF8.GetString(manifest), "PrefireMacro.exe");
                    if (latest.Version < local) throw new InvalidDataException("Ein älteres Update wurde abgelehnt.");
                    if (!validLocal || latest.Version > local || !String.Equals(latest.Hash, Release.HashBytes(File.ReadAllBytes(AppPath)), StringComparison.OrdinalIgnoreCase))
                    {
                        status("Version " + latest.Version + " herunterladen …"); byte[] binary = fetch(latest.Url, latest.Size); latest.Check(binary);
                        string payload = PayloadPath(latest);
                        AtomicWrite(payload, binary);
                        if (BeforeActivation != null) BeforeActivation();
                        AtomicWrite(manifestPath, Encoding.UTF8.GetBytes(latest.Text));
                        AppPath = payload; validLocal = true;
                    }
                    outcome = "Version " + latest.Version + " ist bereit.";
                }
                catch (Exception ex) { outcome = "Updateprüfung fehlgeschlagen: " + ex.Message + "\n" + (validLocal ? "Die geprüfte installierte Version wird verwendet." : "Die mitgelieferte Version wird verwendet."); }
            }
            else outcome = "Der Online-Downloadort ist noch nicht freigeschaltet. Die mitgelieferte Version wird verwendet.";
            if (!validLocal) { status("Mitgelieferte Version installieren …"); AppPath = BundledPath; AtomicWrite(AppPath, Bundled()); }
            return outcome;
        }
    }
    internal sealed class LoaderWindow : Form
    {
        readonly Label state = new Label(), detail = new Label();
        readonly Button start = new RoundedButton(), retry = new RoundedButton();
        readonly ProgressBar progress = new ProgressBar();
        readonly UpdateEngine engine;
        bool ready, busy;
        readonly bool previewOnly;
        internal LoaderWindow(bool preview = false)
        {
            previewOnly = preview;
            engine = new UpdateEngine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PrefireMacro"), UpdateEngine.Download);
            Text = "Prefire · Updates"; Font = new Font("Segoe UI", 11); AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(640, 470); MinimumSize = new Size(590, 470); StartPosition = FormStartPosition.CenterScreen; MaximizeBox = false;
            TableLayoutPanel grid = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(28), ColumnCount = 1, RowCount = 5 };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 52)); grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 40)); grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 60)); grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); Controls.Add(grid);
            Label title = new Label { Text = "Prefire", Font = new Font(Font.FontFamily, 24, FontStyle.Bold), Dock = DockStyle.Fill, Margin = Padding.Empty }; grid.Controls.Add(title, 0, 0);
            Label subtitle = new Label { Text = "Einmal weitergeben. Gemeinsam aktuell bleiben.", Dock = DockStyle.Fill, ForeColor = DarkTheme.Muted, Margin = Padding.Empty }; grid.Controls.Add(subtitle, 0, 1);
            TableLayoutPanel card = new RoundedCard { Dock = DockStyle.Fill, BackColor = DarkTheme.Surface, Padding = new Padding(20), Margin = new Padding(0, 4, 0, 14), ColumnCount = 1, RowCount = 3 };
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); card.RowStyles.Add(new RowStyle(SizeType.Absolute, 40)); card.RowStyles.Add(new RowStyle(SizeType.Absolute, 18)); card.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); grid.Controls.Add(card, 0, 2);
            state.Dock = DockStyle.Fill; state.Font = new Font(Font, FontStyle.Bold); state.AutoEllipsis = true; state.Text = "Update vorbereiten …"; state.Margin = Padding.Empty; state.AccessibleName = "Status der Updateprüfung"; card.Controls.Add(state, 0, 0);
            progress.Dock = DockStyle.Fill; progress.Height = 5; progress.Margin = new Padding(0, 0, 0, 10); progress.Style = ProgressBarStyle.Marquee; progress.MarqueeAnimationSpeed = 25; progress.AccessibleName = "Updateprüfung läuft"; card.Controls.Add(progress, 0, 1);
            detail.Dock = DockStyle.Fill; detail.ForeColor = DarkTheme.Muted; detail.Margin = new Padding(0, 12, 0, 0); detail.Text = "Prüft GitHub Releases auf eine neue, signierte Prefire-Version.\n\nEinstellungen und Aufnahmen bleiben auf diesem PC."; detail.AutoEllipsis = true; detail.AccessibleName = "Details der Updateprüfung"; card.Controls.Add(detail, 0, 2);
            Label footnote = new Label { Text = "Starte künftig über den Loader: Er prüft jedes Mal auf Updates.\nPrefire startet pausiert. Die Farben richtet jeder PC einmal selbst ein.", Dock = DockStyle.Fill, ForeColor = DarkTheme.Muted, Font = new Font(Font.FontFamily, 10.5f), Margin = Padding.Empty }; grid.Controls.Add(footnote, 0, 3);
            TableLayoutPanel actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); grid.Controls.Add(actions, 0, 4);
            retry.Text = "Erneut prüfen"; retry.Dock = DockStyle.Fill; retry.Margin = new Padding(0, 0, 8, 0); retry.Enabled = false; retry.AccessibleName = "Updates erneut prüfen"; actions.Controls.Add(retry, 0, 0);
            start.Text = "Prefire starten"; start.Dock = DockStyle.Fill; start.Margin = new Padding(8, 0, 0, 0); start.Enabled = false; actions.Controls.Add(start, 1, 0);
            DarkTheme.Apply(this); DarkTheme.Button(start, true); AcceptButton = start;
            start.Click += delegate { if (ready && !busy && !previewOnly) Launch(); }; retry.Click += delegate { BeginPrepare(); };
            Shown += delegate { if (!previewOnly) BeginPrepare(); else Complete(true, "Version " + LoaderSettings.BundledVersion + " bereit", "Du bist auf dem aktuellen Stand.\nUpdates werden beim nächsten Start erneut geprüft."); };
        }
        void Post(Action action) { if (!IsDisposed && IsHandleCreated) try { BeginInvoke((Action)delegate { if (!IsDisposed) action(); }); } catch (InvalidOperationException) { } }
        void Status(string text) { Post(delegate { state.Text = text; }); }
        void BeginPrepare() {
            if (busy || previewOnly) return; busy = true; ready = false; start.Enabled = retry.Enabled = false;
            progress.Visible = true; progress.MarqueeAnimationSpeed = 25; state.Text = "Auf Updates prüfen …";
            detail.Text = "Die Verbindung wird geprüft. Bei fehlender Verbindung bleibt eine geprüfte lokale Version verfügbar.";
            ThreadPool.QueueUserWorkItem(delegate { Prepare(); });
        }
        void Complete(bool success, string title, string body) {
            busy = false; ready = success; state.Text = title; detail.Text = body; progress.MarqueeAnimationSpeed = 0; progress.Visible = false;
            retry.Enabled = true; start.Enabled = success; if (success) start.Focus(); else retry.Focus();
        }
        internal void Review(string path) {
            if (!previewOnly) throw new InvalidOperationException("Review requires preview mode");
            Action<string> save = delegate(string file) { Application.DoEvents(); using (Bitmap bitmap = new Bitmap(Width, Height)) { DrawToBitmap(bitmap, new Rectangle(Point.Empty, Size)); bitmap.Save(file); } };
            if (!ready || !start.Enabled || !retry.Enabled || progress.Visible) throw new Exception("Ready controls"); save(path);
            Complete(true, "Lokal bereit · Verbindung fehlt", "Updateprüfung fehlgeschlagen: Keine Verbindung.\nDie geprüfte installierte Version wird verwendet."); save(Path.ChangeExtension(path, ".offline.png"));
            Complete(false, "Installation nicht abgeschlossen", "Die Programmdatei konnte nicht gespeichert werden.\nPrüfe den Speicherplatz und wähle „Erneut prüfen“.");
            if (start.Enabled || !retry.Enabled || progress.Visible) throw new Exception("Retry controls"); save(Path.ChangeExtension(path, ".error.png"));
            busy = true; ready = false; start.Enabled = retry.Enabled = false; state.Text = "Version herunterladen …"; detail.Text = "Die signierte Version wird geladen und geprüft."; progress.Visible = true; progress.MarqueeAnimationSpeed = 25; save(Path.ChangeExtension(path, ".loading.png"));
            if (start.Enabled || retry.Enabled || !progress.Visible) throw new Exception("Busy controls");
            Complete(true, "Bereit", "Vorschau"); ClientSize = new Size(590, 470); save(Path.ChangeExtension(path, ".compact.png"));
            File.WriteAllText(Path.ChangeExtension(path, ".result.txt"), "PASS: ready, offline fallback, recoverable error, loading and compact layouts rendered; start/retry availability checked. Preview only: no network, install or program launch.");
        }
        void Prepare()
        {
            try
            {
                if (LoaderProgram.TryUpdate(engine.Root, Status))
                { if (!IsDisposed && IsHandleCreated) BeginInvoke((Action)delegate { Close(); }); return; }
                string message = engine.Install(Status);
                string loader = LoaderProgram.EnsureCanonicalLoader(engine.Root, Assembly.GetExecutingAssembly().Location);
                string shortcutNote = "";
                try
                {
                    Type shellType = Type.GetTypeFromProgID("WScript.Shell"); object shell = Activator.CreateInstance(shellType);
                    object shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Prefire starten.lnk") });
                    Type t = shortcut.GetType(); t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { loader });
                    t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { engine.Root }); t.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
                    System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut); System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
                }
                catch { shortcutNote = "\nDesktop-Verknüpfung konnte nicht angelegt werden. Zum Starten weiterhin diese Datei öffnen."; }
                Post(delegate { Complete(true, "Prefire ist bereit", message + shortcutNote); });
            }
            catch (Exception ex) { Post(delegate { Complete(false, "Installation nicht abgeschlossen", ex.Message + "\n\nPrüfe die Verbindung und wähle „Erneut prüfen“."); }); }
        }
        void Launch()
        {
            try
            {
                foreach (Process process in Process.GetProcessesByName("PrefireMacro")) using (process)
                    if (!process.HasExited) { detail.Text = "Prefire läuft bereits. Schließe das vorhandene Fenster, dann hier erneut starten."; return; }
                ProcessStartInfo launch = new ProcessStartInfo(engine.AppPath) { WorkingDirectory = engine.Root, UseShellExecute = false };
                launch.EnvironmentVariables["PREFIRE_RECORDING_ROOT"] = Path.Combine(engine.Root, "Aufnahmen");
                Process.Start(launch); Close();
            }
            catch (Exception ex) { detail.Text = "Start fehlgeschlagen: " + ex.Message; }
        }
    }
    internal static class LoaderProgram
    {
        static void EngineTests(string directory)
        {
            Directory.CreateDirectory(directory);
            string fixtures = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "fixtures");
            byte[] binary = File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "public", "PrefireMacro.exe"));
            byte[] manifest = null, payload = binary;
            bool offline = true; int downloads = 0, passed = 0;
            Action<bool, string> check = delegate(bool ok, string text) { if (!ok) throw new Exception(text); passed++; };
            Func<Uri, int, byte[]> transport = delegate(Uri uri, int limit) { if (offline) throw new WebException("Offline fixture"); if (uri.AbsolutePath.EndsWith(".release")) return manifest; downloads++; return payload; };
            UpdateEngine engine = new UpdateEngine(Path.Combine(directory, "installation"), transport);
            engine.Install(delegate { }); check(Release.HashBytes(File.ReadAllBytes(engine.AppPath)) == LoaderSettings.BundledHash, "offline first installation");
            offline = false; manifest = File.ReadAllBytes(Path.Combine(fixtures, "upgrade.release"));
            engine.Install(delegate { }); string upgraded = engine.AppPath;
            check(upgraded.Contains("99.0.0.0-") && downloads == 1, "verified upgrade activated");
            engine.Install(delegate { }); check(downloads == 1, "current version needs no binary download");
            offline = true; engine = new UpdateEngine(engine.Root, transport); engine.Install(delegate { }); check(engine.AppPath == upgraded, "offline signed cached version");
            offline = false; manifest = File.ReadAllBytes(Path.Combine(fixtures, "older.release")); engine.Install(delegate { }); check(engine.AppPath == upgraded && downloads == 1, "downgrade rejected");
            foreach (string invalidSource in new string[] { "wrong-file.release", "wrong-repo.release", "wrong-host.release", "mutable-url.release" })
            {
                manifest = File.ReadAllBytes(Path.Combine(fixtures, invalidSource));
                engine.Install(delegate { });
                check(engine.AppPath == upgraded && downloads == 1, "valid signature still refuses unexpected GitHub asset/source: " + invalidSource);
            }
            manifest = File.ReadAllBytes(Path.Combine(fixtures, "next.release")); byte[] valid = (byte[])manifest.Clone(); manifest[0] ^= 1;
            engine.Install(delegate { }); check(engine.AppPath == upgraded && downloads == 1, "changed signed manifest rejected before download");
            manifest = valid; payload = new byte[1024]; engine.Install(delegate { }); check(engine.AppPath == upgraded, "truncated download retains current version");
            payload = (byte[])binary.Clone(); payload[100] ^= 1; engine.Install(delegate { }); check(engine.AppPath == upgraded, "same-size corrupted download rejected");
            manifest = Encoding.UTF8.GetBytes("invalid"); engine.Install(delegate { }); check(engine.AppPath == upgraded, "malformed manifest retains current version");
            manifest = valid; payload = binary; engine.BeforeActivation = delegate { throw new IOException("Simulated interruption before activation"); };
            engine.Install(delegate { });
            UpdateEngine recovered = new UpdateEngine(engine.Root, transport); offline = true; recovered.Install(delegate { });
            check(recovered.AppPath == upgraded, "interruption after payload keeps previous signed pointer active");
            offline = false; engine.BeforeActivation = null; engine.Install(delegate { }); check(engine.AppPath.Contains("100.0.0.0-"), "subsequent valid upgrade succeeds after failures");
            check(File.Exists(upgraded), "previous payload survives activation");
            offline = true; UpdateEngine.AtomicWrite(engine.AppPath, new byte[1024]); engine = new UpdateEngine(engine.Root, transport); engine.Install(delegate { });
            check(Release.HashBytes(File.ReadAllBytes(engine.AppPath)) == LoaderSettings.BundledHash, "corrupt cache offline repaired with verified bundle");
            string publicRoot = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "public");
            string selfRoot = Path.Combine(directory, "self-update");
            string signedLoader = File.ReadAllText(Path.Combine(publicRoot, "loader.release")); Release loaderRelease = Release.Read(signedLoader, "PrefireLoader.exe");
            string staged = Path.Combine(selfRoot, "LoaderUpdates", loaderRelease.Hash.ToLowerInvariant(), "PrefireLoader.exe");
            UpdateEngine.AtomicWrite(staged, File.ReadAllBytes(Path.Combine(publicRoot, "PrefireLoader.exe")));
            string stagedManifest = Path.Combine(Path.GetDirectoryName(staged), "loader.release");
            UpdateEngine.AtomicWrite(stagedManifest, Encoding.UTF8.GetBytes(signedLoader));
            int waited = 0;
            ApplyLoader(selfRoot, staged, delegate { waited++; });
            check(waited == 1 && Release.HashBytes(File.ReadAllBytes(Path.Combine(selfRoot, "PrefireLoader.exe"))) == loaderRelease.Hash, "verified staged self-update atomically replaces canonical loader after wait");
            UpdateEngine.AtomicWrite(staged, new byte[1024]); bool rejected = false;
            try { ApplyLoader(selfRoot, staged, delegate { waited++; }); } catch (InvalidDataException) { rejected = true; }
            check(rejected && waited == 1 && Release.HashBytes(File.ReadAllBytes(Path.Combine(selfRoot, "PrefireLoader.exe"))) == loaderRelease.Hash, "corrupt staged loader rejected before parent wait and replacement");
            string oldCopy = Path.Combine(directory, "old-loader.exe"); byte[] oldBytes = new byte[2048]; oldBytes[0] = 77; oldBytes[1] = 90; File.WriteAllBytes(oldCopy, oldBytes);
            string kept = EnsureCanonicalLoader(selfRoot, oldCopy);
            check(Release.HashBytes(File.ReadAllBytes(kept)) == loaderRelease.Hash, "an old offline source cannot overwrite a valid signed canonical loader");
            string freshRoot = Path.Combine(directory, "fresh-loader-root"); string copied = EnsureCanonicalLoader(freshRoot, oldCopy);
            check(Release.HashBytes(File.ReadAllBytes(copied)) == Release.HashBytes(oldBytes), "first canonical copy remains available offline");
            File.WriteAllBytes(copied, new byte[1024]); EnsureCanonicalLoader(freshRoot, oldCopy);
            check(Release.HashBytes(File.ReadAllBytes(copied)) == Release.HashBytes(oldBytes), "unverified corrupt canonical copy is repaired from current source");
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(staged), "loader.release"), "invalid"); EnsureCanonicalLoader(selfRoot, oldCopy);
            check(Release.HashBytes(File.ReadAllBytes(kept)) == Release.HashBytes(oldBytes), "an invalid signature marker cannot make another canonical file trusted");
            File.WriteAllText(Path.Combine(directory, "result.txt"), "PASS: " + passed + " loader checks: offline install, signed upgrade, no repeated payload, offline cached upgrade, rollback, signature tamper, truncation, hash tamper, malformed feed, interrupted activation, recovery, previous payload retained, corrupt cache repair, staged self-replacement, corrupt stage rejection, signed canonical preservation, first canonical copy, corrupt canonical repair and invalid marker handling. Parent wait simulated; no program launched; no game input.");
        }
        internal static string EnsureCanonicalLoader(string root, string self)
        {
            string canonical = Path.Combine(root, "PrefireLoader.exe");
            if (String.Equals(Path.GetFullPath(self), Path.GetFullPath(canonical), StringComparison.OrdinalIgnoreCase)) return canonical;
            byte[] current = File.ReadAllBytes(self);
            if (File.Exists(canonical))
            {
                byte[] previous = File.ReadAllBytes(canonical); string hash = Release.HashBytes(previous);
                if (hash == Release.HashBytes(current)) return canonical;
                try
                {
                    // A completed self-update retains its signed manifest in this hash-addressed directory.
                    // Do not undo it merely because somebody opens an older downloaded copy while offline.
                    Release installed = Release.Read(File.ReadAllText(Path.Combine(root, "LoaderUpdates", hash, "loader.release")), "PrefireLoader.exe");
                    installed.Check(previous);
                    if (installed.Version >= new Version(LoaderSettings.LoaderVersion)) return canonical;
                }
                catch (InvalidDataException) { }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (FormatException) { }
                catch (CryptographicException) { }
                catch (ArgumentException) { }
                catch (OverflowException) { }
            }
            UpdateEngine.AtomicWrite(canonical, current); return canonical;
        }
        internal static bool TryUpdate(string root, Action<string> status)
        {
            try
            {
                status("Loader-Version prüfen …");
                Release latest = Release.Read(Encoding.UTF8.GetString(UpdateEngine.Download(new Uri(LoaderSettings.LoaderFeed), 16384)), "PrefireLoader.exe");
                if (latest.Version <= new Version(LoaderSettings.LoaderVersion)) return false;
                byte[] bytes = UpdateEngine.Download(latest.Url, latest.Size); latest.Check(bytes);
                string stage = Path.Combine(root, "LoaderUpdates", latest.Hash.ToLowerInvariant(), "PrefireLoader.exe");
                UpdateEngine.AtomicWrite(stage, bytes);
                UpdateEngine.AtomicWrite(Path.Combine(Path.GetDirectoryName(stage), "loader.release"), Encoding.UTF8.GetBytes(latest.Text));
                Process.Start(new ProcessStartInfo(stage, "--replace-loader " + Process.GetCurrentProcess().Id) { UseShellExecute = false, WorkingDirectory = root });
                return true;
            }
            catch (Exception ex) { status("Loader bleibt lokal: " + ex.Message); return false; }
        }
        static void ReplaceLoader(int parent)
        {
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PrefireMacro");
            string self = Assembly.GetExecutingAssembly().Location;
            ApplyLoader(root, self, delegate {
                try { using (Process previous = Process.GetProcessById(parent)) { if (!previous.WaitForExit(20000)) throw new IOException("Alter Loader wurde nicht geschlossen."); } } catch (ArgumentException) { }
            });
            Process.Start(new ProcessStartInfo(Path.Combine(root, "PrefireLoader.exe")) { UseShellExecute = false, WorkingDirectory = root });
        }
        static void ApplyLoader(string root, string self, Action waitForParent)
        {
            Release release = Release.Read(File.ReadAllText(Path.Combine(Path.GetDirectoryName(self), "loader.release")), "PrefireLoader.exe");
            string expected = Path.Combine(root, "LoaderUpdates", release.Hash.ToLowerInvariant(), "PrefireLoader.exe");
            if (!String.Equals(Path.GetFullPath(self), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase) || release.Version != new Version(LoaderSettings.LoaderVersion)) throw new InvalidDataException("Ungültige Loader-Aktualisierung.");
            byte[] bytes = File.ReadAllBytes(self); release.Check(bytes);
            waitForParent();
            using (Mutex install = new Mutex(false, "Local\\PrefireDistribution.Loader"))
            {
                bool acquired = false;
                try
                {
                    try { acquired = install.WaitOne(5000); } catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) throw new IOException("Ein anderer Loader ist geöffnet.");
                    UpdateEngine.AtomicWrite(Path.Combine(root, "PrefireLoader.exe"), bytes);
                }
                finally { if (acquired) install.ReleaseMutex(); }
            }
        }
        [STAThread] static void Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--ui-review")
            {
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                using (LoaderWindow window = new LoaderWindow(true)) { window.Show(); Application.DoEvents(); window.Review(args[1]); }
                return;
            }
            if (args.Length == 2 && args[0] == "--self-test")
            { try { EngineTests(Path.GetFullPath(args[1])); } catch (Exception ex) { Directory.CreateDirectory(args[1]); File.WriteAllText(Path.Combine(args[1], "failure.txt"), ex.ToString()); Environment.ExitCode = 1; } return; }
            if (args.Length == 2 && args[0] == "--replace-loader")
            { try { ReplaceLoader(Int32.Parse(args[1])); } catch (Exception ex) { MessageBox.Show(ex.Message, "Loader-Update nicht abgeschlossen"); Environment.ExitCode = 1; } return; }
            if (args.Length == 2 && (args[0] == "--install-test" || args[0] == "--online-test"))
            {
                try { string directory = Path.GetFullPath(args[1]); bool online = args[0] == "--online-test"; UpdateEngine engine = new UpdateEngine(directory, online ? (Func<Uri, int, byte[]>)UpdateEngine.Download : delegate { throw new WebException("Offline test"); }); string result = engine.Install(delegate { }); if (online && !File.Exists(Path.Combine(directory, "installed.release"))) throw new Exception("Online-Installation war nicht erfolgreich: " + result); File.WriteAllText(Path.Combine(directory, "test-result.txt"), "PASS: " + result + "\nEXE=" + Release.HashBytes(File.ReadAllBytes(engine.AppPath))); }
                catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; } return;
            }
            bool created;
            using (Mutex instance = new Mutex(true, "Local\\PrefireDistribution.Loader", out created))
            {
                if (!created) { MessageBox.Show("Der Loader ist bereits geöffnet.", "Prefire"); return; }
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); Application.Run(new LoaderWindow());
            }
        }
    }
}
