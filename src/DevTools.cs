// Developer switches that exercise the settings window without a running overlay:
//   MiniStats.exe --shot-settings <dir>   renders every settings page to <dir>\settings-<n>.png (README images)
//   MiniStats.exe --selftest [report]     drives the settings window against a fake host and checks the results
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

class FakeHost : ISettingsHost
{
    public Config Saved = new Config(null);
    public Config LastPreview;
    public int Commits, StartupCalls;
    public bool Startup = true;
    public bool Elevated = true;
    public Snapshot Last { get { return Snapshot.Sample(); } }
    public string FpsError { get { return Elevated ? null : "access denied"; } }
    public bool HasGpu { get { return true; } }
    public bool IsElevated { get { return Elevated; } }
    public bool IsMovable { get { return false; } }
    public bool StartupEnabled { get { return Startup; } }
    public Config CurrentConfig { get { return Saved.Clone(); } }
    public string ConfigPath { get { return @"C:\Tools\MiniStats\MiniStats.ini"; } }
    public event EventHandler Sampled { add { } remove { } }
    public event EventHandler MoveModeChanged { add { } remove { } }
    public void Preview(Config c) { LastPreview = c.Clone(); }
    public void Commit(Config c) { Saved = c.Clone(); LastPreview = c.Clone(); Commits++; }
    public System.Drawing.Point SetMovable(bool on, bool persist) { return System.Drawing.Point.Empty; }
    public string SetStartup(bool enabled) { StartupCalls++; Startup = enabled; return null; }
    public void RestartElevated() { }
}

partial class SettingsWindow
{
    static void Pump()
    {
        for (int i = 0; i < 4; i++)
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(delegate { }));
    }

    static SettingsWindow Offscreen(ISettingsHost host)
    {
        var w = new SettingsWindow(host);
        w.Win.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Win.Left = -20000; w.Win.Top = -20000;
        w.Win.ShowActivated = false;
        w.Win.ShowInTaskbar = false;
        w.Win.Show();
        Pump();
        return w;
    }

    static void Click(Button b) { b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump(); }

    // ---------------------------------------------------------------- screenshots
    public static int Shots(string dir, double scale)
    {
        Directory.CreateDirectory(dir);
        var w = Offscreen(new FakeHost());
        if (scale <= 0) scale = w.DpiScale(); // same scale the live preview was rendered at -> pixel-exact
        var root = (FrameworkElement)w.Win.Content;
        var scroller = FindAll<ScrollViewer>(root)[0];
        double baseHeight = w.Win.Height;
        for (int tab = 0; tab < 4; tab++)
        {
            w.SelectTab(tab);
            w.Win.Height = baseHeight;
            Pump();
            root.UpdateLayout();
            // grow the window so the whole page is visible in the screenshot (no scrolling)
            double extra = scroller.ExtentHeight - scroller.ViewportHeight;
            if (extra > 0) { w.Win.Height = baseHeight + extra + 4; Pump(); root.UpdateLayout(); }
            var rtb = new RenderTargetBitmap((int)Math.Round(root.ActualWidth * scale), (int)Math.Round(root.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            rtb.Render(root);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = File.Create(Path.Combine(dir, "settings-" + tab + ".png"))) enc.Save(fs);
        }
        w.committed = true;
        w.Win.Close();
        return 0;
    }

    // ---------------------------------------------------------------- self-test
    public static int SelfTest(string reportPath)
    {
        var log = new StringBuilder();
        int failed = 0;
        Action<string, bool> check = delegate (string name, bool ok) { log.AppendLine((ok ? "PASS  " : "FAIL  ") + name); if (!ok) failed++; };

        try
        {
            var host = new FakeHost();
            var f = Offscreen(host);
            check("window loads with every named element", f.Win.IsLoaded);
            check("Apply is disabled when nothing changed", !f.ApplyBtn.IsEnabled);
            check("Midnight tile is selected for the default palette", f.themeTiles["Midnight"].IsChecked == true);
            check("Custom tile is hidden for a preset palette", f.themeTiles["Custom"].Visibility != Visibility.Visible);
            check("preview image is rendered", f.PreviewImage.Source != null && f.PreviewImage.Width > 50);

            f.SelectTab(1); Pump();
            check("navigation switches page and title", f.pages[1].Visibility == Visibility.Visible && f.PageTitle.Text == "Appearance");

            f.themeTiles["Daylight"].IsChecked = true; Pump();
            check("Daylight tile previews its background", host.LastPreview != null && host.LastPreview.Back.ToArgb() == System.Drawing.ColorTranslator.FromHtml("#F6F7F9").ToArgb());
            check("Daylight tile previews its CPU colour", host.LastPreview.ColorOf("cpu").ToArgb() == System.Drawing.ColorTranslator.FromHtml("#1F6FEB").ToArgb());
            check("Apply and the unsaved hint appear after a change", f.ApplyBtn.IsEnabled && f.DirtyHint.Visibility == Visibility.Visible);

            f.colorKey = "fps"; f.SetColor("fps", System.Drawing.Color.FromArgb(0x12, 0x34, 0x56)); Pump();
            check("custom colour previews", host.LastPreview.ColorOf("fps").ToArgb() == System.Drawing.Color.FromArgb(0x12, 0x34, 0x56).ToArgb());
            check("custom colour switches the theme to Custom", f.themeTiles["Custom"].IsChecked == true && f.themeTiles["Custom"].Visibility == Visibility.Visible);
            System.Drawing.Color parsed;
            check("hex parser accepts 7FDBFF", TryParseHex("7FDBFF", out parsed) && parsed.B == 0xFF && parsed.R == 0x7F);
            check("hex parser rejects junk", !TryParseHex("#12G", out parsed));

            f.OpacitySlider.Value = 50; Pump();
            check("opacity slider previews 50 %", Math.Abs(host.LastPreview.Opacity - 0.5) < 0.001 && f.OpacityText.Text == "50 %");
            f.RadiusSlider.Value = 0; Pump();
            check("radius 0 previews square corners", host.LastPreview.CornerRadius == 0);
            f.FontSizeSlider.Value = 12.5; Pump();
            check("text size previews", Math.Abs(host.LastPreview.FontSize - 12.5f) < 0.01 && f.FontSizeText.Text == "12.5 pt");
            int consolas = f.FontCombo.Items.IndexOf("Consolas");
            if (consolas >= 0) { f.FontCombo.SelectedIndex = consolas; Pump(); check("font choice previews", host.LastPreview.FontName == "Consolas"); }

            // metrics: switches and ordering
            f.SelectTab(0); Pump();
            CheckBox gpuSwitch = null, tempSwitch = null;
            foreach (var cb in FindAll<CheckBox>(f.MetricRows)) { if ((string)cb.Tag == "gpu") gpuSwitch = cb; if ((string)cb.Tag == "gputemp") tempSwitch = cb; }
            gpuSwitch.IsChecked = false; Pump();
            check("turning GPU off removes it from the bar", !host.LastPreview.Items.Contains("gpu"));
            tempSwitch.IsChecked = true; Pump();
            check("turning GPU temperature on adds it", host.LastPreview.Items.Contains("gputemp"));
            int before = f.order.IndexOf("gputemp");
            f.MoveMetric("gputemp", -1); Pump();
            check("move up reorders the list", f.order.IndexOf("gputemp") == before - 1);
            var firstUp = FindAll<Button>(f.MetricRows)[0];
            check("move up is disabled on the first row", !firstUp.IsEnabled);
            check("bar order follows the list order", string.Join(",", host.LastPreview.Items) == string.Join(",", Enabled(f)));
            f.IntervalSlider.Value = 2000; Pump();
            check("refresh rate previews", host.LastPreview.IntervalMs == 2000 && f.IntervalText.Text == "2 s");

            // position
            f.SelectTab(2); Pump();
            f.CornerTL.IsChecked = true; Pump();
            check("clicking a corner previews that anchor", host.LastPreview.Anchor == "top-left");
            f.MarginSlider.Value = 40; Pump();
            check("edge distance previews", host.LastPreview.Margin == 40 && f.MarginText.Text == "40 px");
            if (f.MonitorCombo.Items.Count > 1)
            {
                f.MonitorCombo.SelectedIndex = 1; Pump();
                check("choosing Display 1 previews monitor index 0", host.LastPreview.Monitor == 0);
            }

            // save flow
            Click(f.ApplyBtn);
            check("Apply commits once", host.Commits == 1);
            check("Apply greys out after saving", !f.ApplyBtn.IsEnabled && f.DirtyHint.Visibility != Visibility.Visible);
            f.themeTiles["Nord"].IsChecked = true; Pump();
            check("a later change re-enables Apply", f.ApplyBtn.IsEnabled);
            Click(f.CancelBtn);
            check("Cancel closes the window", !f.Win.IsVisible);
            check("Cancel restores the last saved look", host.LastPreview.Anchor == "top-left" && host.LastPreview.DetectTheme() == null && host.Commits == 1);

            var g = Offscreen(host);
            check("reopening shows the saved anchor", g.CornerTL.IsChecked == true);
            check("reopening shows the saved order", string.Join(",", host.Saved.Items) == string.Join(",", Enabled(g)));
            g.StartupSwitch.IsChecked = false; Pump();
            check("startup switch calls the host immediately", host.StartupCalls == 1 && !host.Startup);
            Click(g.ResetBtn);
            check("reset previews the defaults", host.LastPreview.DetectTheme() == "Midnight" && host.LastPreview.Anchor == "bottom-right");
            Click(g.SaveBtn);
            check("Save commits and closes", host.Commits == 2 && !g.Win.IsVisible);
            check("defaults were saved", string.Join(",", host.Saved.Items) == "fps,cpu,ram,gpu" && Math.Abs(host.Saved.Opacity - 0.88) < 0.001);

            var limited = new FakeHost { Elevated = false };
            var h = Offscreen(limited);
            h.SelectTab(3); Pump();
            check("not elevated: restart button is shown", h.ElevateBtn.Visibility == Visibility.Visible);
            check("not elevated: startup switch is disabled", !h.StartupSwitch.IsEnabled);
            check("not elevated: FPS badge says Off", h.FpsBadgeText.Text == "Off");
            h.Win.Close();
        }
        catch (Exception ex) { failed++; log.AppendLine("ERROR " + ex); }

        log.AppendLine(failed == 0 ? "ALL PASSED" : failed + " FAILED");
        File.WriteAllText(reportPath, log.ToString());
        return failed == 0 ? 0 : 1;
    }

    static List<string> Enabled(SettingsWindow w)
    {
        var l = new List<string>();
        foreach (var k in w.order) if (w.enabled.Contains(k)) l.Add(k);
        return l;
    }

    static List<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        var list = new List<T>();
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T) list.Add((T)c);
            list.AddRange(FindAll<T>(c));
        }
        return list;
    }
}
