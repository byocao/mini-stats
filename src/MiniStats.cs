// MiniStats - a tiny always-on-top, click-through overlay for Windows: FPS | CPU | RAM | GPU.
//
//   FPS  : ETW present events (Microsoft-Windows-DXGI / D3D9 / DxgKrnl) of the foreground process
//   CPU  : "% Processor Utility" performance counter (same source as Task Manager)
//   RAM  : GlobalMemoryStatusEx
//   GPU  : NVIDIA NVML (nvml.dll ships with the NVIDIA driver)
//
// Targets .NET Framework 4.x (built into Windows 10/11), C# 5. See build.cmd.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;

static class Program
{
    public const string RepoUrl = "https://github.com/byocao/mini-stats";

    [STAThread]
    static int Main(string[] args)
    {
        try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { } // per-monitor v2

        if (args.Length > 0 && args[0] == "--render") return RenderCli.Run(args);
        if (args.Length > 0 && args[0] == "--shot-settings") return SettingsWindow.Shots(args.Length > 1 ? args[1] : ".", args.Length > 2 ? double.Parse(args[2], CultureInfo.InvariantCulture) : 0);
        if (args.Length > 0 && args[0] == "--selftest") return SettingsWindow.SelfTest(args.Length > 1 ? args[1] : Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "selftest.txt"));
        if (args.Length > 0 && args[0] == "--probe") { FpsTracker.Probe(args.Length > 1 ? int.Parse(args[1]) : 5); return 0; }

        // "--settings [tab]" opens the settings window (tab 0-3); launching a second copy does the same
        int settingsTab = -1;
        int si = Array.IndexOf(args, "--settings");
        if (si >= 0) { settingsTab = 0; int t; if (si + 1 < args.Length && int.TryParse(args[si + 1], out t)) settingsTab = t; }

        bool created;
        using (var mtx = new Mutex(true, "Local\\MiniStatsOverlay", out created))
        {
            if (!created)
            {
                // "--replace": started by an instance that is restarting itself (e.g. elevated); wait for it to exit
                if (Array.IndexOf(args, "--replace") < 0)
                {
                    // already running: ask that instance to show its settings window, then quit.
                    // Posted straight to its overlay window: a broadcast would not reach an elevated instance (UIPI).
                    foreach (var h in FindOverlayWindows())
                        Native.PostMessage(h, ShowSettingsMessage, new IntPtr(Math.Max(0, settingsTab) + 1), IntPtr.Zero);
                    return 0;
                }
                try { if (!mtx.WaitOne(15000)) return 0; }
                catch (AbandonedMutexException) { }
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var overlay = new Overlay();
            if (settingsTab >= 0) overlay.Shown += delegate { overlay.ShowSettings(settingsTab); };
            Application.Run(overlay);
        }
        return 0;
    }

    // Registered window message posted by a second launch: wParam = settings tab + 1.
    public static readonly uint ShowSettingsMessage = Native.RegisterWindowMessage("MiniStats.ShowSettings.v1");

    // top-level "MiniStats" windows that belong to another MiniStats.exe process
    static List<IntPtr> FindOverlayWindows()
    {
        var found = new List<IntPtr>();
        int self = Process.GetCurrentProcess().Id;
        var others = new HashSet<int>();
        foreach (var p in Process.GetProcessesByName("MiniStats")) if (p.Id != self) others.Add(p.Id);
        Native.EnumWindows(delegate (IntPtr h, IntPtr l)
        {
            uint pid;
            Native.GetWindowThreadProcessId(h, out pid);
            if (others.Contains((int)pid))
            {
                var sb = new StringBuilder(64);
                Native.GetWindowText(h, sb, sb.Capacity);
                if (sb.ToString() == "MiniStats") found.Add(h);
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static bool IsElevated()
    {
        try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
        catch { return false; }
    }

    public static Icon LoadIcon(Size size)
    {
        try
        {
            using (var s = typeof(Program).Assembly.GetManifestResourceStream("MiniStats.tray.ico"))
                if (s != null) return new Icon(s, size);
        }
        catch { }
        return SystemIcons.Application;
    }
}

// ============================================================================ config
class Config
{
    public string Path;
    public List<string> Items = new List<string> { "fps", "cpu", "ram", "gpu" };
    public int IntervalMs = 1000;
    public string FontName = "Segoe UI";
    public float FontSize = 10f;
    public double Opacity = 0.88;
    public int CornerRadius = 8;
    public int Margin = 10;
    public string Anchor = "bottom-right";   // top-left | top-right | bottom-left | bottom-right | custom
    public int Monitor = -1;                 // -1 = primary, otherwise index into Screen.AllScreens
    public int X = int.MinValue, Y = int.MinValue;
    public Color Back = Color.FromArgb(24, 24, 28);
    public Dictionary<string, Color> Colors = new Dictionary<string, Color> {
        { "label", Color.FromArgb(150, 150, 162) },
        { "fps",   Color.FromArgb(255, 222, 102) },
        { "cpu",   Color.FromArgb(124, 196, 255) },
        { "ram",   Color.FromArgb(139, 226, 139) },
        { "gpu",   Color.FromArgb(255, 176, 102) },
        { "vram",  Color.FromArgb(255, 150, 200) },
        { "temp",  Color.FromArgb(255, 128, 110) } };

    public static readonly string[] Anchors = { "top-left", "top-right", "bottom-left", "bottom-right", "custom" };
    public static readonly string[] AllItems = { "fps", "cpu", "ram", "ramgb", "gpu", "vram", "gputemp" };

    // ---- colour themes: background, label, fps, cpu, ram, gpu, vram, temp
    public static readonly string[] ThemeNames = { "Midnight", "Daylight", "Nord", "Mono" };
    static readonly string[][] ThemeColors = {
        new[] { "#18181C", "#9696A2", "#FFDE66", "#7CC4FF", "#8BE28B", "#FFB066", "#FF96C8", "#FF806E" },
        new[] { "#F6F7F9", "#6E7781", "#B7791F", "#1F6FEB", "#1A7F37", "#D9480F", "#BF3989", "#CF222E" },
        new[] { "#2E3440", "#81A1C1", "#EBCB8B", "#88C0D0", "#A3BE8C", "#D08770", "#B48EAD", "#BF616A" },
        new[] { "#000000", "#8A8A8A", "#FFFFFF", "#FFFFFF", "#FFFFFF", "#FFFFFF", "#FFFFFF", "#FFFFFF" } };
    public static readonly string[] ColorKeys = { "label", "fps", "cpu", "ram", "gpu", "vram", "temp" };

    public void ApplyTheme(string name)
    {
        int i = Array.IndexOf(ThemeNames, name);
        if (i < 0) return;
        Back = ColorTranslator.FromHtml(ThemeColors[i][0]);
        for (int k = 0; k < ColorKeys.Length; k++) Colors[ColorKeys[k]] = ColorTranslator.FromHtml(ThemeColors[i][k + 1]);
    }

    // name of the theme whose colours match exactly, or null for a custom palette
    public string DetectTheme()
    {
        for (int i = 0; i < ThemeNames.Length; i++)
        {
            bool same = Back.ToArgb() == ColorTranslator.FromHtml(ThemeColors[i][0]).ToArgb();
            for (int k = 0; same && k < ColorKeys.Length; k++)
                same = ColorOf(ColorKeys[k]).ToArgb() == ColorTranslator.FromHtml(ThemeColors[i][k + 1]).ToArgb();
            if (same) return ThemeNames[i];
        }
        return null;
    }

    // the display selected by Monitor (falls back to the primary display if it is gone)
    public Screen TargetScreen()
    {
        var all = Screen.AllScreens;
        return Monitor >= 0 && Monitor < all.Length ? all[Monitor] : Screen.PrimaryScreen;
    }

    public Config Clone()
    {
        var c = (Config)MemberwiseClone();
        c.Items = new List<string>(Items);
        c.Colors = new Dictionary<string, Color>(Colors);
        return c;
    }

    public Config(string path)
    {
        Path = path;
        if (path == null) return;
        if (!File.Exists(path)) { try { Save(); } catch { } return; }
        bool anchorSeen = false;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            string k = line.Substring(0, eq).Trim().ToLowerInvariant(), v = line.Substring(eq + 1).Trim();
            try
            {
                switch (k)
                {
                    case "items":
                        Items = new List<string>();
                        foreach (var s in v.Split(',')) if (s.Trim().Length > 0) Items.Add(s.Trim().ToLowerInvariant());
                        break;
                    case "interval_ms": IntervalMs = Math.Max(250, int.Parse(v)); break;
                    case "font": FontName = v; break;
                    case "font_size": FontSize = Math.Max(6f, float.Parse(v, CultureInfo.InvariantCulture)); break;
                    case "opacity": Opacity = double.Parse(v, CultureInfo.InvariantCulture); break;
                    case "corner_radius": CornerRadius = Math.Max(0, int.Parse(v)); break;
                    case "margin": Margin = int.Parse(v); break;
                    case "anchor": if (Array.IndexOf(Anchors, v.ToLowerInvariant()) >= 0) { Anchor = v.ToLowerInvariant(); anchorSeen = true; } break;
                    case "monitor": Monitor = v.Length == 0 || v.ToLowerInvariant() == "primary" ? -1 : Math.Max(-1, int.Parse(v) - 1); break;
                    case "x": if (v.Length > 0) X = int.Parse(v); break;
                    case "y": if (v.Length > 0) Y = int.Parse(v); break;
                    case "background": Back = ColorTranslator.FromHtml(v); break;
                    default: if (k.StartsWith("color_")) Colors[k.Substring(6)] = ColorTranslator.FromHtml(v); break;
                }
            }
            catch { }
        }
        // v1.0 files had no anchor: an explicit x/y meant "where I dragged it"
        if (!anchorSeen && X != int.MinValue && Y != int.MinValue) Anchor = "custom";
    }

    public void Save() { File.WriteAllText(Path, Serialize()); }

    public string Serialize()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# MiniStats configuration - easiest to edit from the tray icon > Settings.");
        sb.AppendLine("# Manual edits are applied automatically when this file is saved.");
        sb.AppendLine("#");
        sb.AppendLine("# items: comma separated, shown in this order.");
        sb.AppendLine("#   fps, cpu, ram, ramgb, gpu, vram, gputemp");
        sb.AppendLine("items=" + string.Join(",", Items));
        sb.AppendLine("interval_ms=" + IntervalMs);
        sb.AppendLine();
        sb.AppendLine("# appearance");
        sb.AppendLine("font=" + FontName);
        sb.AppendLine("font_size=" + FontSize.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine("opacity=" + Opacity.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine("corner_radius=" + CornerRadius);
        sb.AppendLine("background=" + Hex(Back));
        foreach (var kv in Colors) sb.AppendLine("color_" + kv.Key + "=" + Hex(kv.Value));
        sb.AppendLine();
        sb.AppendLine("# position");
        sb.AppendLine("#   anchor : top-left | top-right | bottom-left | bottom-right | custom (uses x/y)");
        sb.AppendLine("#   monitor: primary, or 1, 2, ... (Windows display order)");
        sb.AppendLine("anchor=" + Anchor);
        sb.AppendLine("monitor=" + (Monitor < 0 ? "primary" : (Monitor + 1).ToString()));
        sb.AppendLine("margin=" + Margin);
        sb.AppendLine("x=" + (X == int.MinValue ? "" : X.ToString()));
        sb.AppendLine("y=" + (Y == int.MinValue ? "" : Y.ToString()));
        return sb.ToString();
    }

    static string Hex(Color c) { return string.Format("#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B); }

    public Color ColorOf(string key)
    {
        Color c;
        return Colors.TryGetValue(key, out c) ? c : Color.White;
    }
}

// ============================================================================ metrics -> display parts
class Part
{
    public string ColorKey, Label, Value, Template;
    public Part(string colorKey, string label, string value)
    {
        ColorKey = colorKey; Label = label; Value = value;
        // same text with every digit replaced by '8': reserves a stable width so the bar doesn't jitter
        var t = new StringBuilder(value);
        for (int i = 0; i < t.Length; i++) if (char.IsDigit(t[i])) t[i] = '8';
        Template = t.ToString();
    }
}

class Snapshot
{
    public double Fps = -1; public bool FpsUnavailable;
    public double Cpu = -1;
    public int RamLoad = -1; public double RamUsedGb, RamTotalGb;
    public int Gpu = -1; public double VramGb = -1; public int GpuTemp = -1;

    // representative values, used for screenshots and as a placeholder before the first sample
    public static Snapshot Sample()
    {
        return new Snapshot { Fps = 144, Cpu = 23, RamLoad = 61, RamUsedGb = 19.6, RamTotalGb = 32, Gpu = 87, VramGb = 6.4, GpuTemp = 64 };
    }

    public List<Part> ToParts(List<string> items)
    {
        var parts = new List<Part>();
        foreach (var item in items)
        {
            switch (item)
            {
                case "fps": parts.Add(new Part("fps", "FPS", FpsUnavailable ? "n/a" : Fps < 0 ? "--" : Math.Round(Fps).ToString())); break;
                case "cpu": parts.Add(new Part("cpu", "CPU", Cpu < 0 ? "--" : Math.Min(100, Math.Round(Cpu)) + "%")); break;
                case "ram": parts.Add(new Part("ram", "RAM", RamLoad < 0 ? "--" : RamLoad + "%")); break;
                case "ramgb": parts.Add(new Part("ram", "RAM", RamLoad < 0 ? "--" : string.Format(CultureInfo.InvariantCulture, "{0:0.0}/{1:0} GB", RamUsedGb, RamTotalGb))); break;
                case "gpu": parts.Add(new Part("gpu", "GPU", Gpu < 0 ? "--" : Gpu + "%")); break;
                case "vram": parts.Add(new Part("vram", "VRAM", VramGb < 0 ? "--" : string.Format(CultureInfo.InvariantCulture, "{0:0.0} GB", VramGb))); break;
                case "gputemp": parts.Add(new Part("temp", "TEMP", GpuTemp < 0 ? "--" : GpuTemp + "°C")); break;
            }
        }
        return parts;
    }
}

// ============================================================================ drawing (shared by overlay and --render)
class Painter : IDisposable
{
    public const int PadX = 11, PadY = 5, LabelGap = 4, ItemGap = 12;
    public Font LabelFont, ValueFont;
    float pxPerPt;

    public Painter(Config cfg, float pixelsPerPoint)
    {
        pxPerPt = pixelsPerPoint;
        ValueFont = new Font(cfg.FontName, cfg.FontSize * pxPerPt, FontStyle.Bold, GraphicsUnit.Pixel);
        LabelFont = new Font(cfg.FontName, cfg.FontSize * 0.78f * pxPerPt, FontStyle.Regular, GraphicsUnit.Pixel);
    }

    int S(int v) { return (int)Math.Round(v * pxPerPt / (96f / 72f)); } // scale a 96-dpi pixel constant

    static Size M(string s, Font f) { return TextRenderer.MeasureText(s, f, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine); }

    public Size Measure(List<Part> parts)
    {
        int w = S(PadX), h = 0;
        for (int i = 0; i < parts.Count; i++)
        {
            var p = parts[i];
            var l = M(p.Label, LabelFont);
            var v = M(p.Template, ValueFont);
            var va = M(p.Value, ValueFont);
            w += l.Width + S(LabelGap) + Math.Max(v.Width, va.Width);
            if (i < parts.Count - 1) w += S(ItemGap);
            h = Math.Max(h, Math.Max(l.Height, v.Height));
        }
        return new Size(w + S(PadX), h + 2 * S(PadY));
    }

    public void Draw(Graphics g, Size size, List<Part> parts, Config cfg, bool highlightBorder)
    {
        int x = S(PadX);
        Color labelColor = cfg.ColorOf("label");
        for (int i = 0; i < parts.Count; i++)
        {
            var p = parts[i];
            var l = M(p.Label, LabelFont);
            var v = M(p.Template, ValueFont);
            var va = M(p.Value, ValueFont);
            // align label and value on a common baseline-ish centre line
            TextRenderer.DrawText(g, p.Label, LabelFont, new Point(x, (size.Height - l.Height) / 2 + S(1)), labelColor, cfg.Back, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            x += l.Width + S(LabelGap);
            TextRenderer.DrawText(g, p.Value, ValueFont, new Point(x, (size.Height - va.Height) / 2), cfg.ColorOf(p.ColorKey), cfg.Back, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            x += Math.Max(v.Width, va.Width) + S(ItemGap);
        }
        if (highlightBorder)
            using (var pen = new Pen(cfg.ColorOf("fps"), Math.Max(1, S(2))))
                g.DrawRectangle(pen, 0, 0, size.Width - 1, size.Height - 1);
    }

    public void Dispose() { LabelFont.Dispose(); ValueFont.Dispose(); }
}

// ============================================================================ overlay window
class Overlay : Form, ISettingsHost
{
    Config cfg;
    DateTime cfgStamp;
    NotifyIcon tray;
    ToolStripMenuItem miMove;
    Painter painter;
    int painterDpi;
    bool movable;
    Point dragStart;
    List<Part> parts = new List<Part>();
    SettingsWindow settings;

    PerformanceCounter cpuCounter;
    Nvml nvml;
    FpsTracker fps;

    // Sampling runs on its own thread so a slow driver / counter / other process can never freeze the UI thread
    // (Windows kills windows that stop answering messages). The UI thread only lays out and paints.
    Thread sampler;
    readonly ManualResetEvent stopSampler = new ManualResetEvent(false);
    volatile bool stopping;
    volatile int intervalMs = 1000;
    volatile Snapshot last;
    int uiPending; // 1 while a UI update is queued, so updates never pile up behind a busy UI thread

    public Snapshot Last { get { return last; } }
    public string FpsError { get { return fps.Error; } }
    public bool HasGpu { get { return nvml != null; } }
    public bool IsMovable { get { return movable; } }
    public bool IsElevated { get { return Program.IsElevated(); } }
    public bool StartupEnabled { get { return Startup.IsEnabled(); } }
    public string SetStartup(bool enabled) { return enabled ? Startup.Enable(Application.ExecutablePath) : Startup.Disable(); }
    public Config CurrentConfig { get { return cfg.Clone(); } }
    public event EventHandler Sampled;
    public event EventHandler MoveModeChanged;

    public Overlay()
    {
        string dir = System.IO.Path.GetDirectoryName(Application.ExecutablePath);
        cfg = new Config(System.IO.Path.Combine(dir, "MiniStats.ini"));
        cfgStamp = Stamp();

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.None;
        Text = "MiniStats";

        try { cpuCounter = new PerformanceCounter("Processor Information", "% Processor Utility", "_Total"); cpuCounter.NextValue(); }
        catch
        {
            try { cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total"); cpuCounter.NextValue(); }
            catch { cpuCounter = null; }
        }
        nvml = Nvml.TryCreate();
        fps = new FpsTracker();
        fps.Start();

        BuildTray();
        ApplyConfig();
        Relayout(true); // placeholder values until the first sample arrives
    }

    // ---- window styles: tool window, never activates, layered, click-through, topmost
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80 | 0x08000000 | 0x80000 | 0x20 | 0x8;
            return cp;
        }
    }
    protected override bool ShowWithoutActivation { get { return true; } }

    void SetClickThrough(bool through)
    {
        int ex = Native.GetWindowLong(Handle, -20);
        ex = through ? (ex | 0x20) : (ex & ~0x20);
        Native.SetWindowLong(Handle, -20, ex);
    }

    // ---- config
    DateTime Stamp() { try { return File.GetLastWriteTimeUtc(cfg.Path); } catch { return DateTime.MinValue; } }

    void ApplyConfig()
    {
        BackColor = cfg.Back;
        Opacity = Math.Max(0.2, Math.Min(1.0, cfg.Opacity));
        intervalMs = cfg.IntervalMs;
        if (painter != null) { painter.Dispose(); painter = null; }
    }

    // Show a configuration without saving it (live preview from the settings window).
    public void Preview(Config c)
    {
        string path = cfg.Path;
        cfg = c.Clone();
        cfg.Path = path;
        ApplyConfig();
        Relayout(true);
    }

    // Show and persist a configuration.
    public void Commit(Config c)
    {
        Preview(c);
        SaveConfig();
    }

    public string ConfigPath { get { return cfg.Path; } }

    Painter GetPainter()
    {
        int dpi = 96;
        try { if (IsHandleCreated) dpi = (int)Native.GetDpiForWindow(Handle); } catch { }
        if (dpi <= 0) dpi = 96;
        if (painter == null || painterDpi != dpi)
        {
            if (painter != null) painter.Dispose();
            painter = new Painter(cfg, dpi / 72f);
            painterDpi = dpi;
        }
        return painter;
    }

    // ---- tray
    void BuildTray()
    {
        var menu = new ContextMenuStrip();
        var miSettings = new ToolStripMenuItem("Settings...", null, delegate { ShowSettings(); });
        miSettings.Font = new Font(miSettings.Font, FontStyle.Bold);
        menu.Items.Add(miSettings);
        miMove = new ToolStripMenuItem("Unlock to move", null, delegate { SetMovable(!movable, true); });
        menu.Items.Add(miMove);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, delegate { Close(); });

        tray = new NotifyIcon();
        tray.Icon = Program.LoadIcon(SystemInformation.SmallIconSize);
        tray.Text = "MiniStats";
        tray.ContextMenuStrip = menu;
        tray.Visible = true;
        tray.DoubleClick += delegate { ShowSettings(); };
    }

    public void ShowSettings() { ShowSettings(-1); }

    public void ShowSettings(int tab)
    {
        if (settings == null)
        {
            settings = new SettingsWindow(this);
            settings.Closed += delegate
            {
                settings = null;
                // give back what the settings window used; the overlay itself needs very little
                BeginInvoke((Action)delegate
                {
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                    try { Native.SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, new IntPtr(-1), new IntPtr(-1)); } catch { }
                });
            };
            settings.Show();
        }
        if (tab >= 0) settings.SelectTab(tab);
        settings.Activate();
    }

    // a second launch broadcasts ShowSettingsMessage; allow it through UIPI even when we run elevated
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { Native.ChangeWindowMessageFilterEx(Handle, Program.ShowSettingsMessage, 1, IntPtr.Zero); } catch { }
        StartSampler(); // needs the handle for BeginInvoke
    }

    protected override void WndProc(ref Message m)
    {
        if (Program.ShowSettingsMessage != 0 && m.Msg == (int)Program.ShowSettingsMessage)
        {
            int tab = (int)m.WParam - 1;
            BeginInvoke((Action)delegate { ShowSettings(tab); });
            return;
        }
        base.WndProc(ref m);
    }

    // Unlocked = draggable with a highlighted border. When locking, the spot becomes the "custom" position.
    // persist=true saves straight to the config file (tray menu); the settings window passes false and saves on OK.
    public Point SetMovable(bool on, bool persist)
    {
        if (movable == on) return Location;
        movable = on;
        miMove.Text = movable ? "Lock position" : "Unlock to move";
        SetClickThrough(!movable);
        if (!movable)
        {
            cfg.Anchor = "custom"; cfg.X = Left; cfg.Y = Top;
            if (persist) SaveConfig();
        }
        Invalidate();
        if (MoveModeChanged != null) MoveModeChanged(this, EventArgs.Empty);
        return Location;
    }

    // Relaunch elevated (FPS needs it). The new instance waits for this one to exit.
    public void RestartElevated()
    {
        try
        {
            var psi = new ProcessStartInfo(Application.ExecutablePath, "--replace");
            psi.UseShellExecute = true;
            psi.Verb = "runas";
            psi.WorkingDirectory = System.IO.Path.GetDirectoryName(Application.ExecutablePath);
            Process.Start(psi);
            Close();
        }
        catch (System.ComponentModel.Win32Exception) { } // UAC prompt cancelled
    }

    void SaveConfig() { try { cfg.Save(); cfgStamp = Stamp(); } catch { } }

    // ---- dragging (only while unlocked)
    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (movable && e.Button == MouseButtons.Left) dragStart = e.Location;
        base.OnMouseDown(e);
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (movable && e.Button == MouseButtons.Left) Location = new Point(Left + e.X - dragStart.X, Top + e.Y - dragStart.Y);
        base.OnMouseMove(e);
    }

    // ---- sampling (background thread)
    void StartSampler()
    {
        if (sampler != null) return;
        sampler = new Thread(SamplerLoop) { IsBackground = true, Name = "Sampler" };
        sampler.Start();
    }

    void SamplerLoop()
    {
        while (!stopping)
        {
            try { last = TakeSample(); } catch { }
            if (Interlocked.CompareExchange(ref uiPending, 1, 0) == 0)
            {
                try { BeginInvoke((Action)OnSampledUi); }
                catch { uiPending = 0; } // handle gone: we are shutting down
            }
            stopSampler.WaitOne(intervalMs);
        }
    }

    Snapshot TakeSample()
    {
        var s = new Snapshot();
        s.Fps = fps.ForegroundFps();
        s.FpsUnavailable = fps.Error != null;
        if (cpuCounter != null) try { s.Cpu = cpuCounter.NextValue(); } catch { }
        var mem = new Native.MEMORYSTATUSEX();
        mem.dwLength = (uint)Marshal.SizeOf(typeof(Native.MEMORYSTATUSEX));
        if (Native.GlobalMemoryStatusEx(ref mem))
        {
            s.RamLoad = (int)mem.dwMemoryLoad;
            s.RamTotalGb = mem.ullTotalPhys / 1073741824.0;
            s.RamUsedGb = (mem.ullTotalPhys - mem.ullAvailPhys) / 1073741824.0;
        }
        if (nvml != null)
        {
            var g = nvml.Read();
            s.Gpu = g.Util; s.VramGb = g.MemUsed / 1073741824.0; s.GpuTemp = g.Temp;
        }
        return s;
    }

    // ---- UI update for a new sample (UI thread; must stay cheap and never block)
    void OnSampledUi()
    {
        uiPending = 0;
        if (stopping || IsDisposed) return;

        var st = Stamp();
        if (st != cfgStamp && settings == null) { cfgStamp = st; cfg = new Config(cfg.Path); ApplyConfig(); }

        var s = last;
        string tip = s != null && s.FpsUnavailable ? "MiniStats - run as administrator to enable FPS" : "MiniStats";
        if (tray.Text != tip) tray.Text = tip; // only talk to Explorer when something changed

        Relayout(false);
        // re-assert topmost (games and launchers sometimes push themselves above other topmost windows);
        // SWP_ASYNCWINDOWPOS so this call never waits on anybody
        Native.SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0200 | 0x4000);
        if (Sampled != null) Sampled(this, EventArgs.Empty);
    }

    void Relayout(bool force)
    {
        parts = (Last ?? Snapshot.Sample()).ToParts(cfg.Items);
        Visible = parts.Count > 0;
        var size = GetPainter().Measure(parts);
        if (force || size != Size)
        {
            Size = size;
            UpdateShape();
            Reposition();
        }
        Invalidate();
    }

    void UpdateShape()
    {
        int r = (int)Math.Round(cfg.CornerRadius * (painterDpi > 0 ? painterDpi : 96) / 96.0);
        if (r <= 0) { Region = null; return; }
        IntPtr rgn = Native.CreateRoundRectRgn(0, 0, Width + 1, Height + 1, r * 2, r * 2);
        Region = Region.FromHrgn(rgn);
        Native.DeleteObject(rgn);
    }

    void Reposition()
    {
        if (movable) return;
        if (cfg.Anchor == "custom" && cfg.X != int.MinValue && cfg.Y != int.MinValue) { Location = new Point(cfg.X, cfg.Y); return; }
        var wa = cfg.TargetScreen().WorkingArea;
        int m = (int)Math.Round(cfg.Margin * (painterDpi > 0 ? painterDpi : 96) / 96.0);
        bool left = cfg.Anchor == "top-left" || cfg.Anchor == "bottom-left";
        bool top = cfg.Anchor == "top-left" || cfg.Anchor == "top-right";
        Location = new Point(left ? wa.Left + m : wa.Right - Width - m, top ? wa.Top + m : wa.Bottom - Height - m);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        GetPainter().Draw(e.Graphics, ClientSize, parts, cfg, movable);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (settings != null) settings.Close();
        stopping = true;
        stopSampler.Set();
        bool samplerDone = sampler == null || sampler.Join(2000);
        fps.Stop();
        if (nvml != null && samplerDone) nvml.Dispose(); // never pull NVML out from under a stuck sampler
        tray.Visible = false;
        tray.Dispose();
        base.OnFormClosing(e);
    }
}

// ============================================================================ "Start with Windows" (scheduled task, elevated)
static class Startup
{
    const string TaskName = "MiniStats";

    public static bool IsEnabled() { return Schtasks("/Query /TN " + TaskName) == 0; }

    public static string Enable(string exePath)
    {
        string user = WindowsIdentity.GetCurrent().Name;
        string xml =
            "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
            "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
            "  <RegistrationInfo><Description>MiniStats overlay - " + Program.RepoUrl + "</Description></RegistrationInfo>\r\n" +
            "  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + SecurityElement.Escape(user) + "</UserId><Delay>PT5S</Delay></LogonTrigger></Triggers>\r\n" +
            "  <Principals><Principal id=\"Author\"><UserId>" + SecurityElement.Escape(user) + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\r\n" +
            "  <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>" +
            "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>7</Priority>" +
            "<RestartOnFailure><Interval>PT1M</Interval><Count>5</Count></RestartOnFailure></Settings>\r\n" + // relaunch if it ever gets killed

            "  <Actions Context=\"Author\"><Exec><Command>" + SecurityElement.Escape(exePath) + "</Command><WorkingDirectory>" +
            SecurityElement.Escape(Path.GetDirectoryName(exePath)) + "</WorkingDirectory></Exec></Actions>\r\n" +
            "</Task>\r\n";
        string tmp = Path.Combine(Path.GetTempPath(), "MiniStats.task.xml");
        File.WriteAllText(tmp, xml, Encoding.Unicode);
        int rc = Schtasks("/Create /TN " + TaskName + " /XML \"" + tmp + "\" /F");
        try { File.Delete(tmp); } catch { }
        return rc == 0 ? null : "Could not create the startup task (schtasks exit code " + rc + ").";
    }

    public static string Disable()
    {
        int rc = Schtasks("/Delete /TN " + TaskName + " /F");
        return rc == 0 ? null : "Could not remove the startup task (schtasks exit code " + rc + ").";
    }

    static int Schtasks(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", args);
            psi.CreateNoWindow = true; psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
            using (var p = Process.Start(psi)) { p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit(); return p.ExitCode; }
        }
        catch { return -1; }
    }
}

// ============================================================================ FPS via ETW present events
class FpsTracker
{
    const string SessionName = "MiniStatsFps";
    static readonly Guid DXGI = new Guid("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    static readonly Guid D3D9 = new Guid("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    static readonly Guid DXGKRNL = new Guid("802EC45A-1E99-4B83-9920-87C98277BA9D");
    const int DxgiPresentStart = 42, DxgiPresentMpoStart = 55, D3D9PresentStart = 1, DxgKrnlPresentInfo = 184;

    // EVENT_TRACE_PROPERTIES (x64) field offsets
    const int PropsSize = 120, LoggerNameSpace = 1024;

    public string Error;
    ulong session;
    ulong traceHandle;
    IntPtr propsBuf, logfileBuf, loggerNamePtr;
    Native.EventRecordCallback cb;
    Thread thread;

    readonly object lk = new object();
    Dictionary<int, int[]> counts = new Dictionary<int, int[]>(); // pid -> [dxgi, d3d9, dxgkrnl]
    readonly Stopwatch sw = Stopwatch.StartNew();
    Dictionary<int, Dictionary<int, int>> probe; // diagnostics only

    public void Start()
    {
        try
        {
            StopSession(0, SessionName);
            propsBuf = Marshal.AllocHGlobal(PropsSize + LoggerNameSpace);
            InitProps(propsBuf);
            Marshal.WriteInt32(propsBuf, 48, 64);       // BufferSize (KB)
            Marshal.WriteInt32(propsBuf, 52, 4);        // MinimumBuffers
            Marshal.WriteInt32(propsBuf, 56, 16);       // MaximumBuffers
            Marshal.WriteInt32(propsBuf, 64, 0x100);    // LogFileMode = EVENT_TRACE_REAL_TIME_MODE
            Marshal.WriteInt32(propsBuf, 68, 1);        // FlushTimer (s)
            uint r = Native.StartTrace(out session, SessionName, propsBuf);
            if (r != 0) { Error = r == 5 ? "access denied (needs administrator)" : "StartTrace failed: " + r; session = 0; return; }

            Enable(DXGI, new ushort[] { DxgiPresentStart, DxgiPresentMpoStart });
            Enable(D3D9, new ushort[] { D3D9PresentStart });
            Enable(DXGKRNL, new ushort[] { DxgKrnlPresentInfo });

            // EVENT_TRACE_LOGFILEW (x64, 448 bytes)
            cb = OnEvent;
            logfileBuf = Marshal.AllocHGlobal(448);
            for (int i = 0; i < 448; i += 8) Marshal.WriteInt64(logfileBuf, i, 0);
            loggerNamePtr = Marshal.StringToHGlobalUni(SessionName);
            Marshal.WriteIntPtr(logfileBuf, 8, loggerNamePtr);                              // LoggerName
            Marshal.WriteInt32(logfileBuf, 28, 0x100 | 0x10000000);                        // REAL_TIME | EVENT_RECORD
            Marshal.WriteIntPtr(logfileBuf, 424, Marshal.GetFunctionPointerForDelegate(cb)); // EventRecordCallback
            traceHandle = Native.OpenTrace(logfileBuf);
            if (traceHandle == ulong.MaxValue) { Error = "OpenTrace failed: " + Marshal.GetLastWin32Error(); return; }

            thread = new Thread(delegate () { Native.ProcessTrace(new ulong[] { traceHandle }, 1, IntPtr.Zero, IntPtr.Zero); });
            thread.IsBackground = true;
            thread.Name = "ETW";
            thread.Start();
            AppDomain.CurrentDomain.ProcessExit += delegate { Stop(); };
        }
        catch (Exception ex) { Error = ex.Message; }
    }

    static void InitProps(IntPtr p)
    {
        int size = PropsSize + LoggerNameSpace;
        for (int i = 0; i < size; i += 4) Marshal.WriteInt32(p, i, 0);
        Marshal.WriteInt32(p, 0, size);            // Wnode.BufferSize
        Marshal.WriteInt32(p, 40, 1);              // Wnode.ClientContext = QPC timestamps
        Marshal.WriteInt32(p, 44, 0x00020000);     // Wnode.Flags = WNODE_FLAG_TRACED_GUID
        Marshal.WriteInt32(p, 116, PropsSize);     // LoggerNameOffset
    }

    // Enable a provider, filtered in the kernel to the given event ids (cheap: only presents reach us).
    void Enable(Guid provider, ushort[] ids)
    {
        int fsize = 4 + 2 * ids.Length;            // EVENT_FILTER_EVENT_ID
        IntPtr filt = Marshal.AllocHGlobal(fsize);
        Marshal.WriteByte(filt, 0, 1);             // FilterIn
        Marshal.WriteByte(filt, 1, 0);
        Marshal.WriteInt16(filt, 2, (short)ids.Length);
        for (int i = 0; i < ids.Length; i++) Marshal.WriteInt16(filt, 4 + 2 * i, (short)ids[i]);
        IntPtr desc = Marshal.AllocHGlobal(16);    // EVENT_FILTER_DESCRIPTOR
        Marshal.WriteInt64(desc, 0, filt.ToInt64());
        Marshal.WriteInt32(desc, 8, fsize);
        Marshal.WriteInt32(desc, 12, unchecked((int)0x80000200)); // EVENT_FILTER_TYPE_EVENT_ID
        IntPtr par = Marshal.AllocHGlobal(48);     // ENABLE_TRACE_PARAMETERS
        for (int i = 0; i < 48; i += 8) Marshal.WriteInt64(par, i, 0);
        Marshal.WriteInt32(par, 0, 2);             // ENABLE_TRACE_PARAMETERS_VERSION_2
        Marshal.WriteIntPtr(par, 32, desc);
        Marshal.WriteInt32(par, 40, 1);
        Guid g = provider;
        uint r = Native.EnableTraceEx2(session, ref g, 1, 5, 0, 0, 0, par);
        if (r != 0) { g = provider; Native.EnableTraceEx2(session, ref g, 1, 5, 0, 0, 0, IntPtr.Zero); }
        Marshal.FreeHGlobal(par); Marshal.FreeHGlobal(desc); Marshal.FreeHGlobal(filt);
    }

    // EVENT_RECORD: EventHeader.ProcessId @12, ProviderId @24, EventDescriptor.Id @40
    void OnEvent(IntPtr rec)
    {
        int pid = Marshal.ReadInt32(rec, 12);
        Guid prov = ReadGuid(rec, 24);
        int id = (ushort)Marshal.ReadInt16(rec, 40);
        int slot;
        if (prov == DXGI) { if (id != DxgiPresentStart && id != DxgiPresentMpoStart) return; slot = 0; }
        else if (prov == D3D9) { if (id != D3D9PresentStart) return; slot = 1; }
        else if (prov == DXGKRNL) { if (id != DxgKrnlPresentInfo) return; slot = 2; }
        else return;
        lock (lk)
        {
            int[] c;
            if (!counts.TryGetValue(pid, out c)) { c = new int[3]; counts[pid] = c; }
            c[slot]++;
            if (probe != null)
            {
                Dictionary<int, int> d;
                if (!probe.TryGetValue(pid, out d)) { d = new Dictionary<int, int>(); probe[pid] = d; }
                int key = slot * 1000 + id, v;
                d.TryGetValue(key, out v);
                d[key] = v + 1;
            }
        }
    }

    static Guid ReadGuid(IntPtr p, int off)
    {
        var b = new byte[16];
        Marshal.Copy(new IntPtr(p.ToInt64() + off), b, 0, 16);
        return new Guid(b);
    }

    // Presents per second of the process that owns the foreground window; -1 if it isn't presenting.
    public double ForegroundFps()
    {
        if (Error != null) return -1;
        Dictionary<int, int[]> snap;
        double secs;
        lock (lk) { snap = counts; counts = new Dictionary<int, int[]>(); secs = sw.Elapsed.TotalSeconds; sw.Restart(); }
        if (secs <= 0) return -1;
        uint fgPid;
        Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out fgPid);
        int[] c;
        if (!snap.TryGetValue((int)fgPid, out c)) return -1;
        // prefer API-level presents (DXGI, then D3D9); fall back to kernel presents (Vulkan/OpenGL/others)
        int n = c[0] > 0 ? c[0] : (c[1] > 0 ? c[1] : c[2]);
        return n > 0 ? n / secs : -1;
    }

    public void Stop()
    {
        if (session != 0) { Native.ControlTrace(session, null, propsBuf, 1); session = 0; }
        if (traceHandle != 0 && traceHandle != ulong.MaxValue) { Native.CloseTrace(traceHandle); traceHandle = 0; }
    }

    static void StopSession(ulong handle, string name)
    {
        IntPtr p = Marshal.AllocHGlobal(PropsSize + LoggerNameSpace);
        InitProps(p);
        Native.ControlTrace(handle, name, p, 1); // EVENT_TRACE_CONTROL_STOP
        Marshal.FreeHGlobal(p);
    }

    // MiniStats.exe --probe [seconds] : writes probe.txt with present rates per process (diagnostics)
    public static void Probe(int seconds)
    {
        var t = new FpsTracker();
        t.probe = new Dictionary<int, Dictionary<int, int>>();
        t.Start();
        var sb = new StringBuilder();
        if (t.Error != null) sb.AppendLine("ERROR: " + t.Error);
        Thread.Sleep(seconds * 1000);
        uint fgPid;
        Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out fgPid);
        t.Stop();
        sb.AppendLine("foreground: " + fgPid + " " + ProcName((int)fgPid));
        lock (t.lk)
            foreach (var kv in t.probe)
            {
                sb.Append(kv.Key).Append(' ').Append(ProcName(kv.Key)).Append(':');
                foreach (var e in kv.Value)
                {
                    string src = e.Key / 1000 == 0 ? "DXGI" : e.Key / 1000 == 1 ? "D3D9" : "DxgKrnl";
                    sb.AppendFormat(CultureInfo.InvariantCulture, " {0}#{1}={2:0.0}/s", src, e.Key % 1000, e.Value / (double)seconds);
                }
                sb.AppendLine();
            }
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "probe.txt"), sb.ToString());
    }

    static string ProcName(int pid) { try { return Process.GetProcessById(pid).ProcessName; } catch { return "?"; } }
}

// ============================================================================ NVIDIA GPU via NVML
class Nvml : IDisposable
{
    public class Sample { public int Util = -1; public int Temp = -1; public ulong MemUsed; }
    IntPtr dev;

    [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2")] static extern int Init();
    [DllImport("nvml.dll", EntryPoint = "nvmlShutdown")] static extern int Shutdown();
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")] static extern int GetHandle(uint idx, out IntPtr dev);
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetUtilizationRates")] static extern int GetUtil(IntPtr dev, out Util u);
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetTemperature")] static extern int GetTemp(IntPtr dev, int sensor, out uint t);
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetMemoryInfo")] static extern int GetMem(IntPtr dev, out Mem m);
    [StructLayout(LayoutKind.Sequential)] struct Util { public uint gpu, memory; }
    [StructLayout(LayoutKind.Sequential)] struct Mem { public ulong total, free, used; }

    public static Nvml TryCreate()
    {
        try
        {
            if (Init() != 0) return null;
            var n = new Nvml();
            if (GetHandle(0, out n.dev) != 0) { Shutdown(); return null; }
            return n;
        }
        catch { return null; } // no NVIDIA driver -> GPU shows "--"
    }

    public Sample Read()
    {
        var s = new Sample();
        Util u; if (GetUtil(dev, out u) == 0) s.Util = (int)u.gpu;
        uint t; if (GetTemp(dev, 0, out t) == 0) s.Temp = (int)t;
        Mem m; if (GetMem(dev, out m) == 0) s.MemUsed = m.used;
        return s;
    }

    public void Dispose() { try { Shutdown(); } catch { } }
}

// ============================================================================ --render: offscreen PNG (used for README images)
// MiniStats.exe --render <out.png> [--config <file.ini>] [--scale <n>] [--sample k=v,...]
static class RenderCli
{
    public static int Run(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("usage: --render <out.png> [--config file] [--scale n] [--sample fps=144,cpu=23,...]"); return 2; }
        string outPath = args[1], cfgPath = null, sample = "";
        float scale = 1f;
        for (int i = 2; i + 1 < args.Length; i += 2)
        {
            if (args[i] == "--config") cfgPath = args[i + 1];
            else if (args[i] == "--scale") scale = float.Parse(args[i + 1], CultureInfo.InvariantCulture);
            else if (args[i] == "--sample") sample = args[i + 1];
        }
        var cfg = new Config(cfgPath != null && File.Exists(cfgPath) ? cfgPath : null);

        var s = Snapshot.Sample();
        foreach (var kv in sample.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var p = kv.Split('=');
            if (p.Length != 2) continue;
            double v = double.Parse(p[1], CultureInfo.InvariantCulture);
            switch (p[0].Trim())
            {
                case "fps": s.Fps = v; break;
                case "cpu": s.Cpu = v; break;
                case "ram": s.RamLoad = (int)v; break;
                case "ramused": s.RamUsedGb = v; break;
                case "gpu": s.Gpu = (int)v; break;
                case "vram": s.VramGb = v; break;
                case "temp": s.GpuTemp = (int)v; break;
            }
        }
        using (var bmp = RenderBar(cfg, s.ToParts(cfg.Items), scale))
            bmp.Save(outPath, ImageFormat.Png);
        return 0;
    }

    // Renders the bar to a 32-bit bitmap with rounded corners and the configured opacity.
    // scale = 1 for 96 dpi, 1.25 for 120 dpi, ...
    public static Bitmap RenderBar(Config cfg, List<Part> parts, float scale)
    {
        using (var painter = new Painter(cfg, 96f * scale / 72f))
        {
            var size = painter.Measure(parts);
            if (size.Width < 1 || size.Height < 1) size = new Size(1, 1);
            // GDI text must be drawn on an opaque 24-bit surface (on 32-bit ARGB it gets dark fringes),
            // then copied to ARGB and given an anti-aliased rounded alpha mask * opacity.
            var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
            using (var opaque = new Bitmap(size.Width, size.Height, PixelFormat.Format24bppRgb))
            {
                using (var g = Graphics.FromImage(opaque))
                {
                    g.Clear(cfg.Back);
                    painter.Draw(g, size, parts, cfg, false);
                }
                using (var g = Graphics.FromImage(bmp)) g.DrawImageUnscaled(opaque, 0, 0);
            }
            ApplyAlphaMask(bmp, (int)Math.Round(cfg.CornerRadius * scale), Math.Max(0.2, Math.Min(1.0, cfg.Opacity)));
            return bmp;
        }
    }

    // GDI text kills the alpha channel, so draw opaque first and then apply an anti-aliased rounded mask * opacity.
    static void ApplyAlphaMask(Bitmap bmp, int radius, double opacity)
    {
        int w = bmp.Width, h = bmp.Height;
        using (var mask = new Bitmap(w, h, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(mask))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = RoundRect(new RectangleF(0, 0, w, h), radius))
                using (var b = new SolidBrush(Color.White))
                    g.FillPath(b, path);
            }
            var rect = new Rectangle(0, 0, w, h);
            var bd = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            var md = mask.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var px = new byte[bd.Stride * h];
            var mk = new byte[md.Stride * h];
            Marshal.Copy(bd.Scan0, px, 0, px.Length);
            Marshal.Copy(md.Scan0, mk, 0, mk.Length);
            for (int i = 3; i < px.Length; i += 4) px[i] = (byte)(mk[i] * opacity);
            Marshal.Copy(px, 0, bd.Scan0, px.Length);
            bmp.UnlockBits(bd);
            mask.UnlockBits(md);
        }
    }

    public static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        if (radius <= 0) { p.AddRectangle(r); return p; }
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

// ============================================================================ Win32
static class Native
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void EventRecordCallback(IntPtr rec);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "StartTraceW")] public static extern uint StartTrace(out ulong handle, string name, IntPtr props);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "ControlTraceW")] public static extern uint ControlTrace(ulong handle, string name, IntPtr props, uint code);
    [DllImport("advapi32.dll")] public static extern uint EnableTraceEx2(ulong handle, ref Guid provider, uint controlCode, byte level, ulong any, ulong all, uint timeout, IntPtr param);
    [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "OpenTraceW")] public static extern ulong OpenTrace(IntPtr logfile);
    [DllImport("advapi32.dll")] public static extern uint ProcessTrace(ulong[] handles, uint count, IntPtr start, IntPtr end);
    [DllImport("advapi32.dll")] public static extern uint CloseTrace(ulong handle);

    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int idx);
    [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr h, int idx, int v);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr o);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string name);
    public delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc f, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool ChangeWindowMessageFilterEx(IntPtr h, uint msg, uint action, IntPtr info);
    [DllImport("kernel32.dll")] public static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }
    [DllImport("kernel32.dll")] public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
}
