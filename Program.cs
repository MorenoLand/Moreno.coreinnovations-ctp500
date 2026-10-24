using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Plugin.BLE;
using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;
#if GUI
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using static TrayIcon;
#endif

#if GUI
class Settings {
    public string? LastDevice { get; set; }
    public int Threshold { get; set; } = 128;
    public bool Dither { get; set; } = true;
    public bool Invert { get; set; }
    public int Density { get; set; } = 3;
    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ThermalPrint", "settings.json");
    public static Settings Load() {
        try { if (File.Exists(FilePath)) return System.Text.Json.JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings(); } catch { }
        return new Settings();
    }
    public void Save() {
        try { Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!); File.WriteAllText(FilePath, System.Text.Json.JsonSerializer.Serialize(this)); } catch { }
    }
}

static class T {
    public static ISolidColorBrush BG => S(25, 25, 28);
    public static ISolidColorBrush Panel => S(37, 37, 40);
    public static ISolidColorBrush Surface => S(45, 45, 48);
    public static ISolidColorBrush Border => S(63, 63, 70);
    public static ISolidColorBrush Text => S(212, 212, 216);
    public static ISolidColorBrush TextDim => S(122, 122, 130);
    public static ISolidColorBrush Accent => S(0, 122, 204);
    public static ISolidColorBrush Green => S(106, 204, 88);
    public static ISolidColorBrush Red => S(228, 88, 88);
    public static ISolidColorBrush Orange => S(230, 160, 50);
    public static ISolidColorBrush S(byte r, byte g, byte b) => new SolidColorBrush(Color.FromRgb(r, g, b));
}
#endif

public static class Cmd {
    public static byte[] GetInfo()    => new byte[] { 0x51,0x78,0xA8,0x00,0x01,0x00,0x00,0x00,0xFF };
    public static byte[] GetBattery() => new byte[] { 0x51,0x78,0xBA,0x00,0x01,0x00,0x00,0x00,0xFF };
    public static byte[] GetStatus()  => new byte[] { 0x51,0x78,0xA3,0x00,0x01,0x00,0x00,0x00,0xFF };
    public static byte[] GetDevId()   => new byte[] { 0x51,0x78,0xBB,0x00,0x01,0x00,0x01,0x07,0xFF };
    public static byte[] SelfTest()   => new byte[] { 0x51,0x78,0xA6,0x00,0x0B,0x00,0xAA,0x55,0x17,0x38,0x44,0x5F,0x5F,0x5F,0x44,0x38,0x2C,0xA1,0xFF };
    public static byte[] StopPrint()  => new byte[] { 0x51,0x78,0xA6,0x00,0x01,0x00,0x05,0x1B,0xFF };
    public static byte[] PrintModeImg()  => new byte[] { 0x51,0x78,0xBE,0x00,0x01,0x00,0x00,0x00,0xFF };
    public static byte[] PrintModeText() => new byte[] { 0x51,0x78,0xBE,0x00,0x01,0x00,0x01,0x07,0xFF };
    public static byte[] EscInit() => new byte[] { 0x1B, 0x40 };
    // Frame: 51 78 <cmd> <dir> <len lo> <len hi> <payload...> <crc8(payload)> FF
    // CRC-8 poly 0x07, init 0 (matches the density/stop/devid frames above).
    public static byte Crc8(ReadOnlySpan<byte> d) {
        byte c = 0;
        foreach (var b in d) {
            c ^= b;
            for (int i = 0; i < 8; i++) c = (byte)((c & 0x80) != 0 ? (c << 1) ^ 0x07 : c << 1);
        }
        return c;
    }
    public static byte[] Frame(byte cmd, params byte[] payload) {
        var f = new byte[8 + payload.Length];
        f[0] = 0x51; f[1] = 0x78; f[2] = cmd; f[3] = 0x00;
        f[4] = (byte)(payload.Length & 0xFF); f[5] = (byte)(payload.Length >> 8);
        payload.CopyTo(f, 6);
        f[6 + payload.Length] = Crc8(payload);
        f[7 + payload.Length] = 0xFF;
        return f;
    }
    public static byte[] FeedLines(int lines) {
        lines = Math.Clamp(lines, 1, 0xFFFF);
        return Frame(0xA1, (byte)(lines & 0xFF), (byte)(lines >> 8));
    }
    public static byte[] SetDensity(int level) {
        byte[][] d = {
            new byte[] { 0x51,0x78,0xA4,0x00,0x01,0x00,0x31,0x97,0xFF },
            new byte[] { 0x51,0x78,0xA4,0x00,0x01,0x00,0x32,0x9E,0xFF },
            new byte[] { 0x51,0x78,0xA4,0x00,0x01,0x00,0x33,0x99,0xFF },
            new byte[] { 0x51,0x78,0xA4,0x00,0x01,0x00,0x34,0x8C,0xFF },
            new byte[] { 0x51,0x78,0xA4,0x00,0x01,0x00,0x35,0x8B,0xFF },
        };
        return d[Math.Clamp(level, 0, 4)];
    }

    public static string ParseResponse(byte[] data) {
        if (data == null || data.Length == 0) return "RX: (empty)";
        var hex = Convert.ToHexString(data);
        if (data.Length >= 5 && data[0] == 0x1B && data[1] == 0x1F && data[2] == 0xFE) return $"RX SYSTEM: {hex}";
        if (data.Length >= 9 && data[0] == 0xFA && data[1] == 0x01) return $"RX SYSTEM: {hex}";
        if (data.Length >= 3 && data[0] == 0x1B && data[1] == 0x1E && data[2] == 0x01) return $"RX SYSTEM: {hex}";
        if (data.Length >= 2 && data[0] == 0x45 && data[1] == 0x52) return $"RX ERROR: {hex}";
        if (data.Length >= 3 && data[0] == 0x51 && data[1] == 0x78) {
            string name = data[2] switch {
                0xA8 => "GET_INFO", 0xBA => "GET_BATTERY", 0xA3 => "GET_STATUS",
                0xBB => "GET_DEV_ID", 0xA4 => "SET_DENSITY", 0xA1 => "FEED",
                0xBE => "SET_PRINT_MODE", 0xA6 => "SELFTEST/STOP",
                0xBD => "FEED_PAPER", 0xAF => "SET_ENERGY", 0xAC => "CHECK_LABEL",
                _ => $"0x{data[2]:X2}"
            };
            return $"RX [{name}]: {hex}";
        }
        return $"RX: {hex}";
    }

    public static int? ParseBattery(byte[] data) {
        if (data?.Length >= 7 && data[0] == 0x51 && data[1] == 0x78 && data[2] == 0xBA) return data[6];
        return null;
    }

    public static (bool paperOut, bool coverOpen, bool overheat)? ParseStatus(byte[] data) {
        if (data?.Length >= 7 && data[0] == 0x51 && data[1] == 0x78 && data[2] == 0xA3) {
            byte s = data[6];
            return ((s & 0x01) != 0, (s & 0x04) != 0, (s & 0x08) != 0);
        }
        return null;
    }
}

#if GUI
static class TrayIcon {
    public const int WM_TRAYICON = 0x8001;
    public const int WM_LBUTTONUP = 0x0202;
    public const int WM_RBUTTONUP = 0x0205;
    public const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    public const int NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATA {
        public int cbSize; public IntPtr hWnd; public int uID; public int uFlags;
        public int uCallbackMessage; public IntPtr hIcon; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState; public int dwStateMask; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uVersion; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);
    [DllImport("user32.dll")] public static extern IntPtr CreateMenu();
    [DllImport("user32.dll")] public static extern bool AppendMenu(IntPtr hMenu, uint uFlags, IntPtr uIDNewItem, string lpNewItem);
    [DllImport("user32.dll")] public static extern bool SetMenuDefaultItem(IntPtr hMenu, uint uIDNewItem, uint fByPos);
    [DllImport("user32.dll")] public static extern int TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);
    [DllImport("user32.dll")] public static extern bool DestroyMenu(IntPtr hMenu);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    public const uint MN_GETHMENU = 0x01E1;
    public const uint WM_INITMENUPOPUP = 0x0117;
    public const uint WM_DRAWITEM = 0x002B;
    public const uint WM_MEASUREITEM = 0x002C;
    public const uint WM_MENUCHAR = 0x0120;
    public const uint MF_STRING = 0x0000;
    public const uint MF_SEPARATOR = 0x0800;
    public const uint MF_BYCOMMAND = 0x0000;
    public const uint TPM_RIGHTBUTTON = 0x0002;
    public const uint TPM_NONOTIFY = 0x0080;
    public const uint TPM_RETURNCMD = 0x0100;
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
}

class App {
    const string PRINTER_SVC = "49535343-fe7d-4ae5-8fa9-9fafd205e455";
    const string TX_UUID = "49535343-8841-43f4-a8d4-ecbe34729bb3";
    const string RX_UUID = "49535343-1e4d-4bd9-ba61-23c647249616";

    public static Stream? AppIcon() => typeof(App).Assembly.GetManifestResourceStream("thermalprint.png");

    public static Window? _mainWindow;
    static TrayHelper? _tray;

    static async Task Main(string[] args) {
        var app = BuildAvaloniaApp();
        app.AfterSetup(_ => { });
        app.StartWithClassicDesktopLifetime(args);
    }

    static void ToggleWindow() {
        if (_mainWindow == null) return;
        Dispatcher.UIThread.Post(() => {
            if (_mainWindow.IsVisible) { _mainWindow.Hide(); }
            else { _mainWindow.Show(); _mainWindow.WindowState = WindowState.Normal; _mainWindow.Activate(); }
        });
    }

    static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<AppBase>()
        .UsePlatformDetect().WithInterFont().LogToTrace();

    class AppBase : Application {
        public override void Initialize() => Styles.Add(new Avalonia.Themes.Simple.SimpleTheme());
        public override void OnFrameworkInitializationCompleted() {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) {
                desktop.MainWindow = BuildWindow();
                _mainWindow = desktop.MainWindow;
                desktop.MainWindow.Closing += (s, e) => {
                    e.Cancel = true;
                    _mainWindow?.Hide();
                };
                desktop.MainWindow.Opened += (_, _) => {
                    _tray = new TrayHelper(() => ToggleWindow(), () => {
                        _tray?.Remove();
                        desktop.Shutdown();
                    });
                };
            }
            base.OnFrameworkInitializationCompleted();
        }
    }

    static Window BuildWindow() {
        var state = new PrinterState();
        var settings = Settings.Load();
        state.Invert = settings.Invert;
        bool printing = false;
        var log = new List<string>();
        var canvasRef = new Canvas();

        var win = new Window {
            Title = "ThermalPrint", Width = 960, Height = 780,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            SystemDecorations = SystemDecorations.BorderOnly,
            ExtendClientAreaToDecorationsHint = true,
            ExtendClientAreaTitleBarHeightHint = -1,
            ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome,
            Background = T.BG, Foreground = T.Text,
            TransparencyLevelHint = new List<WindowTransparencyLevel> { WindowTransparencyLevel.AcrylicBlur }
        };
        try { using var st = AppIcon(); if (st != null) win.Icon = new WindowIcon(st); } catch { }

        var winRoot = new Grid();
        winRoot.RowDefinitions.Add(new RowDefinition(32, GridUnitType.Pixel));
        winRoot.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star));
        win.Content = winRoot;

        // Title bar
        var titleBar = new Border { Background = T.Panel, Height = 32 };
        Grid.SetRow(titleBar, 0); winRoot.Children.Add(titleBar);
        var tbGrid = new Grid(); titleBar.Child = tbGrid;
        var titleCenter = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        try { using var st = AppIcon(); if (st != null) titleCenter.Children.Add(new Image { Source = new Bitmap(st), Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,6,0) }); } catch { }
        titleCenter.Children.Add(new TextBlock { Text = "THERMAL PRINT", Foreground = T.Text, FontSize = 12, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        tbGrid.Children.Add(titleCenter);
        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        tbGrid.Children.Add(btnPanel);
        Button TitleBtn(string c, Action a) {
            var b = new Button { Content = c, Width = 46, Height = 32, Background = Brushes.Transparent, Foreground = T.TextDim, BorderThickness = new Thickness(0), FontSize = 14, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Cursor = Cursor.Parse("Hand") };
            b.Click += (_, _) => a(); btnPanel.Children.Add(b); return b;
        }
        TitleBtn("─", () => win.WindowState = WindowState.Minimized);
        TitleBtn("□", () => win.WindowState = win.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized);
        var closeBtn = TitleBtn("✕", () => win.Close());
        closeBtn.PointerEntered += (_, _) => { closeBtn.Background = T.Red; closeBtn.Foreground = Brushes.White; };
        closeBtn.PointerExited += (_, _) => { closeBtn.Background = Brushes.Transparent; closeBtn.Foreground = T.TextDim; };
        titleBar.PointerPressed += (_, e) => { if (e.GetCurrentPoint(titleBar).Properties.IsLeftButtonPressed) win.BeginMoveDrag(e); };

        // Main layout
        var root = new Grid(); Grid.SetRow(root, 1); winRoot.Children.Add(root);
        root.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star));
        root.RowDefinitions.Add(new RowDefinition(6, GridUnitType.Pixel));
        root.RowDefinitions.Add(new RowDefinition(160, GridUnitType.Pixel));
        root.ColumnDefinitions.Add(new ColumnDefinition(260, GridUnitType.Pixel));
        root.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));

        // Sidebar
        var sidebar = new ScrollViewer { Background = T.Panel };
        Grid.SetColumn(sidebar, 0); Grid.SetRowSpan(sidebar, 3); root.Children.Add(sidebar);
        var sb = new StackPanel { Margin = new Thickness(16, 12) };
        sidebar.Content = sb;

        void Header(string t) {
            var sp = new StackPanel { Margin = new Thickness(0, 12, 0, 6) };
            sp.Children.Add(new Border { Width = 20, Height = 2, Background = T.Accent, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0,0,0,4) });
            sp.Children.Add(new TextBlock { Text = t, FontSize = 10, FontWeight = FontWeight.SemiBold, Foreground = T.TextDim });
            sb.Children.Add(sp);
        }
        Button FlatBtn(string text, int h = 32) => new() {
            Content = text, Height = h, Foreground = T.Text, Background = T.Surface,
            BorderBrush = T.Border, BorderThickness = new Thickness(1),
            HorizontalContentAlignment = HorizontalAlignment.Center, FontSize = 12,
            Margin = new Thickness(0, 4, 0, 0), Cursor = Cursor.Parse("Hand")
        };

        var logBox = new TextBox {
            IsReadOnly = true, Background = T.BG, Foreground = T.TextDim, BorderThickness = new Thickness(0),
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New"), FontSize = 11,
            TextWrapping = TextWrapping.Wrap, Padding = new Thickness(8), AcceptsReturn = true
        };
        void Log(string msg) {
            log.Add($"[{DateTime.Now:HH:mm:ss.fff}] {msg}");
            Dispatcher.UIThread.Post(() => logBox.Text = string.Join("\n", log.TakeLast(200)));
        }

        // PRINTER
        Header("PRINTER");
        var statusTxt = new TextBlock { Text = "● Disconnected", Foreground = T.Orange, FontSize = 11, Margin = new Thickness(0,0,0,2) };
        sb.Children.Add(statusTxt);
        var batteryTxt = new TextBlock { Text = "", Foreground = T.TextDim, FontSize = 10, Margin = new Thickness(0,0,0,2) };
        sb.Children.Add(batteryTxt);
        var batteryBar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 4, Margin = new Thickness(0,0,0,4), IsVisible = false, Background = T.Surface };
        sb.Children.Add(batteryBar);
        var paperStatTxt = new TextBlock { Text = "", Foreground = T.TextDim, FontSize = 10, Margin = new Thickness(0,0,0,4) };
        sb.Children.Add(paperStatTxt);
        var connectBtn = FlatBtn("Connect to Printer");
        sb.Children.Add(connectBtn);
        var chooseBtn = FlatBtn("Choose device...", 26);
        sb.Children.Add(chooseBtn);

        var printerControls = new StackPanel { IsVisible = false };
        sb.Children.Add(printerControls);

        // Density
        string[] densityNames = { "Lightest", "Light", "Default", "Dark", "Darkest" };
        settings.Density = Math.Clamp(settings.Density, 1, 5);
        var densityLabel = new TextBlock { Text = $"Density: {settings.Density} ({densityNames[settings.Density - 1]})", Foreground = T.TextDim, FontSize = 10, Margin = new Thickness(0,8,0,2) };
        printerControls.Children.Add(densityLabel);
        var densitySlider = new Slider { Minimum = 1, Maximum = 5, Value = settings.Density, IsSnapToTickEnabled = true, TickFrequency = 1, Height = 20 };
        printerControls.Children.Add(densitySlider);
        densitySlider.ValueChanged += async (_, _) => {
            int level = (int)densitySlider.Value - 1;
            densityLabel.Text = $"Density: {(int)densitySlider.Value} ({densityNames[level]})";
            settings.Density = (int)densitySlider.Value; settings.Save();
            if (state.IsConnected) { await state.Send(Cmd.SetDensity(level)); Log($"Density → level {(int)densitySlider.Value}"); }
        };

        // Feed controls
        var feedRow = new Grid { Margin = new Thickness(0,6,0,0) };
        feedRow.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        feedRow.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        var feedInput = new NumericUpDown { Value = 20, Minimum = 1, Maximum = 1000, Height = 30, FontSize = 11, Margin = new Thickness(0,4,4,0) };
        var feedFwdBtn = FlatBtn("Feed Lines");
        Grid.SetColumn(feedInput, 0); Grid.SetColumn(feedFwdBtn, 1);
        feedRow.Children.Add(feedInput); feedRow.Children.Add(feedFwdBtn);
        printerControls.Children.Add(feedRow);
        feedFwdBtn.Click += async (_, _) => { int n = (int)(feedInput.Value ?? 1); await state.Send(Cmd.FeedLines(n)); Log($"Feed {n} lines"); };

        // Self-test / Stop
        var testRow = new Grid { Margin = new Thickness(0,4,0,0) };
        testRow.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        testRow.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        var selfTestBtn = FlatBtn("Self Test");
        var stopBtn = FlatBtn("Stop");
        Grid.SetColumn(selfTestBtn, 0); Grid.SetColumn(stopBtn, 1);
        testRow.Children.Add(selfTestBtn); testRow.Children.Add(stopBtn);
        printerControls.Children.Add(testRow);
        selfTestBtn.Click += async (_, _) => { await state.Send(Cmd.SelfTest()); Log("Self-test sent"); };
        stopBtn.Click += async (_, _) => { await state.Send(Cmd.StopPrint()); Log("Stop sent"); };

        var refreshBtn = FlatBtn("Refresh Status", 28);
        printerControls.Children.Add(refreshBtn);
        refreshBtn.Click += async (_, _) => {
            await state.Send(Cmd.GetBattery()); await Task.Delay(100);
            await state.Send(Cmd.GetStatus());
        };

        // IMAGE
        const int PaperW = 384;
        Slider threshSlider = null!; CheckBox ditherChk = null!;
        Header("IMAGE");
        var imgBtn = FlatBtn("Load Image"); sb.Children.Add(imgBtn);
        var imgName = new TextBlock { Foreground = T.TextDim, FontSize = 10, Margin = new Thickness(0,2,0,0), TextTrimming = TextTrimming.CharacterEllipsis };
        sb.Children.Add(imgName);
        imgBtn.Click += async (_, _) => {
            var files = await win.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
                Title = "Load Image",
                FileTypeFilter = new[] { new FilePickerFileType("Images") { Patterns = new[] { "*.jpg","*.jpeg","*.png","*.bmp","*.gif" } } }
            });
            if (files.Count == 0) return;
            try {
                using var stream = await files[0].OpenReadAsync();
                await LoadImage(stream, files[0].Name);
            } catch (Exception ex) { Log($"Image load failed: {ex.Message}"); await ShowMsg(win, $"Could not load image: {ex.Message}"); }
        };
        async Task LoadImage(Stream stream, string name) {
            var bmp = SkiaSharp.SKBitmap.Decode(stream);
            if (bmp == null) { Log($"Unsupported image: {name}"); await ShowMsg(win, $"Could not load {name}: unsupported image format."); return; }
            state.Source?.Dispose();
            state.Source = bmp;
            imgName.Text = name; Refresh();
        }
        DragDrop.SetAllowDrop(win, true);
        win.AddHandler(DragDrop.DropEvent, async (_, e) => {
            try {
                var f = e.Data.GetFiles()?.OfType<IStorageFile>().FirstOrDefault();
                if (f == null) return;
                using var stream = await f.OpenReadAsync();
                await LoadImage(stream, f.Name);
            } catch (Exception ex) { Log($"Image drop failed: {ex.Message}"); }
        });
        sb.Children.Add(new TextBlock { Text = "…or drag an image onto the window", Foreground = T.TextDim, FontSize = 10, Margin = new Thickness(0,2,0,0) });

        // TEXT
        Header("TEXT");
        var textInput = new TextBox { MinHeight = 60, Text = "Hello World", FontSize = 12, Padding = new Thickness(6,4), Margin = new Thickness(0,4,0,0), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
        sb.Children.Add(textInput);
        var textSize = new ComboBox { Height = 30, Foreground = T.Text, Background = T.Surface, BorderBrush = T.Border, Margin = new Thickness(0,4,0,0), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
        textSize.ItemsSource = new[] { "12","16","20","24","32","48" };
        textSize.SelectedIndex = 3; sb.Children.Add(textSize);
        var alignBox = new ComboBox { Height = 30, Foreground = T.Text, Background = T.Surface, BorderBrush = T.Border, Margin = new Thickness(0,4,0,0), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
        alignBox.ItemsSource = new[] { "Left","Center","Right" }; alignBox.SelectedIndex = 0; sb.Children.Add(alignBox);
        var boldChk = new CheckBox { Content = "Bold", FontSize = 12, Margin = new Thickness(0,4,0,0) };
        sb.Children.Add(boldChk);
        var textAddBtn = FlatBtn("Add Text"); sb.Children.Add(textAddBtn);
        var textUndoBtn = FlatBtn("Remove Last Text"); sb.Children.Add(textUndoBtn);
        textAddBtn.Click += (_, _) => {
            if (string.IsNullOrWhiteSpace(textInput.Text)) return;
            if (int.TryParse((string?)textSize.SelectedItem, out int sz) && sz > 0)
                state.Texts.Add(new TextLayer(textInput.Text!, sz, Math.Max(0, alignBox.SelectedIndex), boldChk.IsChecked == true));
            Refresh();
        };
        textUndoBtn.Click += (_, _) => { if (state.Texts.Count > 0) { state.Texts.RemoveAt(state.Texts.Count - 1); Refresh(); } };

        // ADJUST
        Header("ADJUST");
        threshSlider = new Slider { Minimum = 0, Maximum = 255, Value = Math.Clamp(settings.Threshold, 0, 255), Height = 20, Margin = new Thickness(0,2,0,6) };
        var threshLabel = new TextBlock { Text = $"Threshold {Math.Clamp(settings.Threshold, 0, 255)}", Foreground = T.TextDim, FontSize = 11 };
        sb.Children.Add(threshLabel); sb.Children.Add(threshSlider);
        threshSlider.ValueChanged += (_, _) => { threshLabel.Text = $"Threshold {(int)threshSlider.Value}"; settings.Threshold = (int)threshSlider.Value; settings.Save(); Refresh(); };
        ditherChk = new CheckBox { Content = "Dither photos (Floyd-Steinberg)", IsChecked = settings.Dither, FontSize = 12, Margin = new Thickness(0,2,0,0) };
        sb.Children.Add(ditherChk);
        ditherChk.IsCheckedChanged += (_, _) => { settings.Dither = ditherChk.IsChecked == true; settings.Save(); Refresh(); };
        var invertChk = new CheckBox { Content = "Invert output", IsChecked = settings.Invert, FontSize = 12, Margin = new Thickness(0,4,0,0) };
        sb.Children.Add(invertChk);
        invertChk.IsCheckedChanged += (_, _) => { state.Invert = invertChk.IsChecked == true; settings.Invert = state.Invert; settings.Save(); Refresh(); };

        // CLEAR / PRINT
        sb.Children.Add(new Border { Height = 1, Background = T.Border, Margin = new Thickness(0,16,0,8) });
        var clearBtn = FlatBtn("Clear All"); sb.Children.Add(clearBtn);
        clearBtn.Click += (_, _) => { state.Clear(); imgName.Text = ""; Refresh(); };
        var printBtn = new Button {
            Content = "PRINT", Height = 44, FontSize = 14, FontWeight = FontWeight.Bold,
            Background = T.Accent, Foreground = Brushes.White, BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0,10,0,0), Cursor = Cursor.Parse("Hand")
        };
        sb.Children.Add(printBtn);
        var printProgress = new ProgressBar { Minimum = 0, Maximum = 1, Height = 4, Margin = new Thickness(0,6,0,0), IsVisible = false, Foreground = T.Accent };
        sb.Children.Add(printProgress);

        void SetConnected(bool on, string? text = null, ISolidColorBrush? color = null) {
            printerControls.IsVisible = on;
            connectBtn.Content = on ? "Disconnect" : (settings.LastDevice != null ? "Reconnect" : "Connect to Printer");
            chooseBtn.IsVisible = !on;
            statusTxt.Text = text ?? (on ? "● Connected" : "● Disconnected");
            statusTxt.Foreground = color ?? (on ? T.Green : T.Orange);
            if (!on) { batteryTxt.Text = ""; paperStatTxt.Text = ""; batteryBar.IsVisible = false; state.Battery = null; state.LastStatus = null; }
        }
        state.ConnectionLost = () => Dispatcher.UIThread.Post(() => SetConnected(false, "● Connection lost", T.Red));

        // Connect: reconnect to the remembered printer first, otherwise (or on request) pick from a live scan.
        void ApplyRx(byte[] data) {
            var bat = Cmd.ParseBattery(data);
            var sts = Cmd.ParseStatus(data);
            if (bat.HasValue) state.Battery = bat.Value;
            if (sts.HasValue) state.LastStatus = sts.Value;
            Dispatcher.UIThread.Post(() => {
                if (bat.HasValue) {
                    batteryTxt.Text = $"Battery: {bat.Value}%";
                    var col = bat.Value < 20 ? T.Red : bat.Value < 50 ? T.Orange : T.Green;
                    batteryTxt.Foreground = col; batteryBar.Foreground = col;
                    batteryBar.Value = bat.Value; batteryBar.IsVisible = true;
                }
                if (sts.HasValue) {
                    var (po, co, oh) = sts.Value;
                    paperStatTxt.Text = po ? "⚠ Paper out" : co ? "⚠ Cover open" : oh ? "⚠ Overheat" : "● Paper OK";
                    paperStatTxt.Foreground = (po || co || oh) ? T.Orange : T.Green;
                }
            });
        }
        async Task<bool> ConnectTo(string addr) {
            statusTxt.Text = "● Connecting..."; statusTxt.Foreground = T.Orange;
            connectBtn.Content = "Connecting...";
            await state.Connect(addr, TX_UUID, RX_UUID, Log, data => { ApplyRx(data); return Task.CompletedTask; });
            settings.LastDevice = addr; settings.Save();
            SetConnected(true);
            await Task.Delay(200);
            await state.Send(Cmd.SetDensity(settings.Density - 1)); Log($"Density → level {settings.Density}");
            await Task.Delay(100);
            await state.Send(Cmd.GetBattery());
            await Task.Delay(100);
            await state.Send(Cmd.GetStatus());
            await Task.Delay(100);
            await state.Send(Cmd.GetInfo());
            return true;
        }
        async Task ConnectFlow(bool forcePick) {
            if (state.IsConnected) { state.Disconnect(); SetConnected(false); return; }
            connectBtn.IsEnabled = false; chooseBtn.IsEnabled = false;
            try {
                if (!forcePick && settings.LastDevice != null) {
                    try { await ConnectTo(settings.LastDevice); return; }
                    catch (Exception ex) { Log($"Reconnect failed ({ex.Message}); choose a device."); state.Disconnect(); }
                }
                connectBtn.Content = "Choose a device...";
                var addr = await PickDevice(win, state, Log);
                if (addr == null) { SetConnected(false); return; }
                await ConnectTo(addr);
            } catch (Exception ex) {
                SetConnected(false, "● Connect failed", T.Red);
                Log($"ERROR: {ex.Message}");
            } finally { connectBtn.IsEnabled = true; chooseBtn.IsEnabled = true; }
        }
        connectBtn.Click += async (_, _) => await ConnectFlow(false);
        chooseBtn.Click += async (_, _) => await ConnectFlow(true);
        SetConnected(false);

        // Keep battery/paper status fresh while connected and idle.
        var poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        poll.Tick += async (_, _) => {
            if (!state.IsConnected || printing) return;
            try { await state.Send(Cmd.GetBattery()); await Task.Delay(100); await state.Send(Cmd.GetStatus()); } catch { }
        };
        poll.Start();

        // Preview
        var previewScroll = new ScrollViewer { Background = T.BG, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(previewScroll, 1); Grid.SetRow(previewScroll, 0); root.Children.Add(previewScroll);
        previewScroll.Content = canvasRef;

        // Splitter
        var splitter = new GridSplitter { Background = T.Border, ResizeDirection = GridResizeDirection.Rows, HorizontalAlignment = HorizontalAlignment.Stretch };
        Grid.SetColumn(splitter, 1); Grid.SetRow(splitter, 1); root.Children.Add(splitter);

        // Console
        var consoleBorder = new Border { Background = T.Panel, BorderBrush = T.Border, BorderThickness = new Thickness(1,0,0,0) };
        Grid.SetColumn(consoleBorder, 1); Grid.SetRow(consoleBorder, 2); root.Children.Add(consoleBorder);
        var consoleDock = new DockPanel(); consoleBorder.Child = consoleDock;
        var copyLog = new Button { Content = "Copy", Height = 22, FontSize = 10, Padding = new Thickness(8,0), Background = T.Surface, Foreground = T.TextDim, BorderBrush = T.Border, Margin = new Thickness(0,0,4,0) };
        var clearLog = new Button { Content = "Clear", Height = 22, FontSize = 10, Padding = new Thickness(8,0), Background = T.Surface, Foreground = T.TextDim, BorderBrush = T.Border, Margin = new Thickness(0,0,6,0) };
        copyLog.Click += async (_, _) => { if (win.Clipboard != null) await win.Clipboard.SetTextAsync(string.Join("\n", log)); };
        clearLog.Click += (_, _) => { log.Clear(); logBox.Text = ""; };
        var consoleHead = new Grid { Height = 28 };
        consoleHead.Children.Add(new TextBlock { Text = "CONSOLE", FontSize = 10, Foreground = T.TextDim, FontWeight = FontWeight.SemiBold, Margin = new Thickness(8,0), VerticalAlignment = VerticalAlignment.Center });
        consoleHead.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Children = { copyLog, clearLog } });
        DockPanel.SetDock(consoleHead, Dock.Top);
        consoleDock.Children.Add(consoleHead);
        consoleDock.Children.Add(logBox);

        void Refresh() {
            canvasRef.Children.Clear();
            using var mono = Render(state, (int)threshSlider.Value, ditherChk.IsChecked == true);
            int h = mono.Height;
            // Preview is the exact 1-bit output that will be sent (dark = burned dot).
            using var preview = new SkiaSharp.SKBitmap(PaperW, h, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Opaque);
            for (int y = 0; y < h; y++) for (int x = 0; x < PaperW; x++)
                preview.SetPixel(x, y, mono.GetPixel(x, y).Red < 128 ? new SkiaSharp.SKColor(20, 20, 20) : new SkiaSharp.SKColor(250, 250, 245));
            using var pimg = SkiaSharp.SKImage.FromBitmap(preview);
            using var penc = pimg.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            using var pms = new MemoryStream(penc.ToArray());
            var paper = new Border { BorderBrush = T.Border, BorderThickness = new Thickness(1), Child = new Image { Source = new Bitmap(pms), Width = PaperW, Height = h } };
            Canvas.SetLeft(paper, 16); Canvas.SetTop(paper, 16); canvasRef.Children.Add(paper);
            var dimLabel = new TextBlock { Text = $"{PaperW} dots × {h} rows  (48 mm × {h / 8.0:0.#} mm @ 200 DPI)", Foreground = T.TextDim, FontSize = 10 };
            Canvas.SetLeft(dimLabel, 16); Canvas.SetTop(dimLabel, h + 24); canvasRef.Children.Add(dimLabel);
            canvasRef.Width = PaperW + 32; canvasRef.Height = h + 48;
        }

        // Print
        printBtn.Click += async (_, _) => {
            if (!state.IsConnected) { await ShowMsg(win, "Connect printer first"); return; }
            if (state.Source == null && state.Texts.Count == 0) { await ShowMsg(win, "Nothing to print — add an image or text first."); return; }
            printing = true;
            printBtn.IsEnabled = false; printBtn.Content = "Checking printer..."; printProgress.Value = 0; printProgress.IsVisible = true;
            try {
                // Ask the printer for fresh status; refuse only if it reports a fault (a missing reply never blocks printing).
                state.LastStatus = null;
                await state.Send(Cmd.GetStatus());
                for (int i = 0; i < 10 && state.LastStatus == null; i++) await Task.Delay(100);
                if (state.LastStatus is var (po, co, oh) && (po || co || oh)) {
                    var why = po ? "The printer is out of paper." : co ? "The printer cover is open." : "The printer is overheated — let it cool down.";
                    Log($"Print blocked: {why}");
                    await ShowMsg(win, why);
                    return;
                }
                printBtn.Content = "Printing...";
                using var mono = Render(state, (int)threshSlider.Value, ditherChk.IsChecked == true);
                int h = mono.Height, bpr = PaperW / 8;
                var escBits = new byte[bpr * h];
                for (int y = 0; y < h; y++) for (int x = 0; x < PaperW; x++)
                    if (mono.GetPixel(x, y).Red < 128) escBits[y * bpr + x / 8] |= (byte)(0x80 >> (x % 8));
                await state.Send(Cmd.PrintModeImg());
                await Task.Delay(50);
                await state.SendImage(escBits, bpr, h, p => Dispatcher.UIThread.Post(() => printProgress.Value = p));
                Log($"Print complete — {PaperW}×{h}px");
                statusTxt.Text = "● Printed"; statusTxt.Foreground = T.Green;
            } catch (Exception ex) {
                Log($"Print ERROR: {ex.Message}");
                await ShowMsg(win, $"Print error: {ex.Message}");
            }
            finally { printing = false; printProgress.IsVisible = false; printBtn.IsEnabled = true; printBtn.Content = "PRINT"; }
        };

        Refresh();
        return win;
    }

    // Live device picker: opens immediately, lists devices as the scan finds them, returns the chosen id (or null).
    static async Task<string?> PickDevice(Window win, PrinterState state, Action<string> log) {
        var devices = new Dictionary<string, (string name, int rssi)>();
        var list = new ListBox { Background = T.BG, BorderBrush = T.Border, BorderThickness = new Thickness(1), MinHeight = 180 };
        var scanTxt = new TextBlock { Text = "Scanning for Bluetooth devices...", Foreground = T.TextDim, FontSize = 11 };
        var bar = new ProgressBar { IsIndeterminate = true, Height = 3, Foreground = T.Accent };
        var hideUnnamed = new CheckBox { Content = "Hide unnamed devices", IsChecked = true, FontSize = 12, Foreground = T.Text };
        var rescan = new Button { Content = "Rescan", Width = 90, Height = 30, HorizontalContentAlignment = HorizontalAlignment.Center, Background = T.Surface, Foreground = T.Text, BorderBrush = T.Border };
        var cancel = new Button { Content = "Cancel", Width = 90, Height = 30, HorizontalContentAlignment = HorizontalAlignment.Center, Background = T.Surface, Foreground = T.Text, BorderBrush = T.Border };
        var connect = new Button { Content = "Connect", Width = 100, Height = 30, IsEnabled = false, HorizontalContentAlignment = HorizontalAlignment.Center, Background = T.Accent, Foreground = Brushes.White };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { rescan, cancel, connect } };
        var body = new StackPanel { Margin = new Thickness(16), Spacing = 10, Children = {
            new TextBlock { Text = "Select your printer", FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = T.Text },
            scanTxt, bar, list, hideUnnamed, buttons } };
        var dlg = new Window { Title = "Connect to Printer", Width = 420, SizeToContent = SizeToContent.Height, CanResize = false, Background = T.Panel, Foreground = T.Text, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = body };

        string? result = null;
        string? selectedId = null;
        void Rebuild() {
            var items = devices
                .Where(d => hideUnnamed.IsChecked != true || d.Value.name.Length > 0)
                .OrderByDescending(d => d.Value.name.Contains("print", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(d => d.Value.name.Length > 0)
                .ThenByDescending(d => d.Value.rssi)
                .Select(d => new ListBoxItem {
                    Tag = d.Key,
                    Content = new StackPanel { Margin = new Thickness(4, 4), Children = {
                        new TextBlock { Text = d.Value.name.Length > 0 ? d.Value.name : "Unnamed device", FontSize = 13, Foreground = d.Value.name.Length > 0 ? T.Text : T.TextDim,
                            FontWeight = d.Value.name.Contains("print", StringComparison.OrdinalIgnoreCase) ? FontWeight.SemiBold : FontWeight.Normal },
                        new TextBlock { Text = d.Value.rssi != 0 ? $"Signal {d.Value.rssi} dBm" : "Signal unknown", FontSize = 10, Foreground = T.TextDim } } }
                }).ToList();
            list.ItemsSource = items;
            var keep = items.FirstOrDefault(i => (string?)i.Tag == selectedId)
                       ?? items.FirstOrDefault(i => devices[(string)i.Tag!].name.Contains("print", StringComparison.OrdinalIgnoreCase));
            if (keep != null) list.SelectedItem = keep;
        }
        list.SelectionChanged += (_, _) => { selectedId = (list.SelectedItem as ListBoxItem)?.Tag as string; connect.IsEnabled = selectedId != null; };
        hideUnnamed.IsCheckedChanged += (_, _) => Rebuild();
        void Accept() { if (selectedId != null) { result = selectedId; dlg.Close(); } }
        connect.Click += (_, _) => Accept();
        list.DoubleTapped += (_, _) => Accept();
        cancel.Click += (_, _) => dlg.Close();

        System.Threading.CancellationTokenSource? cts = null;
        async Task Scan() {
            cts?.Cancel(); cts = new System.Threading.CancellationTokenSource();
            var token = cts.Token;
            rescan.IsEnabled = false; bar.IsVisible = true; scanTxt.Text = "Scanning for Bluetooth devices...";
            await state.ScanDevices(log, (id, name, rssi) => Dispatcher.UIThread.Post(() => { devices[id] = (name, rssi); Rebuild(); }), token);
            if (token.IsCancellationRequested) return;
            bar.IsVisible = false; rescan.IsEnabled = true;
            scanTxt.Text = devices.Count == 0 ? "No devices found. Make sure the printer is on and in range, then Rescan." : $"Scan finished — {devices.Count} device(s).";
        }
        rescan.Click += async (_, _) => { devices.Clear(); selectedId = null; Rebuild(); await Scan(); };
        dlg.Closed += (_, _) => cts?.Cancel();
        dlg.Opened += async (_, _) => await Scan();
        await dlg.ShowDialog(win);
        return result;
    }

    // Composes the image (scaled to 384 dots) then text blocks top-to-bottom and returns a bitmap
    // whose pixels are strictly 0 (burn) or 255 (blank). Shared by preview and print so they match.
    static SkiaSharp.SKBitmap Render(PrinterState st, int threshold, bool dither) {
        const int W = 384, Pad = 8;
        SkiaSharp.SKBitmap? scaled = null;
        if (st.Source != null) {
            int ih = Math.Max(1, (int)Math.Round(st.Source.Height * (double)W / st.Source.Width));
            scaled = st.Source.Resize(new SkiaSharp.SKImageInfo(W, ih), new SkiaSharp.SKSamplingOptions(SkiaSharp.SKFilterMode.Linear, SkiaSharp.SKMipmapMode.Linear));
        }
        int imgRows = scaled?.Height ?? 0;
        var tf = SkiaSharp.SKTypeface.FromFamilyName("Segoe UI");
        var tfBold = SkiaSharp.SKTypeface.FromFamilyName("Segoe UI", SkiaSharp.SKFontStyle.Bold);
        var blocks = new List<(TextLayer t, string[] lines, SkiaSharp.SKFont font, float lh)>();
        int height = imgRows;
        foreach (var t in st.Texts) {
            var font = new SkiaSharp.SKFont(t.Bold ? tfBold : tf, t.Size);
            var lines = WrapText(t.Text, font, W - Pad * 2);
            float lh = font.Spacing;
            blocks.Add((t, lines, font, lh));
            height += Pad + (int)Math.Ceiling(lh * lines.Length);
        }
        height = Math.Max(height + (blocks.Count > 0 ? Pad : 0), 64);
        var gray = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(W, height, SkiaSharp.SKColorType.Gray8, SkiaSharp.SKAlphaType.Opaque));
        using (var surf = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(W, height, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul))) {
            var c = surf.Canvas; c.Clear(SkiaSharp.SKColors.White);
            int y = 0;
            if (scaled != null) { c.DrawBitmap(scaled, 0, 0); y = imgRows; }
            using var paint = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.Black, IsAntialias = true };
            foreach (var (t, lines, font, lh) in blocks) {
                y += Pad;
                foreach (var ln in lines) {
                    float w = font.MeasureText(ln);
                    float x = t.Align == 1 ? (W - w) / 2 : t.Align == 2 ? W - Pad - w : Pad;
                    c.DrawText(ln, x, y - font.Metrics.Ascent, SkiaSharp.SKTextAlign.Left, font, paint);
                    y += (int)Math.Ceiling(lh);
                }
                font.Dispose();
            }
            using var snap = surf.Snapshot();
            if (!snap.ReadPixels(gray.Info, gray.GetPixels(), gray.RowBytes, 0, 0)) throw new Exception("Render failed");
        }
        scaled?.Dispose();
        // Threshold, with error diffusion on the image rows only so text stays crisp.
        var raw = gray.GetPixelSpan();
        var buf = new float[W * height];
        for (int i = 0; i < buf.Length; i++) buf[i] = raw[i];
        for (int yy = 0; yy < height; yy++) for (int xx = 0; xx < W; xx++) {
            int i = yy * W + xx;
            float old = buf[i];
            float nw = old < threshold ? 0 : 255;
            if (dither && yy < imgRows) {
                float err = old - nw;
                if (xx + 1 < W) buf[i + 1] += err * 7 / 16;
                if (yy + 1 < height) {
                    if (xx > 0) buf[i + W - 1] += err * 3 / 16;
                    buf[i + W] += err * 5 / 16;
                    if (xx + 1 < W) buf[i + W + 1] += err * 1 / 16;
                }
            }
            bool black = nw == 0;
            if (st.Invert) black = !black;
            raw[i] = black ? (byte)0 : (byte)255;
        }
        return gray;
    }

    static string[] WrapText(string text, SkiaSharp.SKFont font, float maxW) {
        var result = new List<string>();
        foreach (var para in text.Replace("\r", "").Split('\n')) {
            var cur = "";
            foreach (var word in para.Split(' ')) {
                var test = cur.Length == 0 ? word : cur + " " + word;
                if (cur.Length == 0 || font.MeasureText(test) <= maxW) cur = test;
                else { result.Add(cur); cur = word; }
            }
            result.Add(cur);
        }
        return result.ToArray();
    }

    static async Task ShowMsg(Window win, string msg) {
        var ok = new Button { Content = "OK", Width = 80, HorizontalAlignment = HorizontalAlignment.Right, HorizontalContentAlignment = HorizontalAlignment.Center, Background = T.Accent, Foreground = Brushes.White };
        var body = new StackPanel { Margin = new Thickness(20), Spacing = 16, Children = { new TextBlock { Text = msg, TextWrapping = TextWrapping.Wrap, Foreground = T.Text }, ok } };
        var dlg = new Window { Title = "ThermalPrint", Width = 380, SizeToContent = SizeToContent.Height, CanResize = false, Background = T.Panel, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = body };
        ok.Click += (_, _) => dlg.Close();
        await dlg.ShowDialog(win);
    }
}

class TrayHelper {
    IntPtr _hWnd, _hIcon;
    TrayIcon.NOTIFYICONDATA _nid;
    Action _onToggle;
    Action _onExit;
    public bool InTray;

    public TrayHelper(Action onToggle, Action onExit) {
        _onToggle = onToggle; _onExit = onExit;
        _hWnd = App._mainWindow?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (_hWnd == IntPtr.Zero) return;
        _hIcon = LoadIconFromResource();
        if (_hIcon == IntPtr.Zero) _hIcon = LoadSystemIcon();
        _nid = new TrayIcon.NOTIFYICONDATA { cbSize = Marshal.SizeOf<TrayIcon.NOTIFYICONDATA>(), hWnd = _hWnd, uID = 1, uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP, uCallbackMessage = TrayIcon.WM_TRAYICON, hIcon = _hIcon, szTip = "ThermalPrint" };
        Shell_NotifyIcon(NIM_ADD, ref _nid);
        InTray = true;
        _wndProc = WndProc;
        _origWndProc = SetWindowLongPtr(_hWnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_wndProc));
    }

    const int NIF_MESSAGE = TrayIcon.NIF_MESSAGE, NIF_ICON = TrayIcon.NIF_ICON, NIF_TIP = TrayIcon.NIF_TIP;
    const int NIM_ADD = TrayIcon.NIM_ADD, NIM_MODIFY = TrayIcon.NIM_MODIFY, NIM_DELETE = TrayIcon.NIM_DELETE;

    delegate IntPtr WNDPROC(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    WNDPROC _wndProc = null!;
    IntPtr _origWndProc;
    const int GWLP_WNDPROC = -4;

    IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam) {
        if (msg == TrayIcon.WM_TRAYICON) {
            int wm = (int)lParam & 0xFFFF;
            if (wm == TrayIcon.WM_LBUTTONUP) _onToggle();
            else if (wm == TrayIcon.WM_RBUTTONUP) ShowContextMenu();
        }
        else if (msg == TrayIcon.WM_MEASUREITEM && wParam == IntPtr.Zero) {
            var mis = Marshal.PtrToStructure<MEASUREITEMSTRUCT>(lParam);
            if (mis.CtlType == 1) {
                mis.itemWidth = 150; mis.itemHeight = 28;
                Marshal.StructureToPtr(mis, lParam, false);
                return (IntPtr)1;
            }
        }
        else if (msg == TrayIcon.WM_DRAWITEM && wParam == IntPtr.Zero) {
            var dis = Marshal.PtrToStructure<DRAWITEMSTRUCT>(lParam);
            if (dis.CtlType == 1) DrawMenuItem(dis);
            return (IntPtr)1;
        }
        return CallWindowProc(_origWndProc, hWnd, msg, wParam, lParam);
    }

    void DrawMenuItem(DRAWITEMSTRUCT dis) {
        bool hover = (dis.itemState & ODS_SELECTED) != 0;
        bool isSep = (dis.itemState & ODS_DEFAULT) != 0;
        var bg = hover ? new IntPtr(0x00CC7A00) : new IntPtr(0x00282525);
        FillRect(dis.hDC, ref dis.rcItem, bg);
        if (isSep) {
            var sepRect = new RECT { Left = dis.rcItem.Left + 8, Top = dis.rcItem.Top + (dis.rcItem.Bottom - dis.rcItem.Top) / 2, Right = dis.rcItem.Right - 8, Bottom = dis.rcItem.Top + (dis.rcItem.Bottom - dis.rcItem.Top) / 2 + 1 };
            FillRect(dis.hDC, ref sepRect, new IntPtr(0x0046403F));
            return;
        }
        string text = dis.itemID switch { 1001 => "Show / Hide", 1002 => "Exit", _ => "" };
        SetBkMode(dis.hDC, 1);
        SetTextColor(dis.hDC, hover ? 0x00FFFFFF : 0x00D8D8D0);
        DrawText(dis.hDC, text, text.Length, ref dis.rcItem, DT_LEFT | DT_VCENTER | DT_SINGLELINE);
    }

    const int ODS_SELECTED = 0x0004, ODS_DEFAULT = 0x0020;
    const int DT_LEFT = 0, DT_VCENTER = 4, DT_SINGLELINE = 0x20;

    [StructLayout(LayoutKind.Sequential)]
    struct MEASUREITEMSTRUCT { public int CtlType; public int CtlID; public int itemID; public int itemWidth; public int itemHeight; public IntPtr itemData; }
    [StructLayout(LayoutKind.Sequential)]
    struct DRAWITEMSTRUCT { public int CtlType; public int CtlID; public int itemID; public int itemAction; public int itemState; public IntPtr hwndItem; public IntPtr hDC; public RECT rcItem; public IntPtr itemData; }

    [DllImport("user32.dll")] static extern int FillRect(IntPtr hDC, ref RECT lprc, IntPtr hbr);
    [DllImport("gdi32.dll")] static extern int SetBkMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] static extern int SetTextColor(IntPtr hdc, int color);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int DrawText(IntPtr hDC, string lpString, int nCount, ref RECT lpRect, uint uFormat);

    void ShowContextMenu() {
        SetForegroundWindow(_hWnd);
        var hMenu = CreatePopupMenu();
        AppendMenu(hMenu, MF_STRING | MFT_OWNERDRAW, (IntPtr)1001, "Show / Hide");
        SetMenuDefaultItem(hMenu, 1001, 0);
        AppendMenu(hMenu, MF_SEPARATOR, IntPtr.Zero, null);
        AppendMenu(hMenu, MF_STRING | MFT_OWNERDRAW, (IntPtr)1002, "Exit");
        var info = new MENUINFO { cbSize = Marshal.SizeOf<MENUINFO>(), fMask = MIM_BACKGROUND, hbrBack = CreateSolidBrush(RGB(0x25, 0x25, 0x28)) };
        SetMenuInfo(hMenu, ref info);
        GetCursorPos(out var pt);
        int cmd = TrackPopupMenu(hMenu, TPM_RIGHTBUTTON | TPM_RETURNCMD, pt.X, pt.Y, 0, _hWnd, IntPtr.Zero);
        DestroyMenu(hMenu);
        if (cmd == 1001) _onToggle();
        else if (cmd == 1002) _onExit();
    }

    public void Remove() {
        if (InTray) { Shell_NotifyIcon(NIM_DELETE, ref _nid); InTray = false; }
        if (_hIcon != IntPtr.Zero) { DestroyIcon(_hIcon); _hIcon = IntPtr.Zero; }
    }

    static IntPtr LoadIconFromResource() {
        try {
            using var st = App.AppIcon();
            if (st == null) return IntPtr.Zero;
            using var bmp = new System.Drawing.Bitmap(st);
            var icon = System.Drawing.Icon.FromHandle(bmp.GetHicon());
            return icon.Handle;
        } catch { return IntPtr.Zero; }
    }
    static IntPtr LoadSystemIcon() {
        try { return LoadIcon(IntPtr.Zero, IDI_APPLICATION); } catch { return IntPtr.Zero; }
    }

    const uint MF_STRING = 0x0000, MF_SEPARATOR = 0x0800, MFT_OWNERDRAW = 0x0100;
    const uint MIM_BACKGROUND = 0x00000002;
    static int RGB(int r, int g, int b) => (r | (g << 8) | (b << 16));
    static IntPtr IDI_APPLICATION = (IntPtr)32512;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MENUINFO { public int cbSize; public uint fMask; public uint dwStyle; public uint cyMax; public IntPtr hbrBack; public uint dwContextHelpID; public uint dwMenuData; }

    [DllImport("user32.dll")] static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    [DllImport("user32.dll")] static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool AppendMenu(IntPtr hMenu, uint uFlags, IntPtr uIDNewItem, string? lpNewItem);
    [DllImport("user32.dll")] static extern bool SetMenuDefaultItem(IntPtr hMenu, uint uIDNewItem, uint fByPos);
    [DllImport("user32.dll")] static extern int TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);
    [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr hMenu);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern void GetCursorPos(out TrayIcon.POINT lpPoint);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("user32.dll")] static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);
    [DllImport("user32.dll")] static extern bool SetMenuInfo(IntPtr hMenu, ref MENUINFO lpmi);
    [DllImport("gdi32.dll")] static extern IntPtr CreateSolidBrush(int crColor);
}
#endif

public class PrinterState {
#if GUI
    public SkiaSharp.SKBitmap? Source;
#endif
    public List<TextLayer> Texts = new();
    public bool Invert, IsConnected;
    public Action? ConnectionLost;
    public int? Battery;
    public (bool paperOut, bool coverOpen, bool overheat)? LastStatus;
    bool _lostHooked;
    ICharacteristic? _tx;
    IAdapter? _adapter;
    IDevice? _device;
    Action<string>? _log;
    Func<byte[], Task>? _onRx;
    const int MTU = 182;
    const string PRINTER_SVC = "49535343-fe7d-4ae5-8fa9-9fafd205e455";

    public void Clear() {
        Texts.Clear();
#if GUI
        Source?.Dispose(); Source = null;
#endif
    }
    public void Disconnect() {
        _tx = null; IsConnected = false;
        if (_device != null) _adapter?.DisconnectDeviceAsync(_device);
    }

    public async Task Send(byte[] cmd) {
        if (_tx == null) return;
        for (int i = 0; i < cmd.Length; i += MTU) {
            await _tx.WriteAsync(cmd[i..Math.Min(i + MTU, cmd.Length)]);
            if (cmd.Length > MTU) await Task.Delay(10);
        }
    }

    // Streams discovered devices to onFound(id, name, rssi) until the scan timeout or cancellation.
    public async Task ScanDevices(Action<string> log, Action<string, string, int> onFound, System.Threading.CancellationToken ct) {
        log("Scanning for Bluetooth devices...");
        try { var ble = CrossBluetoothLE.Current; log($"BLE available: {ble.IsAvailable}, state: {ble.State}"); if (!ble.IsAvailable || ble.State != BluetoothState.On) { log("Bluetooth is off or unavailable."); return; } }
        catch (Exception ex) { log($"BLE init error: {ex.Message}"); return; }
        _adapter = CrossBluetoothLE.Current.Adapter;
        _adapter.ScanTimeout = 10000;
        _adapter.ScanMode = ScanMode.LowLatency;
        void OnFound(object? s, DeviceEventArgs a) => onFound(a.Device.Id.ToString(), a.Device.Name ?? "", a.Device.Rssi);
        _adapter.DeviceDiscovered += OnFound;
        try { await _adapter.StartScanningForDevicesAsync((ScanFilterOptions?)null, null, false, ct); }
        catch (Exception ex) { if (!ct.IsCancellationRequested) log($"Scan error: {ex.Message}"); }
        finally {
            _adapter.DeviceDiscovered -= OnFound;
            try { await _adapter.StopScanningForDevicesAsync(); } catch { }
        }
        log("Scan complete.");
    }

    public async Task Connect(string deviceId, string txUuid, string rxUuid) {
        try { await Connect(deviceId, txUuid, rxUuid, _ => { }, _ => Task.CompletedTask); }
        catch { }
    }
    public async Task Connect(string deviceId, string txUuid, string rxUuid, Action<string> log, Func<byte[], Task> onRx) {
        _log = log; _onRx = onRx;
        _adapter ??= CrossBluetoothLE.Current.Adapter;
        log("Connecting...");
        if (!_lostHooked) {
            _lostHooked = true;
            _adapter!.DeviceConnectionLost += (_, _) => { IsConnected = false; _tx = null; log("Connection lost."); ConnectionLost?.Invoke(); };
        }
        IDevice? device = null;
        try { device = await _adapter.ConnectToKnownDeviceAsync(Guid.Parse(deviceId), new ConnectParameters(false, true)); } catch { }
        if (device == null) throw new Exception("Device not found");
        _device = device;
        log("Connected, negotiating MTU...");
        try { var mtu = await device.RequestMtuAsync(185); log($"MTU negotiated: {mtu}"); } catch (Exception ex) { log($"MTU: {ex.Message}"); }
        await Task.Delay(500);
        var allSvcs = await device.GetServicesAsync();
        log($"Services: {string.Join(", ", allSvcs.Select(s => s.Id.ToString()))}");
        var svc = allSvcs.FirstOrDefault(s => s.Id == Guid.Parse(PRINTER_SVC)) ?? allSvcs.FirstOrDefault();
        if (svc == null) throw new Exception("No services found");
        log($"Using service: {svc.Id}");
        var allChars = await svc.GetCharacteristicsAsync();
        foreach (var c in allChars) log($"  CHAR: {c.Id} W={c.CanWrite} R={c.CanRead} N={c.CanUpdate}");
        _tx = allChars.FirstOrDefault(c => c.Id == Guid.Parse(txUuid)) ?? allChars.FirstOrDefault(c => c.CanWrite);
        if (_tx == null) throw new Exception("No writable characteristic found");
        var rx = allChars.FirstOrDefault(c => c.Id == Guid.Parse(rxUuid)) ?? allChars.FirstOrDefault(c => c.CanUpdate);
        if (rx != null && rx.CanUpdate) {
            rx.ValueUpdated += async (_, e) => {
                log("RX fired");
                if (e.Characteristic.Value == null) { log("RX value null"); return; }
                log(Cmd.ParseResponse(e.Characteristic.Value));
                if (_onRx != null) await _onRx(e.Characteristic.Value);
            };
            await rx.StartUpdatesAsync();
        }
        IsConnected = true;
        await _tx.WriteAsync(Cmd.EscInit());
        log("Connected. Printer initialized.");
    }

    public async Task SendImage(byte[] packedBits, int bytesPerRow, int height, Action<double>? progress = null) {
        if (_tx == null) throw new Exception("Not connected");
        var buf = new List<byte>();
        buf.AddRange(Cmd.EscInit());
        buf.AddRange(new byte[] { 0x1D,0x76,0x30,0x00 });
        buf.Add((byte)(bytesPerRow & 0xFF)); buf.Add((byte)(bytesPerRow >> 8));
        buf.Add((byte)(height & 0xFF)); buf.Add((byte)(height >> 8));
        buf.AddRange(packedBits);
        buf.AddRange(new byte[] { 0x1B,0x4A,0xA0 });
        var data = buf.ToArray();
        _log?.Invoke($"TX: {data.Length} bytes, {(data.Length + MTU - 1) / MTU} chunks");
        for (int i = 0; i < data.Length; i += MTU) {
            if (_tx == null || !IsConnected) throw new Exception("Connection lost during print");
            await _tx.WriteAsync(data[i..Math.Min(i + MTU, data.Length)]);
            await Task.Delay(20);
            progress?.Invoke(Math.Min(1.0, (double)(i + MTU) / data.Length));
        }
    }
}

public record TextLayer(string Text, int Size, int Align, bool Bold);
