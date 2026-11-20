// In-place updates from GitHub Releases.
//
//   check    GET api.github.com/repos/byocao/mini-stats/releases/latest, compare the tag with our version
//   download MiniStats-win-x64.zip (release asset) with progress
//   verify   SHA256 of the zip against the published .sha256 asset (and GitHub's asset digest when present),
//            then check that the extracted exe really is MiniStats <new version>
//   install  rename the running exe to MiniStats.exe.old, put the new exe in its place; the caller relaunches.
//            Any failure before the relaunch puts the old exe back.
//
// All network and disk work runs on a background thread. Changed is raised on the thread that created the
// Updater (the UI thread). Only api.github.com / github.com are contacted, and only to read public release data.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

class UpdateInfo
{
    public Version Version;
    public string Tag, Title, Notes, PageUrl, ZipUrl, ShaUrl, Digest;
    public long ZipSize;
    public DateTime Published;
}

enum UpdateState { Idle, Checking, UpToDate, Available, Downloading, Verifying, Installing, Error }

class Updater
{
    public const string LatestApi = "https://api.github.com/repos/byocao/mini-stats/releases/latest";
    public const string AssetName = "MiniStats-win-x64.zip";

    readonly SynchronizationContext ui;
    readonly object gate = new object();

    public Version Current { get; private set; }
    public UpdateState State { get; private set; }
    public UpdateInfo Latest { get; private set; }
    public string Error { get; private set; }
    public int Progress { get; private set; }        // 0-100 while downloading
    public DateTime LastChecked { get; private set; } // local time, DateTime.MinValue = never
    public event EventHandler Changed;

    // HTTP GET returning text (url, Accept header). Replaceable for tests.
    public Func<string, string, string> GetText = DefaultGetText;

    public Updater(Version current)
    {
        Current = Normalize(current);
        // only keep a real UI context (WinForms / WPF); the plain base context would post to the thread pool
        var ctx = SynchronizationContext.Current;
        ui = ctx != null && ctx.GetType() != typeof(SynchronizationContext) ? ctx : null;
    }

    public bool IsBusy
    {
        get { var s = State; return s == UpdateState.Checking || s == UpdateState.Downloading || s == UpdateState.Verifying || s == UpdateState.Installing; }
    }

    // ------------------------------------------------------------------ state
    void Set(UpdateState s, string error)
    {
        State = s;
        Error = error;
        Raise();
    }

    void Raise()
    {
        var h = Changed;
        if (h == null) return;
        if (ui != null) ui.Post(delegate { h(this, EventArgs.Empty); }, null);
        else h(this, EventArgs.Empty);
    }

    // Shows a failure that happened outside the updater (e.g. the relaunch) in the same UI.
    public void Fail(string message) { Set(UpdateState.Error, message); }

    // Used by the screenshot / self-test tooling to put the UI into a given state.
    public void Simulate(UpdateState state, UpdateInfo info, int progress)
    {
        Latest = info;
        Progress = progress;
        LastChecked = DateTime.Now;
        Set(state, state == UpdateState.Error ? "Couldn't reach GitHub." : null);
    }

    // ------------------------------------------------------------------ check
    public void CheckAsync()
    {
        lock (gate)
        {
            if (IsBusy) return;
            Set(UpdateState.Checking, null);
        }
        var t = new Thread(delegate () { CheckNow(); }) { IsBackground = true, Name = "UpdateCheck" };
        t.Start();
    }

    // Synchronous check (background thread or tests).
    public void CheckNow()
    {
        try
        {
            var info = Parse(GetText(LatestApi, "application/vnd.github+json"));
            Latest = info;
            LastChecked = DateTime.Now;
            Set(info.Version > Current ? UpdateState.Available : UpdateState.UpToDate, null);
        }
        catch (Exception ex)
        {
            LastChecked = DateTime.Now;
            Set(UpdateState.Error, Friendly(ex));
        }
    }

    public static UpdateInfo Parse(string json)
    {
        var o = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 }.Deserialize<Dictionary<string, object>>(json);
        var info = new UpdateInfo();
        info.Tag = Str(o, "tag_name");
        info.Version = ParseTag(info.Tag);
        if (info.Version == null) throw new InvalidDataException("Unexpected release tag \"" + info.Tag + "\".");
        info.Title = Str(o, "name") ?? info.Tag;
        info.Notes = Str(o, "body") ?? "";
        info.PageUrl = Str(o, "html_url");
        DateTime pub;
        if (DateTime.TryParse(Str(o, "published_at"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out pub))
            info.Published = pub.ToLocalTime();

        object assets;
        if (o.TryGetValue("assets", out assets) && assets is IEnumerable)
            foreach (var item in (IEnumerable)assets)
            {
                var a = item as Dictionary<string, object>;
                if (a == null) continue;
                string name = Str(a, "name");
                if (name == AssetName)
                {
                    info.ZipUrl = Str(a, "url"); // API URL; the binary comes with Accept: application/octet-stream
                    info.ZipSize = a.ContainsKey("size") ? Convert.ToInt64(a["size"], CultureInfo.InvariantCulture) : 0;
                    info.Digest = Str(a, "digest");
                }
                else if (name == AssetName + ".sha256") info.ShaUrl = Str(a, "url");
            }
        if (info.ZipUrl == null) throw new InvalidDataException("The latest release has no " + AssetName + ".");
        return info;
    }

    static string Str(Dictionary<string, object> o, string key)
    {
        object v;
        return o.TryGetValue(key, out v) ? v as string : null;
    }

    public static Version ParseTag(string tag)
    {
        if (string.IsNullOrEmpty(tag)) return null;
        tag = tag.Trim();
        if (tag.StartsWith("v", StringComparison.OrdinalIgnoreCase)) tag = tag.Substring(1);
        int dash = tag.IndexOfAny(new[] { '-', '+', ' ' });
        if (dash > 0) tag = tag.Substring(0, dash);
        Version v;
        return Version.TryParse(tag, out v) ? Normalize(v) : null;
    }

    static Version Normalize(Version v) { return new Version(v.Major, v.Minor, Math.Max(0, v.Build)); }

    // ------------------------------------------------------------------ install
    // Downloads, verifies and swaps in the new exe on a background thread, then calls relaunch on the UI thread.
    public void InstallAsync(string exePath, Action<string> relaunch)
    {
        UpdateInfo info;
        lock (gate)
        {
            info = Latest;
            if (info == null || IsBusy) return;
            Progress = 0;
            Set(UpdateState.Downloading, null);
        }
        var t = new Thread(delegate ()
        {
            try
            {
                string newExe = Prepare(info);
                Set(UpdateState.Installing, null);
                Swap(exePath, newExe);
                if (ui != null) ui.Post(delegate { relaunch(exePath); }, null);
                else relaunch(exePath);
            }
            catch (Exception ex) { Set(UpdateState.Error, Friendly(ex)); }
        }) { IsBackground = true, Name = "UpdateInstall" };
        t.Start();
    }

    // Download + verify + extract. Returns the path of the verified new MiniStats.exe in a temp folder.
    public string Prepare(UpdateInfo info)
    {
        string work = Path.Combine(Path.GetTempPath(), "MiniStats-update-" + info.Version);
        if (Directory.Exists(work)) Directory.Delete(work, true);
        Directory.CreateDirectory(work);

        string zip = Path.Combine(work, AssetName);
        Progress = 0;
        Set(UpdateState.Downloading, null);
        Download(info.ZipUrl, zip, info.ZipSize);

        Set(UpdateState.Verifying, null);
        string expected = ExpectedHash(info);
        string actual = Sha256(zip);
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The download did not match the published checksum, so it was not installed.");

        string newExe = Path.Combine(work, "MiniStats.exe");
        using (var z = ZipFile.OpenRead(zip))
        {
            var entry = z.GetEntry("MiniStats.exe");
            if (entry == null) throw new InvalidDataException("The release package does not contain MiniStats.exe.");
            entry.ExtractToFile(newExe, true);
        }
        var fvi = FileVersionInfo.GetVersionInfo(newExe);
        if (fvi.ProductName != "MiniStats" || ParseTag(fvi.ProductVersion) != info.Version)
            throw new InvalidDataException("The downloaded file is not MiniStats " + info.Version + ".");
        return newExe;
    }

    string ExpectedHash(UpdateInfo info)
    {
        string fromFile = null, fromDigest = null;
        if (info.ShaUrl != null)
        {
            var text = GetText(info.ShaUrl, "application/octet-stream").Trim();
            fromFile = text.Split(new[] { ' ', '\t', '\r', '\n', '*' }, StringSplitOptions.RemoveEmptyEntries)[0];
        }
        if (!string.IsNullOrEmpty(info.Digest) && info.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            fromDigest = info.Digest.Substring(7);
        if (fromFile != null && fromDigest != null && !string.Equals(fromFile, fromDigest, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The published checksums disagree, so the update was not installed.");
        var h = fromFile ?? fromDigest;
        if (h == null || h.Length != 64) throw new InvalidDataException("The release has no usable checksum, so it was not installed.");
        return h;
    }

    // Swap the running exe for the new one. Windows lets a running exe be renamed, not overwritten.
    public static void Swap(string exePath, string newExe)
    {
        string old = exePath + ".old";
        if (File.Exists(old))
        {
            try { File.Delete(old); }
            catch { old = exePath + "." + DateTime.Now.Ticks + ".old"; } // a previous .old is still locked
        }
        File.Move(exePath, old);
        try { File.Copy(newExe, exePath, true); }
        catch
        {
            try { if (File.Exists(exePath)) File.Delete(exePath); } catch { }
            File.Move(old, exePath);
            throw;
        }
    }

    // Puts the previous exe back (used when the new version cannot be started).
    public static void Rollback(string exePath)
    {
        string old = exePath + ".old";
        if (!File.Exists(old)) return;
        if (File.Exists(exePath)) File.Delete(exePath);
        File.Move(old, exePath);
    }

    // Removes MiniStats.exe*.old left behind by an update (retries while the old process is still exiting).
    public static void CleanupOld(string exePath)
    {
        string dir = Path.GetDirectoryName(exePath), name = Path.GetFileName(exePath);
        for (int attempt = 0; attempt < 20; attempt++)
        {
            bool left = false;
            foreach (var f in Directory.GetFiles(dir, name + "*.old"))
            {
                try { File.Delete(f); } catch { left = true; }
            }
            if (!left) return;
            Thread.Sleep(500);
        }
    }

    // ------------------------------------------------------------------ HTTP
    static HttpWebRequest Request(string url, string accept)
    {
        try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12 | (SecurityProtocolType)12288; } // TLS 1.2 + 1.3
        catch { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }
        var r = (HttpWebRequest)WebRequest.Create(url);
        r.UserAgent = "MiniStats-Updater (+" + Program.RepoUrl + ")";
        r.Accept = accept;
        r.Timeout = 20000;
        r.ReadWriteTimeout = 60000;
        r.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
        return r;
    }

    static string DefaultGetText(string url, string accept)
    {
        using (var resp = Request(url, accept).GetResponse())
        using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            return sr.ReadToEnd();
    }

    void Download(string url, string path, long expectedSize)
    {
        using (var resp = (HttpWebResponse)Request(url, "application/octet-stream").GetResponse())
        using (var src = resp.GetResponseStream())
        using (var dst = File.Create(path))
        {
            long total = resp.ContentLength > 0 ? resp.ContentLength : expectedSize, done = 0;
            var buf = new byte[64 * 1024];
            int n, lastReported = -1;
            while ((n = src.Read(buf, 0, buf.Length)) > 0)
            {
                dst.Write(buf, 0, n);
                done += n;
                int pct = total > 0 ? (int)Math.Min(100, done * 100 / total) : 0;
                if (pct >= lastReported + 4 || pct == 100) { Progress = pct; lastReported = pct; Raise(); }
            }
        }
    }

    static string Sha256(string path)
    {
        using (var sha = SHA256.Create())
        using (var fs = File.OpenRead(path))
            return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
    }

    static string Friendly(Exception ex)
    {
        var web = ex as WebException;
        if (web != null)
        {
            var resp = web.Response as HttpWebResponse;
            if (resp != null && (int)resp.StatusCode == 403) return "GitHub is rate-limiting update checks. Try again in a while.";
            if (resp != null && (int)resp.StatusCode == 404) return "No published release was found.";
            return "Couldn't reach GitHub. Check your internet connection.";
        }
        if (ex is UnauthorizedAccessException)
            return "MiniStats can't write to its own folder. Restart it as administrator or move it to a folder you own.";
        if (ex is IOException && !(ex is InvalidDataException))
            return "Couldn't replace MiniStats.exe: " + ex.Message;
        return ex.Message;
    }

    // ------------------------------------------------------------------ release notes as plain text
    public static string PlainNotes(string markdown, int maxLines)
    {
        var lines = new List<string>();
        foreach (var raw in (markdown ?? "").Replace("\r", "").Split('\n'))
        {
            var l = raw.Trim();
            if (l.Length == 0 || l.StartsWith("<!--") || l.StartsWith("**Full Changelog**")) continue;
            l = System.Text.RegularExpressions.Regex.Replace(l, @"\[([^\]]+)\]\([^)]+\)", "$1"); // [text](url) -> text
            l = l.Replace("**", "").Replace("`", "");
            if (l.StartsWith("#")) l = l.TrimStart('#').Trim();
            else if (l.StartsWith("- ") || l.StartsWith("* ")) l = "•  " + l.Substring(2);
            lines.Add(l);
            if (lines.Count == maxLines) break;
        }
        return string.Join("\n", lines);
    }
}
