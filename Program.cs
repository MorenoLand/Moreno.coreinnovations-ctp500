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
    public static byte[] FeedLines(int lines) {
        byte hi = (byte)(lines >> 8), lo = (byte)(lines & 0xFF);
        return new byte[] { 0x51,0x78,0xA1,0x00,0x02,0x00,hi,lo,0xFF };
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
        try { var ip = Path.Combine(AppContext.BaseDirectory, "thermalprint.png"); if (File.Exists(ip)) win.Icon = new WindowIcon(ip); } catch { }

        var winRoot = new Grid();
        winRoot.RowDefinitions.Add(new RowDefinition(32, GridUnitType.Pixel));
        winRoot.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star));
        win.Content = winRoot;

        // Title bar
        var titleBar = new Border { Background = T.Panel, Height = 32 };
        Grid.SetRow(titleBar, 0); winRoot.Children.Add(titleBar);
        var tbGrid = new Grid(); titleBar.Child = tbGrid;
        var titleCenter = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        try { var ip = Path.Combine(AppContext.BaseDirectory, "thermalprint.png"); if (File.Exists(ip)) titleCenter.Children.Add(new Image { Source = new Bitmap(ip), Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,6,0) }); } catch { }
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
        var paperStatTxt = new TextBlock { Text = "", Foreground = T.TextDim, FontSize = 10, Margin = new Thickness(0,0,0,4) };
        sb.Children.Add(paperStatTxt);
        var connectBtn = FlatBtn("Connect to Printer");
        sb.Children.Add(connectBtn);

        var printerControls = new StackPanel { IsVisible = false };
        sb.Children.Add(printerControls);

        // Density
        var densityLabel = new TextBlock { Text = "Density: 3 (Default)", Foreground = T.TextDim, FontSize = 10, Margin = new Thickness(0,8,0,2) };
        printerControls.Children.Add(densityLabel);
        var densitySlider = new Slider { Minimum = 1, Maximum = 5, Value = 3, IsSnapToTickEnabled = true, TickFrequency = 1, Height = 20 };
        printerControls.Children.Add(densitySlider);
        densitySlider.ValueChanged += async (_, _) => {
            int level = (int)densitySlider.Value - 1;
            string[] names = { "Lightest", "Light", "Default", "Dark", "Darkest" };
            densityLabel.Text = $"Density: {(int)densitySlider.Value} ({names[level]})";
            if (state.IsConnected) { await state.Send(Cmd.SetDensity(level)); Log($"Density → level {(int)densitySlider.Value}"); }
        };

        // Feed controls
        var feedRow = new Grid { Margin = new Thickness(0,6,0,0) };
        feedRow.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        feedRow.ColumnDefinitions.Add(new ColumnDefinition(60, GridUnitType.Pixel));
        feedRow.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        var feedBackBtn = FlatBtn("◀ Back");
        var feedFwdBtn = FlatBtn("Fwd ▶");
        var feedInput = new NumericUpDown { Value = 1, Minimum = 1, Maximum = 9999, Width = 56, Height = 30, FontSize = 11, Margin = new Thickness(2,4,2,0) };
        Grid.SetColumn(feedBackBtn, 0); Grid.SetColumn(feedInput, 1); Grid.SetColumn(feedFwdBtn, 2);
        feedRow.Children.Add(feedBackBtn); feedRow.Children.Add(feedInput); feedRow.Children.Add(feedFwdBtn);
        printerControls.Children.Add(feedRow);
        feedFwdBtn.Click += async (_, _) => { int n = (int)(feedInput.Value ?? 1); await state.Send(Cmd.FeedLines(n)); Log($"Feed {n} lines"); };
        feedBackBtn.Click += (_, _) => Log("Backoff not supported on this firmware");

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

        // PAPER
        Header("PAPER");
        var paperOpts = new ComboBox { Height = 30, Foreground = T.Text, Background = T.Surface, BorderBrush = T.Border, Margin = new Thickness(0,4,0,0), FontSize = 12 };
        paperOpts.ItemsSource = new[] { "57mm Roll (384px)", "Label 40×30mm", "Label 40×60mm", "Label 15mm width" };
        paperOpts.SelectedIndex = 0;
        sb.Children.Add(paperOpts);
        int paperW = 384, displayH = 200;
        (int w, int h)[] paperSizes = { (384, 0), (320, 240), (320, 480), (120, 0) };
        paperOpts.SelectionChanged += (_, _) => {
            var (w, h) = paperSizes[paperOpts.SelectedIndex];
            paperW = w; displayH = h > 0 ? h : (state.ImgH > 0 ? state.ImgH : 200);
            Refresh();
        };

        // IMAGE
        Header("IMAGE");
        var imgBtn = FlatBtn("Load Image"); sb.Children.Add(imgBtn);
        var imgName = new TextBlock { Foreground = T.TextDim, FontSize = 10, Margin = new Thickness(0,2,0,0), TextTrimming = TextTrimming.CharacterEllipsis };
        sb.Children.Add(imgName);
        imgBtn.Click += async (_, _) => {
            var files = await win.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
                Title = "Load Image",
                FileTypeFilter = new[] { new FilePickerFileType("Images") { Patterns = new[] { "*.jpg","*.jpeg","*.png","*.bmp","*.gif","*.tiff" } } }
            });
            if (files.Count > 0) {
                using var stream = await files[0].OpenReadAsync();
                var bmp = new Bitmap(stream);
                double scale = (double)paperW / bmp.PixelSize.Width;
                state.ImgW = paperW; state.ImgH = (int)(bmp.PixelSize.Height * scale);
                state.Image = bmp; displayH = state.ImgH;
                imgName.Text = files[0].Name; Refresh();
            }
        };

        // TEXT
        Header("TEXT");
        var textInput = new TextBox { Height = 30, Text = "Hello World", FontSize = 12, Padding = new Thickness(6,4), Margin = new Thickness(0,4,0,0) };
        sb.Children.Add(textInput);
        var textSize = new ComboBox { Height = 30, Foreground = T.Text, Background = T.Surface, BorderBrush = T.Border, Margin = new Thickness(0,4,0,0), FontSize = 12 };
        textSize.ItemsSource = new[] { "12","16","20","24","32","48" };
        textSize.SelectedIndex = 3; sb.Children.Add(textSize);
        var textAddBtn = FlatBtn("Add Text"); sb.Children.Add(textAddBtn);
        textAddBtn.Click += (_, _) => {
            if (int.TryParse((string?)textSize.SelectedItem, out int sz) && sz > 0)
                state.Texts.Add(new TextLayer(textInput.Text ?? "", sz, 0, 0));
            Refresh();
        };

        // ADJUST
        Header("ADJUST");
        var threshSlider = new Slider { Minimum = 0, Maximum = 100, Value = 50, Height = 20, Margin = new Thickness(0,2,0,6) };
        var threshLabel = new TextBlock { Text = "Threshold 50", Foreground = T.TextDim, FontSize = 11 };
        sb.Children.Add(threshLabel); sb.Children.Add(threshSlider);
        threshSlider.ValueChanged += (_, _) => { threshLabel.Text = $"Threshold {(int)threshSlider.Value}"; Refresh(); };
        var invertChk = new CheckBox { Content = "Invert output", IsChecked = false, FontSize = 12, Margin = new Thickness(0,2,0,0) };
        sb.Children.Add(invertChk);
        invertChk.IsCheckedChanged += (_, _) => { state.Invert = invertChk.IsChecked == true; Refresh(); };
        var bwChk = new CheckBox { Content = "Black & White preview", IsChecked = false, FontSize = 12, Margin = new Thickness(0,4,0,0) };
        sb.Children.Add(bwChk);
        bwChk.IsCheckedChanged += (_, _) => { state.BWPreview = bwChk.IsChecked == true; Refresh(); };

        // CLEAR / PRINT
        sb.Children.Add(new Border { Height = 1, Background = T.Border, Margin = new Thickness(0,16,0,8) });
        var clearBtn = FlatBtn("Clear All"); sb.Children.Add(clearBtn);
        clearBtn.Click += (_, _) => { state.Clear(); displayH = paperSizes[paperOpts.SelectedIndex].h > 0 ? paperSizes[paperOpts.SelectedIndex].h : 200; imgName.Text = ""; Refresh(); };
        var printBtn = new Button {
            Content = "PRINT", Height = 44, FontSize = 14, FontWeight = FontWeight.Bold,
            Background = T.Accent, Foreground = Brushes.White, BorderThickness = new Thickness(0),
            Margin = new Thickness(0,10,0,0), Cursor = Cursor.Parse("Hand")
        };
        sb.Children.Add(printBtn);

        // Connect handler
        connectBtn.Click += async (_, _) => {
            if (state.IsConnected) {
                state.Disconnect();
                statusTxt.Text = "● Disconnected"; statusTxt.Foreground = T.Orange;
                connectBtn.Content = "Connect to Printer";
                batteryTxt.Text = ""; paperStatTxt.Text = "";
                printerControls.IsVisible = false;
                return;
            }
            connectBtn.IsEnabled = false; connectBtn.Content = "Scanning...";
            try {
                var addr = await state.ScanForPrinter(PRINTER_SVC, Log);
                if (addr == null) {
                    statusTxt.Text = "● Not found"; statusTxt.Foreground = T.Red;
                    connectBtn.Content = "Connect to Printer"; connectBtn.IsEnabled = true; return;
                }
                connectBtn.Content = "Connecting...";
                await state.Connect(addr, TX_UUID, RX_UUID, Log, async (data) => {
                    var bat = Cmd.ParseBattery(data);
                    var sts = Cmd.ParseStatus(data);
                    await Dispatcher.UIThread.InvokeAsync(() => {
                        if (bat.HasValue) {
                            batteryTxt.Text = $"Battery: {bat.Value}%";
                            batteryTxt.Foreground = bat.Value < 20 ? T.Red : bat.Value < 50 ? T.Orange : T.Green;
                        }
                        if (sts.HasValue) {
                            var (po, co, oh) = sts.Value;
                            paperStatTxt.Text = po ? "⚠ Paper out" : co ? "⚠ Cover open" : oh ? "⚠ Overheat" : "● Paper OK";
                            paperStatTxt.Foreground = (po || co || oh) ? T.Orange : T.Green;
                        }
                    });
                });
                statusTxt.Text = "● Thermal Printer"; statusTxt.Foreground = T.Green;
                connectBtn.Content = "Disconnect";
                printerControls.IsVisible = true;
                await Task.Delay(200);
                await state.Send(Cmd.GetBattery());
                await Task.Delay(100);
                await state.Send(Cmd.GetStatus());
                await Task.Delay(100);
                await state.Send(Cmd.GetInfo());
            } catch (Exception ex) {
                statusTxt.Text = "● Failed"; statusTxt.Foreground = T.Red;
                connectBtn.Content = "Connect to Printer";
                Log($"ERROR: {ex.Message}");
            }
            connectBtn.IsEnabled = true;
        };

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
        DockPanel.SetDock(new TextBlock { Text = "CONSOLE", FontSize = 10, Foreground = T.TextDim, FontWeight = FontWeight.SemiBold, Margin = new Thickness(8,6,8,4) }, Dock.Top);
        consoleDock.Children.Add(new TextBlock { Text = "CONSOLE", FontSize = 10, Foreground = T.TextDim, FontWeight = FontWeight.SemiBold, Margin = new Thickness(8,6,8,4) });
        consoleDock.Children.Add(logBox);

        void Refresh() {
            canvasRef.Children.Clear();
            int h = displayH;
            for (int cy = 0; cy < (h + 7) / 8; cy++)
                for (int cx = 0; cx < (paperW + 7) / 8; cx++) {
                    var r = new Border { Width = 8, Height = 8, Background = (cx + cy) % 2 == 0 ? T.S(0x40,0x40,0x40) : T.S(0x60,0x60,0x60) };
                    Canvas.SetLeft(r, 16 + cx * 8); Canvas.SetTop(r, 16 + cy * 8);
                    canvasRef.Children.Add(r);
                }
            if (state.Image != null) {
                var img = new Image { Source = state.Image, Width = state.ImgW > 0 ? state.ImgW : paperW, Height = state.ImgH > 0 ? state.ImgH : h };
                Canvas.SetLeft(img, 16); Canvas.SetTop(img, 16); canvasRef.Children.Add(img);
            }
            foreach (var t in state.Texts) {
                var tb = new TextBlock { Text = t.Text, FontSize = t.Size, FontFamily = new FontFamily("Segoe UI"), Foreground = state.Invert ? Brushes.White : Brushes.Black };
                Canvas.SetLeft(tb, 16 + t.X); Canvas.SetTop(tb, 16 + t.Y); canvasRef.Children.Add(tb);
            }
            var dimLabel = new TextBlock { Text = $"{paperW}×{h}px", Foreground = T.TextDim, FontSize = 10 };
            Canvas.SetLeft(dimLabel, 16); Canvas.SetTop(dimLabel, h + 22); canvasRef.Children.Add(dimLabel);
            canvasRef.Width = paperW + 32; canvasRef.Height = h + 48;
        }

        // Print
        printBtn.Click += async (_, _) => {
            if (!state.IsConnected) { await ShowMsg(win, "Connect printer first"); return; }
            printBtn.IsEnabled = false; printBtn.Content = "Printing...";
            try {
                int h = displayH;
                var info = new SkiaSharp.SKImageInfo(paperW, h, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul);
                using var surface = SkiaSharp.SKSurface.Create(info);
                var skCanvas = surface.Canvas;
                skCanvas.Clear(state.Invert ? SkiaSharp.SKColors.Black : SkiaSharp.SKColors.White);
                if (state.Image != null) {
                    using var ms = new MemoryStream();
                    state.Image.Save(ms); ms.Position = 0;
                    var decoded = SkiaSharp.SKBitmap.Decode(ms);
                    double scale = (double)paperW / decoded.Width;
                    var resized = decoded.Resize(new SkiaSharp.SKImageInfo(paperW, (int)(decoded.Height * scale)), new SkiaSharp.SKSamplingOptions(SkiaSharp.SKFilterMode.Linear, SkiaSharp.SKMipmapMode.Linear));
                    skCanvas.DrawBitmap(resized, 0, 0);
                }
                foreach (var t in state.Texts) {
                    using var font = new SkiaSharp.SKFont(SkiaSharp.SKTypeface.FromFamilyName("Segoe UI"), t.Size);
                    using var paint = new SkiaSharp.SKPaint { Color = state.Invert ? SkiaSharp.SKColors.White : SkiaSharp.SKColors.Black, IsAntialias = true };
                    skCanvas.DrawText(t.Text, (float)t.X, (float)(t.Y + t.Size), SkiaSharp.SKTextAlign.Left, font, paint);
                }
                using var img = surface.Snapshot();
                using var data = img.Encode();
                var pixels = data.ToArray();
                int bpr = (paperW + 7) / 8;
                var escBits = new byte[bpr * h];
                for (int y = 0; y < h; y++) for (int x = 0; x < paperW; x++) {
                    int off = (y * paperW + x) * 4;
                    if (off + 2 < pixels.Length) {
                        int gray = (pixels[off + 2] * 30 + pixels[off + 1] * 59 + pixels[off] * 11) / 100;
                        bool black = state.Invert ? gray > (int)threshSlider.Value : gray < (int)threshSlider.Value;
                        if (black) escBits[y * bpr + x / 8] |= (byte)(0x80 >> (x % 8));
                    }
                }
                await state.Send(Cmd.PrintModeImg());
                await Task.Delay(50);
                await state.SendImage(escBits, bpr, h);
                statusTxt.Text = "● Printed"; statusTxt.Foreground = T.Green;
                Log($"Print complete — {bpr * 8}×{h}px");
            } catch (Exception ex) {
                Log($"Print ERROR: {ex.Message}");
                await ShowMsg(win, $"Print error: {ex.Message}");
            }
            printBtn.IsEnabled = true; printBtn.Content = "PRINT";
        };

        Refresh();
        return win;
    }

    static async Task ShowMsg(Window win, string msg) {
        var dlg = new Window { Title = "ThermalPrint", Width = 360, Height = 160, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new TextBlock { Text = msg, Margin = new Thickness(20), TextWrapping = TextWrapping.Wrap } };
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
        _hIcon = LoadIconFromFile(Path.Combine(AppContext.BaseDirectory, "thermalprint.png"));
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

    static IntPtr LoadIconFromFile(string path) {
        try {
            if (!File.Exists(path)) return IntPtr.Zero;
            using var bmp = new System.Drawing.Bitmap(path);
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
    public Bitmap? Image;
#endif
    public List<TextLayer> Texts = new();
    public bool Invert, IsConnected, BWPreview;
    public int ImgW, ImgH;
    ICharacteristic? _tx;
    IAdapter? _adapter;
    IDevice? _device;
    Action<string>? _log;
    Func<byte[], Task>? _onRx;
    const int MTU = 182;
    const string PRINTER_SVC = "49535343-fe7d-4ae5-8fa9-9fafd205e455";

    public void Clear() { Texts.Clear(); ImgW = 0; ImgH = 0; }
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

    public async Task<string?> ScanForPrinter(string svcUuid) {
        try { return await ScanForPrinter(svcUuid, _ => { }); }
        catch { return null; }
    }
    public async Task<string?> ScanForPrinter(string svcUuid, Action<string> log) {
        log("Scanning for thermal printers...");
        try { var ble = CrossBluetoothLE.Current; log($"BLE available: {ble.IsAvailable}, state: {ble.State}"); } catch (Exception ex) { log($"BLE init error: {ex.Message}"); return null; }
        _adapter = CrossBluetoothLE.Current.Adapter;
        _adapter.ScanTimeout = 8000;
        _adapter.ScanMode = ScanMode.LowLatency;
        var found = new ConcurrentBag<IDevice>();
        void OnFound(object? s, DeviceEventArgs a) {
            var name = string.IsNullOrEmpty(a.Device.Name) ? $"Bluetooth {a.Device.Id}" : a.Device.Name;
            log($"Found: {name} ({a.Device.Id})");
            found.Add(a.Device);
        }
        _adapter.DeviceDiscovered += OnFound;
        try { await _adapter.StartScanningForDevicesAsync(new ScanFilterOptions { ServiceUuids = new[] { Guid.Parse(svcUuid) } }); } catch { }
        if (found.Count == 0) {
            log("Trying unfiltered scan...");
            try { await _adapter.StartScanningForDevicesAsync(); } catch { }
        }
        await _adapter.StopScanningForDevicesAsync();
        _adapter.DeviceDiscovered -= OnFound;
        var all = found.ToList();
        var discovered = _adapter.DiscoveredDevices;
        if (discovered != null && discovered.Count > all.Count) all = discovered.ToList();
        log($"Scan complete — {all.Count} device(s)");
        var printer = all.FirstOrDefault(d => !string.IsNullOrEmpty(d.Name) && d.Name.Contains("Print", StringComparison.OrdinalIgnoreCase));
        printer ??= all.FirstOrDefault(d => !string.IsNullOrEmpty(d.Name));
        printer ??= all.FirstOrDefault();
        if (printer != null) { log($"Selected: {printer.Name ?? printer.Id.ToString()}"); return printer.Id.ToString(); }
        log("No devices found."); return null;
    }

    public async Task Connect(string deviceId, string txUuid, string rxUuid) {
        try { await Connect(deviceId, txUuid, rxUuid, _ => { }, _ => Task.CompletedTask); }
        catch { }
    }
    public async Task Connect(string deviceId, string txUuid, string rxUuid, Action<string> log, Func<byte[], Task> onRx) {
        _log = log; _onRx = onRx;
        log("Connecting...");
        _adapter!.DeviceConnectionLost += (_, _) => { IsConnected = false; _tx = null; log("Connection lost."); };
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

    public async Task SendImage(byte[] packedBits, int bytesPerRow, int height) {
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
            await _tx.WriteAsync(data[i..Math.Min(i + MTU, data.Length)]);
            await Task.Delay(20);
        }
    }
}

public record TextLayer(string Text, int Size, double X, double Y);
