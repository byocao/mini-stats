// MiniStats settings window (WPF). The layout and styles are in SettingsWindow.xaml (embedded resource);
// this file wires the controls to a working copy of the configuration.
// Every change is previewed on the real overlay immediately; Save / Apply persist it, Cancel restores the saved look.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using DColor = System.Drawing.Color;

// What the settings window needs from the running app (implemented by Overlay; dev tools use fakes).
interface ISettingsHost
{
    Snapshot Last { get; }
    string FpsError { get; }
    bool HasGpu { get; }
    bool IsElevated { get; }
    bool IsMovable { get; }
    bool StartupEnabled { get; }
    Config CurrentConfig { get; }
    string ConfigPath { get; }
    event EventHandler Sampled;
    event EventHandler MoveModeChanged;
    void Preview(Config c);
    void Commit(Config c);
    System.Drawing.Point SetMovable(bool on, bool persist);
    string SetStartup(bool enabled);
    void RestartElevated();
}

partial class SettingsWindow
{
    readonly ISettingsHost host;
    Config original, work;
    bool loading, committed;
    List<string> order = new List<string>();
    HashSet<string> enabled = new HashSet<string>();
    string colorKey;

    public readonly Window Win;
    public event EventHandler Closed;

    static readonly string[][] Metrics = {
        new[] { "fps",     "FPS",              "Frames per second of the window in front",  "fps" },
        new[] { "cpu",     "CPU usage",        "Total processor load, same as Task Manager", "cpu" },
        new[] { "ram",     "RAM usage",        "Memory in use, as a percentage",             "ram" },
        new[] { "ramgb",   "RAM used / total", "Memory in use, in GB",                       "ram" },
        new[] { "gpu",     "GPU usage",        "3D load of the NVIDIA graphics card",        "gpu" },
        new[] { "vram",    "VRAM used",        "Video memory in use, in GB",                 "vram" },
        new[] { "gputemp", "GPU temperature",  "Graphics card core temperature",             "temp" } };

    static readonly string[][] ColorNames = {
        new[] { "background", "Background" }, new[] { "label", "Captions" }, new[] { "fps", "FPS" }, new[] { "cpu", "CPU" },
        new[] { "ram", "RAM" }, new[] { "gpu", "GPU" }, new[] { "vram", "VRAM" }, new[] { "temp", "Temperature" } };

    static readonly string[] PaletteColors = {
        "#FFFFFF", "#D9D9E3", "#9696A2", "#5A5A6E", "#2E2E3A", "#18181C", "#000000", "#F6F7F9",
        "#FFDE66", "#FFB066", "#FF806E", "#FF5C7A", "#FF96C8", "#C792EA", "#9D7CFF", "#7C8CFF",
        "#7CC4FF", "#4FD1FF", "#5EEAD4", "#8BE28B", "#3DDC84", "#A3BE8C", "#EBCB8B", "#D08770",
        "#1F6FEB", "#1A7F37", "#B7791F", "#D9480F", "#CF222E", "#BF3989", "#88C0D0", "#2E3440" };

    static readonly string[][] PageText = {
        new[] { "Metrics", "Choose what the bar shows, and in which order." },
        new[] { "Appearance", "Theme, colours, text and shape of the bar." },
        new[] { "Position", "Where the bar sits on your screens." },
        new[] { "General", "FPS counter, startup and app info." } };

    // ------------------------------------------------------------------ named elements
    RadioButton[] nav = new RadioButton[4];
    FrameworkElement[] pages = new FrameworkElement[4];
    TextBlock PageTitle, PageSub, PreviewEmpty, IntervalText, FontSizeText, OpacityText, RadiusText, MarginText,
              FpsPillText, FpsBadgeText, FpsHint, StartupHint, ConfigPathText, AboutText, VersionText, DragText, ColorPopupTitle;
    Image PreviewImage, Logo;
    StackPanel MetricRows, DirtyHint;
    WrapPanel ThemeTiles, Palette;
    System.Windows.Controls.Primitives.UniformGrid ColorChips;
    Slider IntervalSlider, FontSizeSlider, OpacitySlider, RadiusSlider, MarginSlider;
    ComboBox FontCombo, MonitorCombo;
    Popup ColorPopup;
    TextBox HexBox;
    RadioButton CornerTL, CornerTR, CornerBL, CornerBR;
    Border CustomBadge, ScreenMock, FpsPill, FpsBadge;
    Ellipse FpsDot;
    CheckBox StartupSwitch;
    Button ApplyBtn, SaveBtn, CancelBtn, ResetBtn, CloseBtn, MinBtn, DragBtn, ElevateBtn, OpenFileBtn, ShowFolderBtn, GithubBtn, SystemPickerBtn;
    TextBlock GpuNote;
    readonly Dictionary<string, RadioButton> themeTiles = new Dictionary<string, RadioButton>();

    T E<T>(string name) where T : class
    {
        var o = Win.FindName(name) as T;
        if (o == null) throw new InvalidOperationException("SettingsWindow.xaml: missing element " + name);
        return o;
    }

    public SettingsWindow(ISettingsHost h)
    {
        host = h;
        original = h.CurrentConfig;
        work = original.Clone();

        using (var s = typeof(SettingsWindow).Assembly.GetManifestResourceStream("MiniStats.SettingsWindow.xaml"))
            Win = (Window)XamlReader.Load(s);

        for (int i = 0; i < 4; i++) { nav[i] = E<RadioButton>("Nav" + i); pages[i] = E<FrameworkElement>("Page" + i); }
        PageTitle = E<TextBlock>("PageTitle"); PageSub = E<TextBlock>("PageSub");
        PreviewImage = E<Image>("PreviewImage"); PreviewEmpty = E<TextBlock>("PreviewEmpty"); Logo = E<Image>("Logo");
        VersionText = E<TextBlock>("VersionText"); FpsPill = E<Border>("FpsPill"); FpsDot = E<Ellipse>("FpsDot"); FpsPillText = E<TextBlock>("FpsPillText");
        MetricRows = E<StackPanel>("MetricRows"); IntervalSlider = E<Slider>("IntervalSlider"); IntervalText = E<TextBlock>("IntervalText"); GpuNote = E<TextBlock>("GpuNote");
        ThemeTiles = E<WrapPanel>("ThemeTiles"); FontCombo = E<ComboBox>("FontCombo");
        FontSizeSlider = E<Slider>("FontSizeSlider"); FontSizeText = E<TextBlock>("FontSizeText");
        OpacitySlider = E<Slider>("OpacitySlider"); OpacityText = E<TextBlock>("OpacityText");
        RadiusSlider = E<Slider>("RadiusSlider"); RadiusText = E<TextBlock>("RadiusText");
        ColorChips = E<System.Windows.Controls.Primitives.UniformGrid>("ColorChips"); ColorPopup = E<Popup>("ColorPopup"); ColorPopupTitle = E<TextBlock>("ColorPopupTitle");
        Palette = E<WrapPanel>("Palette"); HexBox = E<TextBox>("HexBox"); SystemPickerBtn = E<Button>("SystemPickerBtn");
        MonitorCombo = E<ComboBox>("MonitorCombo"); ScreenMock = E<Border>("ScreenMock"); CustomBadge = E<Border>("CustomBadge");
        CornerTL = E<RadioButton>("CornerTL"); CornerTR = E<RadioButton>("CornerTR"); CornerBL = E<RadioButton>("CornerBL"); CornerBR = E<RadioButton>("CornerBR");
        DragBtn = E<Button>("DragBtn"); DragText = E<TextBlock>("DragText");
        MarginSlider = E<Slider>("MarginSlider"); MarginText = E<TextBlock>("MarginText");
        FpsBadge = E<Border>("FpsBadge"); FpsBadgeText = E<TextBlock>("FpsBadgeText"); FpsHint = E<TextBlock>("FpsHint"); ElevateBtn = E<Button>("ElevateBtn");
        StartupSwitch = E<CheckBox>("StartupSwitch"); StartupHint = E<TextBlock>("StartupHint");
        ConfigPathText = E<TextBlock>("ConfigPathText"); OpenFileBtn = E<Button>("OpenFileBtn"); ShowFolderBtn = E<Button>("ShowFolderBtn");
        AboutText = E<TextBlock>("AboutText"); GithubBtn = E<Button>("GithubBtn");
        ResetBtn = E<Button>("ResetBtn"); CancelBtn = E<Button>("CancelBtn"); ApplyBtn = E<Button>("ApplyBtn"); SaveBtn = E<Button>("SaveBtn");
        DirtyHint = E<StackPanel>("DirtyHint"); CloseBtn = E<Button>("CloseBtn"); MinBtn = E<Button>("MinBtn");

        BuildStaticParts();
        Wire();
        LoadControls();
        ShowPage(0);

        host.Sampled += OnSampled;
        host.MoveModeChanged += OnMoveModeChanged;
        Win.SourceInitialized += delegate { StyleNativeWindow(); };
        Win.Closing += delegate { OnClosing(); };
        Win.Closed += delegate { if (Closed != null) Closed(this, EventArgs.Empty); };
        try { System.Windows.Forms.Integration.ElementHost.EnableModelessKeyboardInterop(Win); } catch { }
    }

    public void Show() { Win.Show(); Win.Activate(); }
    public void Close() { Win.Close(); }
    public bool IsOpen { get { return Win.IsLoaded || Win.IsVisible; } }

    public void Activate()
    {
        if (Win.WindowState == WindowState.Minimized) Win.WindowState = WindowState.Normal;
        Win.Activate();
    }

    public void SelectTab(int i) { if (i >= 0 && i < 4) nav[i].IsChecked = true; }

    // ------------------------------------------------------------------ one-time setup
    void BuildStaticParts()
    {
        try
        {
            using (var ico = System.Drawing.Icon.ExtractAssociatedIcon(System.Windows.Forms.Application.ExecutablePath))
                Logo.Source = Imaging.CreateBitmapSourceFromHIcon(ico.Handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(64, 64));
            Win.Icon = Logo.Source;
        }
        catch { }
        string ver = System.Windows.Forms.Application.ProductVersion;
        VersionText.Text = "v" + ver;
        AboutText.Text = "Version " + ver + "  ·  MIT License  ·  github.com/byocao/mini-stats";
        ConfigPathText.Text = host.ConfigPath;
        GpuNote.Visibility = host.HasGpu ? Visibility.Collapsed : Visibility.Visible;

        // theme tiles
        var names = new List<string>(Config.ThemeNames);
        names.Add("Custom");
        foreach (var n in names)
        {
            var tile = new RadioButton { Style = Res<Style>("Tile"), GroupName = "theme", Tag = n };
            themeTiles[n] = tile;
            ThemeTiles.Children.Add(tile);
            string name = n;
            tile.Checked += delegate { OnThemePicked(name); };
        }

        // colour chips
        foreach (var c in ColorNames)
        {
            var chip = new Button { Style = Res<Style>("Chip"), Tag = c[0] };
            chip.Click += delegate (object s, RoutedEventArgs e) { OpenColorPopup((Button)s); };
            ColorChips.Children.Add(chip);
        }

        // palette
        foreach (var hex in PaletteColors)
        {
            var sw = new Button { Style = Res<Style>("Swatch"), Background = Brush(DColorFrom(hex)), Tag = hex, ToolTip = hex };
            sw.Click += delegate (object s, RoutedEventArgs e) { SetColor(colorKey, DColorFrom((string)((Button)s).Tag)); ColorPopup.IsOpen = false; };
            Palette.Children.Add(sw);
        }

        // fonts
        var fonts = new List<string>();
        foreach (var f in System.Drawing.FontFamily.Families) if (!f.Name.StartsWith("@")) fonts.Add(f.Name);
        fonts.Sort(StringComparer.OrdinalIgnoreCase);
        foreach (var f in fonts) FontCombo.Items.Add(f);

        // monitors
        MonitorCombo.Items.Add("Primary display");
        var all = System.Windows.Forms.Screen.AllScreens;
        for (int i = 0; i < all.Length; i++)
            MonitorCombo.Items.Add(string.Format("Display {0}  ·  {1} × {2}{3}", i + 1, all[i].Bounds.Width, all[i].Bounds.Height, all[i].Primary ? "  (primary)" : ""));
    }

    void Wire()
    {
        for (int i = 0; i < 4; i++) { int idx = i; nav[i].Checked += delegate { ShowPage(idx); }; }

        IntervalSlider.ValueChanged += delegate { IntervalText.Text = (IntervalSlider.Value / 1000.0).ToString("0.00", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.') + " s"; OnChanged(); };
        FontSizeSlider.ValueChanged += delegate { FontSizeText.Text = FontSizeSlider.Value.ToString("0.#", CultureInfo.InvariantCulture) + " pt"; OnChanged(); };
        OpacitySlider.ValueChanged += delegate { OpacityText.Text = Math.Round(OpacitySlider.Value) + " %"; OnChanged(); };
        RadiusSlider.ValueChanged += delegate { RadiusText.Text = Math.Round(RadiusSlider.Value) + " px"; OnChanged(); };
        MarginSlider.ValueChanged += delegate { MarginText.Text = Math.Round(MarginSlider.Value) + " px"; OnChanged(); };
        FontCombo.SelectionChanged += delegate { OnChanged(); };
        MonitorCombo.SelectionChanged += delegate { UpdateScreenMock(); OnChanged(); };

        foreach (var rb in new[] { CornerTL, CornerTR, CornerBL, CornerBR })
            rb.Checked += delegate { if (loading) return; CustomBadge.Visibility = Visibility.Collapsed; OnChanged(); };
        DragBtn.Click += delegate { host.SetMovable(!host.IsMovable, false); UpdateDragButton(); };

        HexBox.KeyDown += delegate (object s, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            DColor c;
            if (TryParseHex(HexBox.Text, out c)) { SetColor(colorKey, c); ColorPopup.IsOpen = false; }
            else HexBox.BorderBrush = Brush(DColor.FromArgb(207, 34, 46));
        };
        SystemPickerBtn.Click += delegate
        {
            ColorPopup.IsOpen = false;
            using (var dlg = new System.Windows.Forms.ColorDialog { FullOpen = true, AnyColor = true, Color = GetColor(colorKey) })
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK) SetColor(colorKey, dlg.Color);
        };

        ElevateBtn.Click += delegate
        {
            if (Dirty()) { host.Commit(work); original = work.Clone(); }
            committed = true;
            host.RestartElevated();
        };
        StartupSwitch.Checked += delegate { OnStartupToggled(true); };
        StartupSwitch.Unchecked += delegate { OnStartupToggled(false); };
        OpenFileBtn.Click += delegate { Run("notepad.exe", "\"" + host.ConfigPath + "\""); };
        ShowFolderBtn.Click += delegate { Run("explorer.exe", "/select,\"" + host.ConfigPath + "\""); };
        GithubBtn.Click += delegate { Run(Program.RepoUrl, null); };

        ResetBtn.Click += delegate { work = new Config(null); LoadControls(); OnChanged(); };
        CancelBtn.Click += delegate { Win.Close(); };
        ApplyBtn.Click += delegate { host.Commit(work); original = work.Clone(); UpdateDirty(); };
        SaveBtn.Click += delegate { host.Commit(work); committed = true; Win.Close(); };
        CloseBtn.Click += delegate { Win.Close(); };
        MinBtn.Click += delegate { Win.WindowState = WindowState.Minimized; };
        Win.PreviewKeyDown += delegate (object s, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && !ColorPopup.IsOpen) { Win.Close(); e.Handled = true; }
            else if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) { host.Commit(work); committed = true; Win.Close(); e.Handled = true; }
        };
    }

    static void Run(string file, string args)
    {
        try { if (args == null) Process.Start(file); else Process.Start(file, args); } catch { }
    }

    void ShowPage(int i)
    {
        for (int k = 0; k < 4; k++) pages[k].Visibility = k == i ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text = PageText[i][0];
        PageSub.Text = PageText[i][1];
    }

    // ------------------------------------------------------------------ load / read
    void LoadControls()
    {
        loading = true;

        order = new List<string>(work.Items);
        foreach (var k in Config.AllItems) if (!order.Contains(k)) order.Add(k);
        enabled = new HashSet<string>(work.Items);
        BuildMetricRows();
        IntervalSlider.Value = Clamp(work.IntervalMs, 250, 3000);

        int fi = FontCombo.Items.IndexOf(work.FontName);
        FontCombo.SelectedIndex = fi >= 0 ? fi : FontCombo.Items.IndexOf("Segoe UI");
        FontSizeSlider.Value = Clamp(work.FontSize, 7, 20);
        OpacitySlider.Value = Clamp(Math.Round(work.Opacity * 100), 20, 100);
        RadiusSlider.Value = Clamp(work.CornerRadius, 0, 20);
        RefreshColors();

        MonitorCombo.SelectedIndex = work.Monitor >= 0 && work.Monitor + 1 < MonitorCombo.Items.Count ? work.Monitor + 1 : 0;
        UpdateScreenMock();
        bool custom = work.Anchor == "custom" && work.X != int.MinValue;
        foreach (var rb in new[] { CornerTL, CornerTR, CornerBL, CornerBR }) rb.IsChecked = false;
        if (!custom) CornerRadio(work.Anchor == "custom" ? "bottom-right" : work.Anchor).IsChecked = true;
        CustomBadge.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        MarginSlider.Value = Clamp(work.Margin, 0, 200);
        UpdateDragButton();

        StartupSwitch.IsChecked = host.StartupEnabled;
        StartupSwitch.IsEnabled = host.IsElevated;
        StartupHint.Text = host.IsElevated
            ? "Launch MiniStats when you sign in, with administrator rights (so FPS works) and no UAC prompt."
            : "Needs administrator rights to change - use \"Restart as administrator\" under FPS counter first.";
        UpdateFpsStatus();

        loading = false;
        // the slider labels are filled by their ValueChanged handlers; make sure they show even if the value didn't change
        IntervalText.Text = (IntervalSlider.Value / 1000.0).ToString("0.00", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.') + " s";
        FontSizeText.Text = FontSizeSlider.Value.ToString("0.#", CultureInfo.InvariantCulture) + " pt";
        OpacityText.Text = Math.Round(OpacitySlider.Value) + " %";
        RadiusText.Text = Math.Round(RadiusSlider.Value) + " px";
        MarginText.Text = Math.Round(MarginSlider.Value) + " px";
        SyncThemeTiles();
        RefreshPreview();
        UpdateDirty();
    }

    void ReadControls()
    {
        var items = new List<string>();
        foreach (var k in order) if (enabled.Contains(k)) items.Add(k);
        work.Items = items;
        work.IntervalMs = (int)IntervalSlider.Value;
        if (FontCombo.SelectedItem != null) work.FontName = (string)FontCombo.SelectedItem;
        work.FontSize = (float)FontSizeSlider.Value;
        work.Opacity = Math.Round(OpacitySlider.Value) / 100.0;
        work.CornerRadius = (int)Math.Round(RadiusSlider.Value);
        work.Monitor = MonitorCombo.SelectedIndex - 1;
        string corner = CheckedCorner();
        work.Anchor = corner ?? "custom";
        work.Margin = (int)Math.Round(MarginSlider.Value);
    }

    void OnChanged()
    {
        if (loading) return;
        ReadControls();
        host.Preview(work);
        SyncThemeTiles();
        RefreshPreview();
        UpdateDirty();
    }

    bool Dirty() { return work.Serialize() != original.Serialize(); }

    void UpdateDirty()
    {
        bool d = Dirty();
        ApplyBtn.IsEnabled = d;
        DirtyHint.Visibility = d ? Visibility.Visible : Visibility.Hidden;
    }

    // ------------------------------------------------------------------ metrics page
    void BuildMetricRows()
    {
        MetricRows.Children.Clear();
        for (int i = 0; i < order.Count; i++)
        {
            string key = order[i];
            string[] info = null;
            foreach (var m in Metrics) if (m[0] == key) info = m;
            if (info == null) continue;

            var row = new Grid { Margin = new Thickness(10, 8, 6, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var accent = new Border { Width = 4, Height = 30, CornerRadius = new CornerRadius(2), Background = Brush(work.ColorOf(info[3])), Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = info[1], FontSize = 13.5, FontWeight = FontWeights.SemiBold });
            text.Children.Add(new TextBlock { Text = info[2], Style = Res<Style>("Hint"), Margin = new Thickness(0, 2, 0, 0) });
            var up = new Button { Style = Res<Style>("IconBtn"), Content = "", ToolTip = "Move up", IsEnabled = i > 0 };
            var down = new Button { Style = Res<Style>("IconBtn"), Content = "", ToolTip = "Move down", IsEnabled = i < order.Count - 1, Margin = new Thickness(2, 0, 14, 0) };
            var sw = new CheckBox { Style = Res<Style>("Switch"), IsChecked = enabled.Contains(key), VerticalAlignment = VerticalAlignment.Center, Tag = key };
            up.Click += delegate { MoveMetric(key, -1); };
            down.Click += delegate { MoveMetric(key, +1); };
            sw.Checked += delegate { if (loading) return; enabled.Add(key); OnChanged(); };
            sw.Unchecked += delegate { if (loading) return; enabled.Remove(key); OnChanged(); };

            Grid.SetColumn(text, 1); Grid.SetColumn(up, 2); Grid.SetColumn(down, 3); Grid.SetColumn(sw, 4);
            row.Children.Add(accent); row.Children.Add(text); row.Children.Add(up); row.Children.Add(down); row.Children.Add(sw);
            MetricRows.Children.Add(row);
            if (i < order.Count - 1) MetricRows.Children.Add(new Border { Height = 1, Background = Res<Brush>("Line"), Margin = new Thickness(10, 0, 10, 0) });
        }
    }

    void MoveMetric(string key, int dir)
    {
        int i = order.IndexOf(key), j = i + dir;
        if (i < 0 || j < 0 || j >= order.Count) return;
        order.RemoveAt(i);
        order.Insert(j, key);
        bool was = loading; loading = true; BuildMetricRows(); loading = was;
        OnChanged();
    }

    // ------------------------------------------------------------------ appearance page
    void OnThemePicked(string name)
    {
        if (loading || name == "Custom") return;
        work.ApplyTheme(name);
        RefreshColors();
        OnChanged();
    }

    void SyncThemeTiles()
    {
        bool was = loading;
        loading = true;
        string current = work.DetectTheme() ?? "Custom";
        foreach (var kv in themeTiles)
        {
            var cfg = kv.Key == "Custom" ? work : ThemeConfig(kv.Key);
            kv.Value.Content = TileContent(kv.Key, cfg);
            kv.Value.Visibility = kv.Key == "Custom" && current != "Custom" ? Visibility.Collapsed : Visibility.Visible;
            kv.Value.IsChecked = kv.Key == current;
        }
        loading = was;
    }

    static Config ThemeConfig(string name) { var c = new Config(null); c.ApplyTheme(name); return c; }

    FrameworkElement TileContent(string name, Config c)
    {
        var sp = new StackPanel { Width = 132 };
        var face = new Border { Height = 58, CornerRadius = new CornerRadius(9), Background = Brush(c.Back) };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        string[][] sample = { new[] { "144", "fps" }, new[] { "23%", "cpu" }, new[] { "61%", "ram" }, new[] { "87%", "gpu" } };
        foreach (var s in sample)
            bar.Children.Add(new TextBlock { Text = s[0], Foreground = Brush(c.ColorOf(s[1])), FontWeight = FontWeights.Bold, FontSize = 11.5, Margin = new Thickness(3.5, 0, 3.5, 0) });
        face.Child = bar;
        sp.Children.Add(face);
        sp.Children.Add(new TextBlock { Text = name, Margin = new Thickness(6, 8, 6, 3), FontWeight = FontWeights.SemiBold, FontSize = 12.5 });
        return sp;
    }

    void RefreshColors()
    {
        foreach (Button chip in ColorChips.Children)
        {
            string key = (string)chip.Tag;
            var c = GetColor(key);
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new Ellipse { Width = 26, Height = 26, Fill = Brush(c), Stroke = new SolidColorBrush(System.Windows.Media.Color.FromArgb(70, 255, 255, 255)), StrokeThickness = 1, VerticalAlignment = VerticalAlignment.Center });
            var t = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            string label = key;
            foreach (var n in ColorNames) if (n[0] == key) label = n[1];
            t.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, FontSize = 12.5 });
            t.Children.Add(new TextBlock { Text = Hex(c), FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 11, Foreground = Res<Brush>("Sub") });
            sp.Children.Add(t);
            chip.Content = sp;
        }
        bool was = loading; loading = true; BuildMetricRows(); loading = was; // the accent bars follow the colours
    }

    void OpenColorPopup(Button chip)
    {
        colorKey = (string)chip.Tag;
        foreach (var n in ColorNames) if (n[0] == colorKey) ColorPopupTitle.Text = n[1] + " colour";
        HexBox.Text = Hex(GetColor(colorKey));
        HexBox.ClearValue(Control.BorderBrushProperty);
        ColorPopup.PlacementTarget = chip;
        ColorPopup.IsOpen = true;
        HexBox.Focus();
        HexBox.SelectAll();
    }

    DColor GetColor(string key) { return key == "background" ? work.Back : work.ColorOf(key); }

    void SetColor(string key, DColor c)
    {
        if (key == null) return;
        if (key == "background") work.Back = c; else work.Colors[key] = c;
        RefreshColors();
        OnChanged();
    }

    // ------------------------------------------------------------------ position page
    RadioButton CornerRadio(string anchor)
    {
        switch (anchor)
        {
            case "top-left": return CornerTL;
            case "top-right": return CornerTR;
            case "bottom-left": return CornerBL;
            default: return CornerBR;
        }
    }

    string CheckedCorner()
    {
        if (CornerTL.IsChecked == true) return "top-left";
        if (CornerTR.IsChecked == true) return "top-right";
        if (CornerBL.IsChecked == true) return "bottom-left";
        if (CornerBR.IsChecked == true) return "bottom-right";
        return null;
    }

    void UpdateScreenMock()
    {
        var c = work.Clone();
        c.Monitor = MonitorCombo.SelectedIndex - 1;
        var b = c.TargetScreen().Bounds;
        double aspect = b.Height > 0 ? b.Width / (double)b.Height : 16.0 / 9.0;
        double w = 300, h = w / aspect;
        if (h > 190) { h = 190; w = h * aspect; }
        if (h < 110) h = 110;
        ScreenMock.Width = w;
        ScreenMock.Height = h;
    }

    void UpdateDragButton() { DragText.Text = host.IsMovable ? "Lock it here" : "Drag to place"; }

    void OnMoveModeChanged(object sender, EventArgs e)
    {
        Win.Dispatcher.BeginInvoke((Action)delegate
        {
            UpdateDragButton();
            if (host.IsMovable) return;
            var c = host.CurrentConfig; // where the bar was dropped
            work.X = c.X; work.Y = c.Y;
            loading = true;
            foreach (var rb in new[] { CornerTL, CornerTR, CornerBL, CornerBR }) rb.IsChecked = false;
            CustomBadge.Visibility = Visibility.Visible;
            loading = false;
            OnChanged();
        });
    }

    // ------------------------------------------------------------------ general page
    void UpdateFpsStatus()
    {
        bool on = host.FpsError == null;
        var green = DColor.FromArgb(61, 220, 132); var amber = DColor.FromArgb(255, 190, 80);
        FpsDot.Fill = Brush(on ? green : amber);
        FpsPill.Background = Brush(on ? DColor.FromArgb(24, 38, 31) : DColor.FromArgb(42, 33, 18));
        FpsPillText.Text = on ? "FPS counter active" : "FPS needs admin rights";
        FpsBadge.Background = Brush(on ? DColor.FromArgb(28, 58, 42) : DColor.FromArgb(58, 44, 20));
        FpsBadgeText.Foreground = Brush(on ? green : amber);
        FpsBadgeText.Text = on ? "Active" : "Off";
        FpsHint.Text = on
            ? "Counting the frames of whichever window is in front."
            : "Windows only lets administrators read the frame events used to count FPS. Everything else works without it.";
        ElevateBtn.Visibility = host.IsElevated ? Visibility.Collapsed : Visibility.Visible;
    }

    void OnStartupToggled(bool on)
    {
        if (loading) return;
        string err = host.SetStartup(on);
        if (err == null) return;
        MessageBox.Show(Win, err, "MiniStats", MessageBoxButton.OK, MessageBoxImage.Warning);
        loading = true; StartupSwitch.IsChecked = !on; loading = false;
    }

    // ------------------------------------------------------------------ preview + lifetime
    void OnSampled(object sender, EventArgs e)
    {
        RefreshPreview();
        UpdateFpsStatus();
    }

    double DpiScale()
    {
        try { var src = PresentationSource.FromVisual(Win); if (src != null) return src.CompositionTarget.TransformToDevice.M11; } catch { }
        using (var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX / 96.0;
    }

    void RefreshPreview()
    {
        var parts = (host.Last ?? Snapshot.Sample()).ToParts(work.Items);
        bool any = parts.Count > 0;
        PreviewEmpty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        PreviewImage.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        if (!any) return;
        double scale = DpiScale();
        using (var bmp = RenderCli.RenderBar(work, parts, (float)scale))
        {
            PreviewImage.Source = ToSource(bmp);
            PreviewImage.Width = bmp.Width / scale;
            PreviewImage.Height = bmp.Height / scale;
        }
    }

    void OnClosing()
    {
        host.Sampled -= OnSampled;
        host.MoveModeChanged -= OnMoveModeChanged;
        if (host.IsMovable) host.SetMovable(false, false);
        if (!committed) host.Preview(original); // Cancel / close: back to the saved look
    }

    // dark title-bar region + Windows 11 rounded corners
    void StyleNativeWindow()
    {
        try
        {
            var hwnd = new WindowInteropHelper(Win).Handle;
            int on = 1, round = 2;
            DwmSetWindowAttribute(hwnd, 20, ref on, 4);    // DWMWA_USE_IMMERSIVE_DARK_MODE
            DwmSetWindowAttribute(hwnd, 33, ref round, 4); // DWMWA_WINDOW_CORNER_PREFERENCE = round
        }
        catch { }
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    // ------------------------------------------------------------------ helpers
    T Res<T>(string key) where T : class { return (T)Win.Resources[key]; }

    static double Clamp(double v, double lo, double hi) { return v < lo ? lo : v > hi ? hi : v; }

    static SolidColorBrush Brush(DColor c)
    {
        var b = new SolidColorBrush(System.Windows.Media.Color.FromRgb(c.R, c.G, c.B));
        b.Freeze();
        return b;
    }

    static string Hex(DColor c) { return string.Format("#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B); }

    static DColor DColorFrom(string hex) { return System.Drawing.ColorTranslator.FromHtml(hex); }

    static bool TryParseHex(string s, out DColor c)
    {
        c = DColor.Empty;
        s = (s ?? "").Trim();
        if (!s.StartsWith("#")) s = "#" + s;
        if (s.Length != 7) return false;
        int v;
        if (!int.TryParse(s.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v)) return false;
        c = DColor.FromArgb((v >> 16) & 255, (v >> 8) & 255, v & 255);
        return true;
    }

    static BitmapSource ToSource(System.Drawing.Bitmap b)
    {
        using (var ms = new MemoryStream())
        {
            b.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            ms.Position = 0;
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.StreamSource = ms;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
    }
}
