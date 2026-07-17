using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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

namespace BiliFansDisplay;

public sealed partial class MainWindow : Window
{
    private const int WindowWidthAtReferenceDpi = 460;
    private const int WindowHeightAtReferenceDpi = 240;
    private const double ReferenceDpiScale = 1.5d;
    private const double DefaultDpi = 96d;
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

    private static readonly Regex YouTubeSubscriberRegex = new(
        @"(?<value>\d[\d,]*(?:\.\d+)?)\s*(?<suffix>K|M|B|thousand|million|billion)?[\s\u200E\u2068\u2069]*subscribers",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

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
    private readonly CounterSource _source;
    private readonly bool _ownsTray;

    private List<FanRecord> _history = [];
    private AppSettings? _settings;
    private DesktopAcrylicController? _acrylicController;
    private DesktopAcrylicKind _acrylicKind = DesktopAcrylicKind.Default;
    private bool _isDragging;
    private bool _isRefreshing;
    private bool _isClosing;
    private bool _secondaryWindowInitialized;
    private double _lastRasterizationScale;
    private int _sourceGeneration;
    private long? _uid;
    private string? _historyPath;
    private string? _sourceUrl;
    private MainWindow? _youtubeWindow;
    private PointInt32 _dragStartCursor;
    private PointInt32 _dragStartWindow;
    private SystemBackdropConfiguration? _backdropConfiguration;
    private XamlRoot? _xamlRoot;
    private DrawingIcon? _trayIconImage;
    private WinForms.ContextMenuStrip? _trayContextMenu;
    private WinForms.NotifyIcon? _trayIcon;
    private WinForms.ToolStripMenuItem? _showYouTubeItem;

    public MainWindow()
        : this(CounterSource.Bilibili, null, true)
    {
    }

    private MainWindow(CounterSource source, string? sourceUrl, bool ownsTray)
    {
        _source = source;
        _sourceUrl = sourceUrl;
        _ownsTray = ownsTray;

        InitializeComponent();

        Title = source == CounterSource.Bilibili ? "BiliFansDisplay" : "YouTube Fans Display";
        SourceNameItem.Text = source == CounterSource.Bilibili ? "Bilibili" : "YouTube";
        SetWindowIcon();
        ApplyInitialWindowSize();

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        Activated += MainWindow_Activated;
        Closed += MainWindow_Closed;
        RemoveWindowChrome();

        if (_ownsTray)
        {
            InitializeTrayIcon();
        }

        InitializeAcrylicBackdrop();
        _refreshTimer.Tick += RefreshTimer_Tick;

        if (_source == CounterSource.Bilibili)
        {
            InitializeSettings();
        }
        else if (AppSettings.NormalizeYouTubeUrl(sourceUrl ?? string.Empty) is string normalizedUrl)
        {
            InitializeYouTubeSource(normalizedUrl);
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/136.0 Safari/537.36 BiliFansDisplay/1.0");
        httpClient.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
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

    private void ApplyInitialWindowSize()
    {
        nint hwnd = WindowNative.GetWindowHandle(this);
        uint dpi = GetDpiForWindow(hwnd);
        double scale = dpi > 0 ? dpi / DefaultDpi : ReferenceDpiScale;
        ApplyWindowSizeForScale(scale);
    }

    private void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        XamlRoot? xamlRoot = RootGrid.XamlRoot;
        if (xamlRoot is null)
        {
            return;
        }

        if (_xamlRoot != xamlRoot)
        {
            if (_xamlRoot is not null)
            {
                _xamlRoot.Changed -= XamlRoot_Changed;
            }

            _xamlRoot = xamlRoot;
            _xamlRoot.Changed += XamlRoot_Changed;
        }

        ApplyWindowSizeForScale(xamlRoot.RasterizationScale);
    }

    private void XamlRoot_Changed(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        ApplyWindowSizeForScale(sender.RasterizationScale);
    }

    private void ApplyWindowSizeForScale(double scale)
    {
        if (scale <= 0 || Math.Abs(scale - _lastRasterizationScale) < 0.001)
        {
            return;
        }

        _lastRasterizationScale = scale;
        double logicalWidth = WindowWidthAtReferenceDpi / ReferenceDpiScale;
        double logicalHeight = WindowHeightAtReferenceDpi / ReferenceDpiScale;
        AppWindow.Resize(new SizeInt32(
            (int)Math.Round(logicalWidth * scale, MidpointRounding.AwayFromZero),
            (int)Math.Round(logicalHeight * scale, MidpointRounding.AwayFromZero)));
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

        var showBilibiliItem = new WinForms.ToolStripMenuItem("显示 Bilibili 窗口");
        showBilibiliItem.Click += TrayShowBilibili_Click;

        _showYouTubeItem = new WinForms.ToolStripMenuItem("显示 YouTube 窗口") { Enabled = false };
        _showYouTubeItem.Click += TrayShowYouTube_Click;

        var hideItem = new WinForms.ToolStripMenuItem("隐藏全部窗口");
        hideItem.Click += TrayHideAll_Click;

        var refreshItem = new WinForms.ToolStripMenuItem("立刻刷新全部");
        refreshItem.Click += TrayRefresh_Click;

        var openSettingsItem = new WinForms.ToolStripMenuItem("打开 settings.ini");
        openSettingsItem.Click += TrayOpenSettings_Click;

        var reloadSettingsItem = new WinForms.ToolStripMenuItem("重新加载配置");
        reloadSettingsItem.Click += TrayReloadSettings_Click;

        var exitItem = new WinForms.ToolStripMenuItem("退出");
        exitItem.Click += TrayExit_Click;

        _trayContextMenu.Items.Add(showBilibiliItem);
        _trayContextMenu.Items.Add(_showYouTubeItem);
        _trayContextMenu.Items.Add(hideItem);
        _trayContextMenu.Items.Add(refreshItem);
        _trayContextMenu.Items.Add(new WinForms.ToolStripSeparator());
        _trayContextMenu.Items.Add(openSettingsItem);
        _trayContextMenu.Items.Add(reloadSettingsItem);
        _trayContextMenu.Items.Add(new WinForms.ToolStripSeparator());
        _trayContextMenu.Items.Add(exitItem);

        _trayIcon = new WinForms.NotifyIcon
        {
            ContextMenuStrip = _trayContextMenu,
            Icon = _trayIconImage,
            Text = "BiliFansDisplay",
            Visible = true
        };
        _trayIcon.DoubleClick += TrayShowBilibili_Click;
    }

    private void TrayShowBilibili_Click(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(ShowWindow);
    }

    private void TrayShowYouTube_Click(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            EnsureYouTubeWindow();
            _youtubeWindow?.ShowWindow();
        });
    }

    private void TrayHideAll_Click(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            AppWindow.Hide();
            _youtubeWindow?.AppWindow.Hide();
        });
    }

    private void TrayRefresh_Click(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            await RefreshNowAsync();
            if (_youtubeWindow is not null)
            {
                await _youtubeWindow.RefreshNowAsync();
            }
        });
    }

    private void TrayOpenSettings_Click(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _settings ??= AppSettings.LoadOrCreate(LoadConfiguredUid());
            Process.Start(new ProcessStartInfo(AppSettings.SettingsPath) { UseShellExecute = true });
        });
    }

    private void TrayReloadSettings_Click(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(async () => await ReloadSettingsAsync());
    }

    private void TrayExit_Click(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(Close);
    }

    private void ShowWindow()
    {
        AppWindow.Show();
        Activate();
    }

    private void InitializeSettings()
    {
        long? legacyUid = LoadConfiguredUid();
        _settings = AppSettings.LoadOrCreate(legacyUid);

        if (AppSettings.TryGetBilibiliUid(_settings.BilibiliUrl, out long uid))
        {
            InitializeUid(uid);
        }
        else
        {
            ShowSetupView();
        }

        UpdateYouTubeTrayState();
    }

    private async Task ReloadSettingsAsync()
    {
        if (!_ownsTray)
        {
            return;
        }

        _settings = AppSettings.LoadOrCreate(LoadConfiguredUid());
        if (AppSettings.TryGetBilibiliUid(_settings.BilibiliUrl, out long uid))
        {
            if (_uid != uid || SetupView.Visibility == Visibility.Visible)
            {
                InitializeUid(uid);
            }
            else
            {
                await RefreshNowAsync();
            }
        }
        else
        {
            ShowSetupView();
        }

        EnsureYouTubeWindow();
        if (_youtubeWindow is not null)
        {
            await _youtubeWindow.RefreshNowAsync();
        }
    }

    private void EnsureYouTubeWindow()
    {
        if (!_ownsTray)
        {
            return;
        }

        string? normalizedUrl = AppSettings.NormalizeYouTubeUrl(_settings?.YouTubeUrl ?? string.Empty);
        if (_showYouTubeItem is not null)
        {
            _showYouTubeItem.Enabled = normalizedUrl is not null;
        }

        if (normalizedUrl is null)
        {
            CloseYouTubeWindow();
            return;
        }

        if (_youtubeWindow is not null
            && string.Equals(_youtubeWindow._sourceUrl, normalizedUrl, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        CloseYouTubeWindow();
        _youtubeWindow = new MainWindow(CounterSource.YouTube, normalizedUrl, false);
        _youtubeWindow.Closed += YouTubeWindow_Closed;
        _youtubeWindow.Activate();
        DispatcherQueue.TryEnqueue(PositionYouTubeWindow);
    }

    private void UpdateYouTubeTrayState()
    {
        if (_showYouTubeItem is not null)
        {
            _showYouTubeItem.Enabled = AppSettings.NormalizeYouTubeUrl(_settings?.YouTubeUrl ?? string.Empty) is not null;
        }
    }

    private void PositionYouTubeWindow()
    {
        if (_youtubeWindow is null)
        {
            return;
        }

        const int gap = 12;
        PointInt32 primaryPosition = AppWindow.Position;
        SizeInt32 primarySize = AppWindow.Size;
        SizeInt32 youtubeSize = _youtubeWindow.AppWindow.Size;
        int x = primaryPosition.X + primarySize.Width + gap;
        int y = primaryPosition.Y;

        DisplayArea displayArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
        RectInt32 workArea = displayArea.WorkArea;
        if (x + youtubeSize.Width > workArea.X + workArea.Width)
        {
            x = primaryPosition.X - youtubeSize.Width - gap;
        }

        x = Math.Clamp(x, workArea.X, workArea.X + Math.Max(0, workArea.Width - youtubeSize.Width));
        y = Math.Clamp(y, workArea.Y, workArea.Y + Math.Max(0, workArea.Height - youtubeSize.Height));
        _youtubeWindow.AppWindow.Move(new PointInt32(x, y));
    }

    private void YouTubeWindow_Closed(object sender, WindowEventArgs args)
    {
        if (sender is MainWindow window)
        {
            window.Closed -= YouTubeWindow_Closed;
        }

        _youtubeWindow = null;
        UpdateYouTubeTrayState();
    }

    private void CloseYouTubeWindow()
    {
        MainWindow? window = _youtubeWindow;
        if (window is null)
        {
            return;
        }

        _youtubeWindow = null;
        window.Closed -= YouTubeWindow_Closed;
        window.Close();
    }

    private void ShowSetupView()
    {
        _sourceGeneration++;
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
        _sourceGeneration++;
        _refreshTimer.Stop();
        _uid = uid;
        _sourceUrl = $"https://space.bilibili.com/{uid.ToString(CultureInfo.InvariantCulture)}";
        _historyPath = GetBilibiliHistoryPath(uid);
        MigrateLegacyHistory(uid);
        InitializeHistoryAndRefresh();
    }

    private void InitializeYouTubeSource(string sourceUrl)
    {
        _sourceGeneration++;
        _refreshTimer.Stop();
        _sourceUrl = sourceUrl;
        _historyPath = GetYouTubeHistoryPath(sourceUrl);
        InitializeHistoryAndRefresh();
    }

    private void InitializeHistoryAndRefresh()
    {
        _history = _historyPath is null ? [] : LoadHistoryFromPath(_historyPath);
        if (RemoveExpiredHistory())
        {
            SaveHistory();
        }

        ShowStatsView();
        FanRecord? latestRecord = _history.OrderBy(record => record.Timestamp).LastOrDefault();
        if (latestRecord is not null)
        {
            UpdateDisplay(latestRecord.Follower, latestRecord.Timestamp);
        }
        else
        {
            FansText.Text = "--";
            UpdateDeltaText(OneHourText, "1h", new DeltaResult(false, 0), true);
            ThreeHourText.Visibility = Visibility.Collapsed;
            DayText.Visibility = Visibility.Collapsed;
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
            _settings ??= AppSettings.LoadOrCreate(uid);
            _settings.BilibiliUrl = $"https://space.bilibili.com/{uid.ToString(CultureInfo.InvariantCulture)}";
            _settings.Save();
            InitializeUid(uid);
        }
        catch
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

        if (IsSourceConfigured && !_isClosing)
        {
            _refreshTimer.Start();
        }
    }

    private bool IsSourceConfigured => _source switch
    {
        CounterSource.Bilibili => _uid is not null,
        CounterSource.YouTube => !string.IsNullOrWhiteSpace(_sourceUrl),
        _ => false
    };

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
        if (_isRefreshing || _isClosing || !IsSourceConfigured)
        {
            return;
        }

        _isRefreshing = true;
        int generation = _sourceGeneration;

        try
        {
            long? follower = _source switch
            {
                CounterSource.Bilibili when _uid is long uid => await FetchBilibiliFollowerAsync(uid),
                CounterSource.YouTube when _sourceUrl is string url => await FetchYouTubeFollowerAsync(url),
                _ => null
            };

            if (follower is null || generation != _sourceGeneration || _isClosing)
            {
                return;
            }

            DateTimeOffset timestamp = DateTimeOffset.UtcNow;
            _history.Add(new FanRecord(timestamp, follower.Value));
            RemoveExpiredHistory();
            SaveHistory();
            UpdateDisplay(follower.Value, timestamp);
        }
        catch
        {
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private static async Task<long?> FetchBilibiliFollowerAsync(long uid)
    {
        string requestUri = $"https://api.bilibili.com/x/relation/stat?vmid={uid.ToString(CultureInfo.InvariantCulture)}";
        using HttpResponseMessage response = await HttpClient.GetAsync(requestUri);
        response.EnsureSuccessStatusCode();

        await using Stream stream = await response.Content.ReadAsStreamAsync();
        using JsonDocument document = await JsonDocument.ParseAsync(stream);
        JsonElement root = document.RootElement;
        return root.GetProperty("code").GetInt32() == 0
            ? root.GetProperty("data").GetProperty("follower").GetInt64()
            : null;
    }

    private static async Task<long?> FetchYouTubeFollowerAsync(string channelUrl)
    {
        string separator = channelUrl.Contains('?') ? "&" : "?";
        string html = await HttpClient.GetStringAsync($"{channelUrl}{separator}hl=en&persist_hl=1");

        foreach (string marker in new[] { "var ytInitialData = ", "ytInitialData = " })
        {
            string? json = ExtractJsonObject(html, marker);
            if (json is null)
            {
                continue;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 256 });
                if (TryFindSubscriberCountInPageHeader(document.RootElement, out long count)
                    || TryFindSubscriberCountInElement(document.RootElement, out count))
                {
                    return count;
                }
            }
            catch (JsonException)
            {
            }
        }

        return TryParseSubscriberCount(html, out long fallbackCount) ? fallbackCount : null;
    }

    private static string? ExtractJsonObject(string html, string marker)
    {
        int markerIndex = html.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return null;
        }

        int start = markerIndex + marker.Length;
        while (start < html.Length && char.IsWhiteSpace(html[start]))
        {
            start++;
        }

        if (start >= html.Length || html[start] != '{')
        {
            return null;
        }

        int depth = 0;
        bool inString = false;
        bool escaped = false;

        for (int index = start; index < html.Length; index++)
        {
            char character = html[index];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (character == '"')
            {
                inString = true;
            }
            else if (character == '{')
            {
                depth++;
            }
            else if (character == '}' && --depth == 0)
            {
                return html[start..(index + 1)];
            }
        }

        return null;
    }

    private static bool TryFindSubscriberCountInPageHeader(JsonElement element, out long count)
    {
        count = 0;
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if ((property.NameEquals("pageHeaderRenderer") || property.NameEquals("pageHeaderViewModel"))
                        && TryFindSubscriberCountInElement(property.Value, out count))
                    {
                        return true;
                    }
                }

                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (TryFindSubscriberCountInPageHeader(property.Value, out count))
                    {
                        return true;
                    }
                }

                break;

            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    if (TryFindSubscriberCountInPageHeader(item, out count))
                    {
                        return true;
                    }
                }

                break;
        }

        return false;
    }

    private static bool TryFindSubscriberCountInElement(JsonElement element, out long count)
    {
        count = 0;
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return TryParseSubscriberCount(element.GetString() ?? string.Empty, out count);

            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (TryFindSubscriberCountInElement(property.Value, out count))
                    {
                        return true;
                    }
                }

                break;

            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    if (TryFindSubscriberCountInElement(item, out count))
                    {
                        return true;
                    }
                }

                break;
        }

        return false;
    }

    private static bool TryParseSubscriberCount(string text, out long count)
    {
        count = 0;
        Match match = YouTubeSubscriberRegex.Match(text);
        if (!match.Success)
        {
            return false;
        }

        string numberText = match.Groups["value"].Value.Replace(",", string.Empty, StringComparison.Ordinal);
        if (!decimal.TryParse(numberText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal value))
        {
            return false;
        }

        decimal multiplier = match.Groups["suffix"].Value.ToLowerInvariant() switch
        {
            "k" or "thousand" => 1_000m,
            "m" or "million" => 1_000_000m,
            "b" or "billion" => 1_000_000_000m,
            _ => 1m
        };

        decimal rounded = Math.Round(value * multiplier, 0, MidpointRounding.AwayFromZero);
        if (rounded < 0 || rounded > long.MaxValue)
        {
            return false;
        }

        count = (long)rounded;
        return true;
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

    private static string GetBilibiliHistoryPath(long uid)
    {
        return Path.Combine(HistoryDirectory, $"{uid.ToString(CultureInfo.InvariantCulture)}.json");
    }

    private static string GetYouTubeHistoryPath(string sourceUrl)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sourceUrl.ToLowerInvariant()));
        string key = Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
        return Path.Combine(HistoryDirectory, $"youtube-{key}.json");
    }

    private void MigrateLegacyHistory(long uid)
    {
        if (!File.Exists(LegacyHistoryPath))
        {
            return;
        }

        try
        {
            string targetPath = GetBilibiliHistoryPath(uid);
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
        return Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue(resourceKey, out object value) && value is Brush brush
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

        if (_ownsTray
            && !_secondaryWindowInitialized
            && args.WindowActivationState != WindowActivationState.Deactivated)
        {
            _secondaryWindowInitialized = true;
            DispatcherQueue.TryEnqueue(EnsureYouTubeWindow);
        }
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _isClosing = true;
        _sourceGeneration++;
        _refreshTimer.Stop();
        _refreshTimer.Tick -= RefreshTimer_Tick;

        if (_xamlRoot is not null)
        {
            _xamlRoot.Changed -= XamlRoot_Changed;
            _xamlRoot = null;
        }

        if (_ownsTray)
        {
            CloseYouTubeWindow();
        }

        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.DoubleClick -= TrayShowBilibili_Click;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _trayContextMenu?.Dispose();
        _trayContextMenu = null;
        _showYouTubeItem = null;

        _trayIconImage?.Dispose();
        _trayIconImage = null;

        RootGrid.ActualThemeChanged -= RootGrid_ActualThemeChanged;
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

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPoint
    {
        public int X;
        public int Y;
    }

    private enum CounterSource
    {
        Bilibili,
        YouTube
    }

    private sealed record AppConfig
    {
        public long Uid { get; init; }
    }

    private sealed record FanRecord(DateTimeOffset Timestamp, long Follower);

    private sealed record DeltaResult(bool HasEnoughHistory, long Delta);
}
