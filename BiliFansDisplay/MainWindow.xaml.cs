using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;
using DrawingIcon = System.Drawing.Icon;
using WinForms = System.Windows.Forms;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace BiliFansDisplay;

public sealed partial class MainWindow : Window
{
    private const int WindowWidth = 460;
    private const int WindowHeight = 240;
    private const string AppDataFolderName = "BiliFansDisplay";
    private const string ConfigFileName = "config.json";
    private const string HistoryFolderName = "history";
    private const string LegacyHistoryFileName = "fans-history.json";
    private static readonly TimeSpan HistoryRetention = TimeSpan.FromDays(3);
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpFrameChanged = 0x0020;
    private const long WsCaption = 0x00C00000;
    private const long WsMaximizeBox = 0x00010000;
    private const long WsMinimizeBox = 0x00020000;
    private const long WsSysMenu = 0x00080000;
    private const long WsThickFrame = 0x00040000;
    private const long WsExToolWindow = 0x00000080;
    private const long WsExAppWindow = 0x00040000;

    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static string AppDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppDataFolderName);

    private static string ConfigPath => Path.Combine(AppDataDirectory, ConfigFileName);

    private static string HistoryDirectory => Path.Combine(AppDataDirectory, HistoryFolderName);

    private static string LegacyHistoryPath => Path.Combine(AppDataDirectory, LegacyHistoryFileName);

    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMinutes(3) };

    private List<FanRecord> _history = [];
    private DesktopAcrylicController? _acrylicController;
    private DesktopAcrylicKind _acrylicKind = DesktopAcrylicKind.Default;
    private bool _isDragging;
    private bool _isRefreshing;
    private long? _uid;
    private string? _historyPath;
    private PointInt32 _dragStartCursor;
    private PointInt32 _dragStartWindow;
    private SystemBackdropConfiguration? _backdropConfiguration;
    private DrawingIcon? _trayIconImage;
    private WinForms.ContextMenuStrip? _trayContextMenu;
    private WinForms.NotifyIcon? _trayIcon;

    public MainWindow()
    {
        InitializeComponent();

        SetWindowIcon();
        AppWindow.Resize(new SizeInt32(WindowWidth, WindowHeight));

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        Closed += MainWindow_Closed;
        RemoveWindowChrome();
        InitializeTrayIcon();
        InitializeAcrylicBackdrop();

        _refreshTimer.Tick += RefreshTimer_Tick;
        InitializeStoredUid();
    }

    private static HttpClient CreateHttpClient()
    {
        var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 BiliFansDisplay/1.0");
        return httpClient;
    }

    private void SetWindowIcon()
    {
        string iconPath = GetAppIconPath();
        if (File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }
    }

    private static string GetAppIconPath()
    {
        return Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
    }

    private void InitializeTrayIcon()
    {
        string iconPath = GetAppIconPath();
        if (!File.Exists(iconPath))
        {
            return;
        }

        _trayIconImage = new DrawingIcon(iconPath);
        _trayContextMenu = new WinForms.ContextMenuStrip();

        var showItem = new WinForms.ToolStripMenuItem("显示窗口");
        showItem.Click += TrayShow_Click;

        var hideItem = new WinForms.ToolStripMenuItem("隐藏窗口");
        hideItem.Click += TrayHide_Click;

        var refreshItem = new WinForms.ToolStripMenuItem("立刻刷新");
        refreshItem.Click += TrayRefresh_Click;

        var exitItem = new WinForms.ToolStripMenuItem("退出");
        exitItem.Click += TrayExit_Click;

        _trayContextMenu.Items.Add(showItem);
        _trayContextMenu.Items.Add(hideItem);
        _trayContextMenu.Items.Add(refreshItem);
        _trayContextMenu.Items.Add(new WinForms.ToolStripSeparator());
        _trayContextMenu.Items.Add(exitItem);

        _trayIcon = new WinForms.NotifyIcon
        {
            ContextMenuStrip = _trayContextMenu,
            Icon = _trayIconImage,
            Text = "BiliFansDisplay",
            Visible = true
        };
        _trayIcon.DoubleClick += TrayShow_Click;
    }

    private void TrayShow_Click(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            AppWindow.Show();
            Activate();
        });
    }

    private void TrayHide_Click(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() => AppWindow.Hide());
    }

    private void TrayRefresh_Click(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(async () => await RefreshNowAsync());
    }

    private void TrayExit_Click(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(Close);
    }

    private void InitializeStoredUid()
    {
        long? storedUid = LoadConfiguredUid();

        if (storedUid is null)
        {
            ShowSetupView();
            return;
        }

        InitializeUid(storedUid.Value);
    }

    private void ShowSetupView()
    {
        _refreshTimer.Stop();
        _uid = null;
        _historyPath = null;
        _history = [];
        FansText.Text = "--";
        SetupErrorText.Text = string.Empty;
        SetupView.Visibility = Visibility.Visible;
        StatsView.Visibility = Visibility.Collapsed;
        UidTextBox.Focus(FocusState.Programmatic);
    }

    private void ShowStatsView()
    {
        SetupView.Visibility = Visibility.Collapsed;
        StatsView.Visibility = Visibility.Visible;
    }

    private void InitializeUid(long uid)
    {
        _uid = uid;
        _historyPath = GetHistoryPath(uid);

        MigrateLegacyHistory(uid);
        _history = LoadHistoryFromPath(_historyPath);

        bool removedExpiredRecords = RemoveExpiredHistory();
        if (removedExpiredRecords)
        {
            SaveHistory();
        }

        ShowStatsView();
        FanRecord? latestRecord = _history.OrderBy(record => record.Timestamp).LastOrDefault();
        if (latestRecord is not null)
        {
            UpdateDisplay(latestRecord.Follower, latestRecord.Timestamp);
        }

        StartRefreshLoop();
    }

    private async void StartRefreshLoop()
    {
        if (!_refreshTimer.IsEnabled)
        {
            _refreshTimer.Start();
        }

        await RefreshFansAsync();
    }

    private void SetupConfirm_Click(object sender, RoutedEventArgs e)
    {
        string uidText = UidTextBox.Text.Trim();
        if (!long.TryParse(uidText, NumberStyles.None, CultureInfo.InvariantCulture, out long uid) || uid <= 0)
        {
            SetupErrorText.Text = "请输入有效的 UID";
            return;
        }

        try
        {
            SaveConfig(uid);
            InitializeUid(uid);
        }
        catch (Exception)
        {
            SetupErrorText.Text = "保存 UID 失败";
        }
    }

    private async void RefreshTimer_Tick(object? sender, object e)
    {
        await RefreshFansAsync();
    }

    private async void RefreshNow_Click(object sender, RoutedEventArgs e)
    {
        await RefreshNowAsync();
    }

    private async Task RefreshNowAsync()
    {
        _refreshTimer.Stop();
        await RefreshFansAsync();

        if (_uid is not null)
        {
            _refreshTimer.Start();
        }
    }

    private void AcrylicDefault_Click(object sender, RoutedEventArgs e)
    {
        ApplyAcrylicKind(DesktopAcrylicKind.Default);
    }

    private void AcrylicBase_Click(object sender, RoutedEventArgs e)
    {
        ApplyAcrylicKind(DesktopAcrylicKind.Base);
    }

    private void AcrylicThin_Click(object sender, RoutedEventArgs e)
    {
        ApplyAcrylicKind(DesktopAcrylicKind.Thin);
    }

    private async Task RefreshFansAsync()
    {
        if (_isRefreshing || _uid is null)
        {
            return;
        }

        _isRefreshing = true;
        long uid = _uid.Value;

        try
        {
            string requestUri = $"https://api.bilibili.com/x/relation/stat?vmid={uid.ToString(CultureInfo.InvariantCulture)}";
            using HttpResponseMessage response = await HttpClient.GetAsync(requestUri);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync();
            using JsonDocument document = await JsonDocument.ParseAsync(stream);
            JsonElement root = document.RootElement;

            if (root.GetProperty("code").GetInt32() == 0)
            {
                long follower = root.GetProperty("data").GetProperty("follower").GetInt64();
                DateTimeOffset timestamp = DateTimeOffset.UtcNow;

                _history.Add(new FanRecord(timestamp, follower));
                RemoveExpiredHistory();
                SaveHistory();
                UpdateDisplay(follower, timestamp);
            }
        }
        catch (Exception)
        {
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private void UpdateDisplay(long follower, DateTimeOffset timestamp)
    {
        FansText.Text = follower.ToString(CultureInfo.InvariantCulture);
        UpdateDeltaText(OneHourText, "1h", CalculateDelta(follower, timestamp, TimeSpan.FromHours(1)), true);
        UpdateDeltaText(ThreeHourText, "3h", CalculateDelta(follower, timestamp, TimeSpan.FromHours(3)), false);
        UpdateDeltaText(DayText, "24h", CalculateDelta(follower, timestamp, TimeSpan.FromHours(24)), false);
    }

    private DeltaResult CalculateDelta(long currentFollower, DateTimeOffset timestamp, TimeSpan period)
    {
        DateTimeOffset cutoff = timestamp - period;
        FanRecord? baseline = _history
            .Where(record => record.Timestamp >= cutoff && record.Timestamp < timestamp)
            .OrderBy(record => record.Timestamp)
            .FirstOrDefault();

        return baseline is null
            ? new DeltaResult(false, 0)
            : new DeltaResult(true, currentFollower - baseline.Follower);
    }

    private void UpdateDeltaText(TextBlock textBlock, string label, DeltaResult result, bool showWhenUnavailable)
    {
        if (!result.HasEnoughHistory && !showWhenUnavailable)
        {
            textBlock.Visibility = Visibility.Collapsed;
            return;
        }

        textBlock.Visibility = Visibility.Visible;
        textBlock.Text = $"{label} {FormatDelta(result.Delta)}";

        if (!result.HasEnoughHistory)
        {
            textBlock.Foreground = GetThemeBrush("TextFillColorTertiaryBrush", Color.FromArgb(255, 128, 128, 128));
            return;
        }

        textBlock.Foreground = result.Delta < 0
            ? GetThemeBrush("SystemFillColorCriticalBrush", Color.FromArgb(255, 196, 43, 28))
            : GetThemeBrush("SystemFillColorSuccessBrush", Color.FromArgb(255, 16, 124, 16));
    }

    private static string FormatDelta(long delta)
    {
        return delta >= 0 ? $"+{delta.ToString(CultureInfo.InvariantCulture)}" : delta.ToString(CultureInfo.InvariantCulture);
    }

    private static long? LoadConfiguredUid()
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                return null;
            }

            string json = File.ReadAllText(ConfigPath);
            AppConfig? config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
            return config?.Uid > 0 ? config.Uid : null;
        }
        catch
        {
            return null;
        }
    }

    private static void SaveConfig(long uid)
    {
        Directory.CreateDirectory(AppDataDirectory);
        var config = new AppConfig { Uid = uid };
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, JsonOptions));
    }

    private static string GetHistoryPath(long uid)
    {
        return Path.Combine(HistoryDirectory, $"{uid.ToString(CultureInfo.InvariantCulture)}.json");
    }

    private void MigrateLegacyHistory(long uid)
    {
        if (!File.Exists(LegacyHistoryPath))
        {
            return;
        }

        try
        {
            string targetPath = GetHistoryPath(uid);
            List<FanRecord> currentRecords = LoadHistoryFromPath(targetPath);
            List<FanRecord> legacyRecords = LoadHistoryFromPath(LegacyHistoryPath);
            List<FanRecord> mergedRecords = currentRecords
                .Concat(legacyRecords)
                .GroupBy(record => record.Timestamp.UtcTicks)
                .Select(group => group.First())
                .OrderBy(record => record.Timestamp)
                .ToList();

            SaveHistoryToPath(targetPath, mergedRecords);
            File.Delete(LegacyHistoryPath);
        }
        catch
        {
        }
    }

    private static List<FanRecord> LoadHistoryFromPath(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return [];
            }

            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<FanRecord>>(json, JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private bool RemoveExpiredHistory()
    {
        DateTimeOffset cutoff = DateTimeOffset.UtcNow - HistoryRetention;
        int originalCount = _history.Count;
        _history = _history
            .Where(record => record.Timestamp >= cutoff)
            .OrderBy(record => record.Timestamp)
            .ToList();

        return _history.Count != originalCount;
    }

    private void SaveHistory()
    {
        if (_historyPath is null)
        {
            return;
        }

        SaveHistoryToPath(_historyPath, _history);
    }

    private static void SaveHistoryToPath(string path, IReadOnlyCollection<FanRecord> records)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        List<FanRecord> orderedRecords = records.OrderBy(record => record.Timestamp).ToList();
        File.WriteAllText(path, JsonSerializer.Serialize(orderedRecords, JsonOptions));
    }

    private static Brush GetThemeBrush(string resourceKey, Color fallbackColor)
    {
        return Application.Current.Resources.TryGetValue(resourceKey, out object value) && value is Brush brush
            ? brush
            : new SolidColorBrush(fallbackColor);
    }

    private void InitializeAcrylicBackdrop()
    {
        if (!DesktopAcrylicController.IsSupported())
        {
            SystemBackdrop = new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop();
            return;
        }

        _backdropConfiguration = new SystemBackdropConfiguration { IsInputActive = true };
        SetBackdropTheme();

        Activated += MainWindow_Activated;
        RootGrid.ActualThemeChanged += RootGrid_ActualThemeChanged;

        _acrylicController = new DesktopAcrylicController { Kind = _acrylicKind };
        _acrylicController.AddSystemBackdropTarget(WinRT.CastExtensions.As<ICompositionSupportsSystemBackdrop>(this));
        _acrylicController.SetSystemBackdropConfiguration(_backdropConfiguration);
    }

    private void ApplyAcrylicKind(DesktopAcrylicKind kind)
    {
        _acrylicKind = kind;

        if (_acrylicController is not null)
        {
            _acrylicController.Kind = kind;
        }

        AcrylicDefaultItem.IsChecked = kind == DesktopAcrylicKind.Default;
        AcrylicBaseItem.IsChecked = kind == DesktopAcrylicKind.Base;
        AcrylicThinItem.IsChecked = kind == DesktopAcrylicKind.Thin;
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (_backdropConfiguration is not null)
        {
            _backdropConfiguration.IsInputActive = args.WindowActivationState != WindowActivationState.Deactivated;
        }
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.DoubleClick -= TrayShow_Click;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _trayContextMenu?.Dispose();
        _trayContextMenu = null;

        _trayIconImage?.Dispose();
        _trayIconImage = null;

        _acrylicController?.Dispose();
        _acrylicController = null;
    }

    private void RootGrid_ActualThemeChanged(FrameworkElement sender, object args)
    {
        SetBackdropTheme();
    }

    private void SetBackdropTheme()
    {
        if (_backdropConfiguration is null)
        {
            return;
        }

        _backdropConfiguration.Theme = RootGrid.ActualTheme switch
        {
            ElementTheme.Dark => SystemBackdropTheme.Dark,
            ElementTheme.Light => SystemBackdropTheme.Light,
            _ => SystemBackdropTheme.Default
        };
    }

    private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (IsSetupInputElement(e.OriginalSource))
        {
            return;
        }

        if (!e.GetCurrentPoint(RootGrid).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _isDragging = true;
        RootGrid.CapturePointer(e.Pointer);

        GetCursorPos(out CursorPoint cursorPoint);
        _dragStartCursor = new PointInt32(cursorPoint.X, cursorPoint.Y);
        _dragStartWindow = AppWindow.Position;
        e.Handled = true;
    }

    private void RootGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging || !e.GetCurrentPoint(RootGrid).Properties.IsLeftButtonPressed)
        {
            return;
        }

        GetCursorPos(out CursorPoint cursorPoint);
        AppWindow.Move(new PointInt32(
            _dragStartWindow.X + cursorPoint.X - _dragStartCursor.X,
            _dragStartWindow.Y + cursorPoint.Y - _dragStartCursor.Y));
        e.Handled = true;
    }

    private void RootGrid_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _isDragging = false;
        RootGrid.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private bool IsSetupInputElement(object originalSource)
    {
        if (SetupView.Visibility != Visibility.Visible)
        {
            return false;
        }

        DependencyObject? current = originalSource as DependencyObject;
        while (current is not null)
        {
            if (current is TextBox or Button)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private void RemoveWindowChrome()
    {
        nint hwnd = WindowNative.GetWindowHandle(this);
        nint style = GetWindowLongPtr(hwnd, GwlStyle);
        long styleValue = style.ToInt64();
        styleValue &= ~(WsCaption | WsSysMenu | WsMinimizeBox | WsMaximizeBox | WsThickFrame);
        SetWindowLongPtr(hwnd, GwlStyle, new nint(styleValue));

        nint exStyle = GetWindowLongPtr(hwnd, GwlExStyle);
        long exStyleValue = exStyle.ToInt64();
        exStyleValue &= ~WsExAppWindow;
        exStyleValue |= WsExToolWindow;
        SetWindowLongPtr(hwnd, GwlExStyle, new nint(exStyleValue));

        SetWindowPos(hwnd, nint.Zero, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoZOrder | SwpFrameChanged);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out CursorPoint lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPoint
    {
        public int X;
        public int Y;
    }

    private sealed record AppConfig
    {
        public long Uid { get; init; }
    }

    private sealed record FanRecord(DateTimeOffset Timestamp, long Follower);

    private sealed record DeltaResult(bool HasEnoughHistory, long Delta);
}
