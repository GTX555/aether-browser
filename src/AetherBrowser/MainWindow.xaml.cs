using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AetherBrowser.Models;
using AetherBrowser.Services;
using CefSharp;
using CefSharp.Wpf.HwndHost;
using Microsoft.Win32;
using AetherSettings = AetherBrowser.Models.BrowserSettings;

namespace AetherBrowser;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly bool _isPrivate;
    public bool IsPrivateWindow => _isPrivate;
    private readonly string? _initialUrl;
    private readonly bool _hasExplicitBounds;
    private readonly ObservableCollection<BrowserTab> _tabs = new();
    private readonly ObservableCollection<AutocompleteItem> _autocompleteItems = new();
    private readonly Stack<string> _closedTabs = new();
    private readonly DispatcherTimer _memoryTimer;
    private readonly Action<AetherSettings> _onSettingsChanged;
    private readonly Action _onBookmarksChanged;
    private readonly Action _onDownloadsChanged;
    private readonly Action<UpdateInfo> _onUpdateStatusChanged;
    private readonly Action<int> _onUpdateProgressChanged;
    private IRequestContext? _privateRequestContext;
    private BrowserTab? _activeTab;
    private bool _isUpdatingAddressBox;
    private bool _isFullscreen;
    private WindowState _preFullscreenState;
    private int _highlightedAutocompleteIndex = -1;
    private double _effectiveTabWidth = 156;
    private string? _lastFindQuery;

    // Tab drag & tear-off state
    private BrowserTab? _dragCandidateTab;
    private Point _dragStartPoint;
    private Point _dragAnchorInWindow;
    private bool _isDraggingTab;
    private bool _isDraggingSingleTabWindow;
    private bool _isTearOffReady;
    private MainWindow? _dropTargetWindow;
    private FrameworkElement? _elevatedTabContainer;

    public double TabUniformWidth => Math.Clamp(App.SettingsService.Current.TabMaxWidth, 100, 280);
    public double EffectiveTabWidth
    {
        get => _effectiveTabWidth;
        private set
        {
            if (Math.Abs(_effectiveTabWidth - value) > 0.5)
            {
                _effectiveTabWidth = value;
                NotifyUi();
                NotifyUi(nameof(IsCompactTabStrip));
                NotifyUi(nameof(IsIconOnlyTabStrip));
            }
        }
    }
    public bool IsCompactTabStrip => _effectiveTabWidth < 74;
    public bool IsIconOnlyTabStrip => _effectiveTabWidth < 52;
    public double CompactAddressBarWidth => Math.Clamp(App.SettingsService.Current.AddressBarWidth, 200, 560);
    public double BarControlHeight => App.SettingsService.Current.CompactMode ? 26 : 30;
    public CornerRadius PillCornerRadius => App.SettingsService.Current.RoundedAddressBar
        ? new CornerRadius(Math.Max(8, (BarControlHeight / 2.0) - 2))
        : new CornerRadius(7);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void NotifyUi([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public MainWindow(bool isPrivate = false, string? initialUrl = null, double? initialLeft = null, double? initialTop = null)
    {
        _isPrivate = isPrivate;
        _initialUrl = initialUrl;

        InitializeComponent();
        DataContext = this;

        if (initialLeft.HasValue && initialTop.HasValue)
        {
            _hasExplicitBounds = true;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = initialLeft.Value;
            Top = initialTop.Value;
        }

        HorizontalTabsList.ItemsSource = _tabs;
        VerticalTabsList.ItemsSource = _tabs;
        AutocompleteList.ItemsSource = _autocompleteItems;
        _tabs.CollectionChanged += (_, _) => RecalculateEffectiveTabWidth();

        // Pixel-exact popup alignment (immune to Windows MenuDropAlignment handedness!)
        ExtensionsPopup.CustomPopupPlacementCallback = RightAlignPopupBelow;
        DownloadsPopup.CustomPopupPlacementCallback = RightAlignPopupBelow;
        MainMenuPopup.CustomPopupPlacementCallback = RightAlignPopupBelow;
        PrivacyPopup.CustomPopupPlacementCallback = LeftAlignPopupBelow;
        AutocompletePopup.CustomPopupPlacementCallback = LeftAlignPopupBelow;

        if (_isPrivate)
        {
            PrivateBadge.Visibility = Visibility.Visible;
            Title = "Aether Browser (Private)";
        }

        _onSettingsChanged = s => Dispatcher.BeginInvoke(() => ApplySettingsToChrome(s));
        _onBookmarksChanged = () => Dispatcher.BeginInvoke(RefreshBookmarksBar);
        _onDownloadsChanged = () => Dispatcher.BeginInvoke(RefreshDownloadsUi);
        _onUpdateStatusChanged = info => Dispatcher.BeginInvoke(() => OnUpdateStatusChanged(info));
        _onUpdateProgressChanged = pct => Dispatcher.BeginInvoke(() => BroadcastToInternalPages(new { type = "updateProgress", percent = pct }));

        ApplySettingsToChrome(App.SettingsService.Current);
        App.SettingsService.SettingsChanged += _onSettingsChanged;
        App.DataStore.BookmarksChanged += _onBookmarksChanged;
        App.DataStore.DownloadsChanged += _onDownloadsChanged;
        App.Updates.UpdateStatusChanged += _onUpdateStatusChanged;
        App.Updates.DownloadProgressChanged += _onUpdateProgressChanged;

        _memoryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _memoryTimer.Tick += async (_, _) => await MemoryTickAsync();

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        StateChanged += MainWindow_StateChanged;
        Deactivated += (_, _) => { AutocompletePopup.IsOpen = false; CancelTabDrag(); };
        LocationChanged += (_, _) => { if (AutocompletePopup.IsOpen) AutocompletePopup.IsOpen = false; };
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        MouseMove += Window_TabDragMouseMove;
        PreviewMouseLeftButtonUp += Window_TabDragMouseUp;
        LostMouseCapture += (_, _) => CancelTabDrag();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var s = App.SettingsService.Current;
        if (s.RestoreWindowBounds && !_isPrivate && !_hasExplicitBounds)
        {
            if (s.WindowWidth >= MinWidth && s.WindowHeight >= MinHeight)
            {
                Width = s.WindowWidth;
                Height = s.WindowHeight;
            }
            if (s.WindowMaximized)
                WindowState = WindowState.Maximized;
        }

        RefreshBookmarksBar();

        // Startup tabs
        if (!string.IsNullOrWhiteSpace(_initialUrl))
        {
            await CreateTabAsync(_initialUrl, select: true);
        }
        else if (!_isPrivate && s.StartupMode == "RestoreSession")
        {
            var saved = App.DataStore.LoadSession();
            if (saved.Count > 0)
            {
                BrowserTab? toSelect = null;
                foreach (var st in saved)
                {
                    var tab = await CreateTabAsync(st.Url, select: false, lazy: s.LazyLoadRestoredTabs && !st.IsActive, presetTitle: st.Title);
                    tab.IsPinned = st.IsPinned;
                    if (st.IsActive) toSelect = tab;
                }
                await SelectTabAsync(toSelect ?? _tabs[0]);
            }
            else
            {
                await CreateTabAsync(s.GetNewTabUrl(), select: true);
            }
        }
        else if (!_isPrivate && s.StartupMode == "SpecificUrls" && !string.IsNullOrWhiteSpace(s.StartupUrls))
        {
            var urls = s.StartupUrls.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (int i = 0; i < urls.Length; i++)
                await CreateTabAsync(NormalizeUserInput(urls[i]), select: i == 0);
        }
        else
        {
            // Default: clean blank new tab!
            await CreateTabAsync(s.GetNewTabUrl(), select: true);
        }

        _memoryTimer.Start();
        _ = RefreshMemoryBadgeAsync();

        // Startup automatic update check (delayed slightly for zero startup overhead)
        if (!_isPrivate && App.Updates.ShouldCheckOnStartup())
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(1800);
                await App.Updates.CheckForUpdatesAsync(manual: false);
            });
        }
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var s = App.SettingsService.Current;
        if (s.ConfirmCloseMultipleTabs && _tabs.Count > 1)
        {
            var res = MessageBox.Show(this, $"Close {_tabs.Count} open tabs?", "Aether Browser", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (res != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        _memoryTimer.Stop();

        App.SettingsService.SettingsChanged -= _onSettingsChanged;
        App.DataStore.BookmarksChanged -= _onBookmarksChanged;
        App.DataStore.DownloadsChanged -= _onDownloadsChanged;
        App.Updates.UpdateStatusChanged -= _onUpdateStatusChanged;
        App.Updates.DownloadProgressChanged -= _onUpdateProgressChanged;

        if (!_isPrivate)
        {
            if (s.RestoreWindowBounds)
            {
                s.WindowMaximized = WindowState == WindowState.Maximized;
                if (WindowState == WindowState.Normal)
                {
                    s.WindowWidth = Width;
                    s.WindowHeight = Height;
                }
                App.SettingsService.Save();
            }

            if (s.StartupMode == "RestoreSession")
            {
                App.DataStore.SaveSession(_tabs.Select(t => new SavedSessionTab
                {
                    Url = t.Url,
                    Title = t.Title,
                    IsPinned = t.IsPinned,
                    IsActive = t == _activeTab
                }));
            }

            // Clear on exit privacy hygiene
            if (s.ClearHistoryOnExit) App.DataStore.ClearHistory();
            if (s.ClearDownloadsOnExit) App.DataStore.ClearDownloads();
            if (s.ClearCookiesOnExit || s.ClearCacheOnExit)
            {
                try
                {
                    CefHostService.ClearBrowsingDataAsync(
                        App.CefHost.GetGlobalRequestContext(),
                        s.ClearCookiesOnExit,
                        s.ClearCacheOnExit).Wait(TimeSpan.FromSeconds(1.5));
                }
                catch { }
            }
        }

        foreach (var tab in _tabs)
        {
            DisposeTabBrowser(tab);
        }

        if (_privateRequestContext is not null)
        {
            try
            {
                if (!_privateRequestContext.IsDisposed)
                    _privateRequestContext.Dispose();
            }
            catch { }
            _privateRequestContext = null;
        }

        try { App.DataStore.Flush(); } catch { }
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        MaximizeIcon.IconKey = WindowState == WindowState.Maximized ? "Icon.Copy" : "Icon.Square";
        RootFrame.BorderThickness = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(1);

        if (WindowState == WindowState.Minimized && App.SettingsService.Current.TrimWorkingSetOnMinimize)
        {
            App.Memory.TrimWorkingSet(aggressiveGc: true);
        }
    }

    // ==================== TAB MANAGEMENT ====================

    public async Task<BrowserTab> CreateTabAsync(string url, bool select = true, bool lazy = false, string? presetTitle = null)
    {
        var s = App.SettingsService.Current;
        var tab = new BrowserTab
        {
            Url = string.IsNullOrWhiteSpace(url) ? "about:blank" : url,
            Title = presetTitle ?? (url == "about:blank" ? "New Tab" : url),
            ZoomFactor = s.DefaultZoomPercent / 100.0,
            MemoryState = lazy ? TabMemoryState.Discarded : TabMemoryState.Active
        };

        if (s.OpenNewTabNextToCurrent && _activeTab is not null)
        {
            var idx = _tabs.IndexOf(_activeTab);
            _tabs.Insert(Math.Min(idx + 1, _tabs.Count), tab);
        }
        else
        {
            _tabs.Add(tab);
        }

        if (select)
            await SelectTabAsync(tab);

        return tab;
    }

    private async Task SelectTabAsync(BrowserTab tab)
    {
        if (_activeTab is not null)
        {
            _activeTab.IsSelected = false;
            _activeTab.LastActiveUtc = DateTime.UtcNow;
            if (_activeTab.Browser is not null)
                _activeTab.Browser.Visibility = Visibility.Collapsed;
        }

        _activeTab = tab;
        _lastFindQuery = null;
        tab.IsSelected = true;
        tab.LastActiveUtc = DateTime.UtcNow;

        if (tab.MemoryState == TabMemoryState.Sleeping)
        {
            App.Memory.WakeSleepingTab(tab, App.SettingsService.Current);
        }

        if (tab.Browser is null || tab.Browser.IsDisposed || tab.MemoryState == TabMemoryState.Discarded)
        {
            await EnsureBrowserInitializedAsync(tab);
            if (tab.Browser is { IsDisposed: false, IsBrowserInitialized: true })
                NavigateTabCore(tab, tab.Url);
        }
        else
        {
            tab.Browser.Visibility = Visibility.Visible;
        }

        // Always dismiss autocomplete and force the address bar to sync to the newly selected tab
        AutocompletePopup.IsOpen = false;
        _highlightedAutocompleteIndex = -1;
        if (!tab.IsBlankTab && AddressBox.IsKeyboardFocusWithin)
        {
            Keyboard.ClearFocus();
            AddressBarBorder.BorderBrush = (Brush)FindResource("BorderBrush");
        }

        SyncToolbarToActiveTab(forceAddressUpdate: true);
        SyncAddressProgressToTab(tab);

        if (tab.IsBlankTab)
        {
            _ = Dispatcher.BeginInvoke(() =>
            {
                AddressBox.Focus();
                AddressBox.SelectAll();
            }, DispatcherPriority.Input);
        }
    }

    private IRequestContext GetOrCreatePrivateRequestContext(AetherSettings settings)
    {
        if (_privateRequestContext is null || _privateRequestContext.IsDisposed)
            _privateRequestContext = App.CefHost.CreatePrivateRequestContext(settings);
        return _privateRequestContext;
    }

    private void DisposeTabBrowser(BrowserTab tab)
    {
        var browser = tab.Browser;
        if (browser is null) return;

        tab.Browser = null;
        tab.IsBrowserInitialized = false;
        tab.IsLoading = false;
        tab.LoadingProgress = 0;

        try { WebViewHostGrid.Children.Remove(browser); } catch { }
        try
        {
            if (!browser.IsDisposed)
                browser.Dispose();
        }
        catch { }
    }

    private async Task EnsureBrowserInitializedAsync(BrowserTab tab)
    {
        if (tab.Browser is { IsDisposed: false } existing && tab.IsBrowserInitialized)
        {
            existing.Visibility = tab.IsSelected ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        if (tab.Browser is not null)
            DisposeTabBrowser(tab);

        var s = App.SettingsService.Current;
        var browser = new ChromiumWebBrowser
        {
            Visibility = tab.IsSelected ? Visibility.Visible : Visibility.Collapsed,
            BrowserSettings = CefHostService.CreateBrowserSettings(s)
        };

        if (_isPrivate)
            browser.RequestContext = GetOrCreatePrivateRequestContext(s);

        // Wire CEF handlers before adding to visual tree so every request/navigation is intercepted from frame 0
        var requestHandler = new AetherRequestHandler(tab);
        requestHandler.RequestBlocked += (host, _) =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                tab.RecordBlocked(host);
                if (tab == _activeTab) UpdateShieldBadge(tab);
            });
        };
        requestHandler.OpenUrlInNewTabRequested += (url, switchToTab) =>
        {
            Dispatcher.BeginInvoke(async () => await CreateTabAsync(url, select: switchToTab));
        };
        browser.RequestHandler = requestHandler;

        var lifeSpanHandler = new AetherLifeSpanHandler();
        lifeSpanHandler.PopupRequested += (url, switchToTab) =>
        {
            Dispatcher.BeginInvoke(async () => await CreateTabAsync(url, select: switchToTab));
        };
        browser.LifeSpanHandler = lifeSpanHandler;

        var downloadHandler = new AetherDownloadHandler();
        downloadHandler.DownloadStarted += _ =>
        {
            RefreshDownloadsUi();
            DownloadsPopup.IsOpen = true;
        };
        downloadHandler.DownloadUpdated += record =>
        {
            if (!record.IsInProgress)
                RefreshDownloadsUi();
        };
        browser.DownloadHandler = downloadHandler;

        var displayHandler = new AetherDisplayHandler();
        displayHandler.TitleChanged += title =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (!tab.IsBlankTab && !tab.IsInternalPage)
                {
                    tab.Title = string.IsNullOrWhiteSpace(title) ? tab.Url : title;
                    if (tab == _activeTab) UpdateWindowTitle();
                }
            });
        };
        displayHandler.AddressChanged += addr =>
        {
            Dispatcher.BeginInvoke(() => OnBrowserAddressChanged(tab, addr));
        };
        displayHandler.LoadingProgressChanged += progress =>
        {
            Dispatcher.BeginInvoke(() => OnBrowserLoadingProgressChanged(tab, progress));
        };
        displayHandler.FaviconUrlChanged += urls => OnBrowserFaviconUrlChanged(tab, urls);
        displayHandler.StatusMessageChanged += msg =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (tab == _activeTab)
                    StatusUrlText.Text = msg;
            });
        };
        displayHandler.FullscreenModeChanged += fs =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (tab == _activeTab && _isFullscreen != fs)
                    ToggleFullscreen();
            });
        };
        browser.DisplayHandler = displayHandler;

        browser.PermissionHandler = new AetherPermissionHandler();

        var jsBridge = AetherJsBridge.RegisterOn(browser);
        jsBridge.MessageReceived += json =>
        {
            Dispatcher.BeginInvoke(async () => await OnInternalWebMessageAsync(tab, json));
        };

        browser.FrameLoadStart += (_, args) =>
        {
            if (!tab.IsBlankTab && !tab.IsInternalPage)
                AetherRequestHandler.InjectPrivacyScript(args.Frame, App.SettingsService.Current);
        };

        browser.LoadingStateChanged += (_, args) =>
        {
            Dispatcher.BeginInvoke(() => OnBrowserLoadingStateChanged(tab, args));
        };

        var initTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        browser.IsBrowserInitializedChanged += (_, _) =>
        {
            if (browser.IsBrowserInitialized)
                initTcs.TrySetResult(true);
        };
        browser.Unloaded += (_, _) =>
        {
            if (browser.IsDisposed)
                initTcs.TrySetResult(false);
        };

        tab.Browser = browser;
        tab.MemoryState = TabMemoryState.Active;
        WebViewHostGrid.Children.Add(browser);

        if (!browser.IsBrowserInitialized)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var reg = cts.Token.Register(() => initTcs.TrySetResult(false));
            await initTcs.Task;
        }

        if (browser.IsDisposed || !browser.IsBrowserInitialized)
            return;

        tab.IsBrowserInitialized = true;
        CefHostService.ApplyLiveSettings(browser, s);
        if (Math.Abs(tab.ZoomFactor - 1.0) > 0.001)
            browser.SetZoomLevel(CefHostService.ZoomFactorToCefLevel(tab.ZoomFactor));
    }

    private void NavigateTabCore(BrowserTab tab, string rawInput)
    {
        if (tab.Browser is null || tab.Browser.IsDisposed || !tab.IsBrowserInitialized) return;
        var s = App.SettingsService.Current;
        var url = rawInput.Trim();

        if (string.IsNullOrEmpty(url) || url.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
        {
            tab.Url = "about:blank";
            tab.Title = "New Tab";
            tab.Favicon = null;
            tab.IsSecure = false;
            tab.IsLoading = false;
            tab.LoadingProgress = 0;
            tab.ResetBlocked();
            tab.Browser.LoadHtml(App.InternalPages.BuildBlankPageHtml(s), base64Encode: true);
            if (tab == _activeTab)
            {
                SyncToolbarToActiveTab();
                SyncAddressProgressToTab(tab);
            }
            return;
        }

        if (url.StartsWith("aether://", StringComparison.OrdinalIgnoreCase))
        {
            tab.Url = url.ToLowerInvariant();
            tab.Title = url.Replace("aether://", "Aether ", StringComparison.OrdinalIgnoreCase);
            tab.Favicon = null;
            tab.IsSecure = true;
            tab.IsLoading = false;
            tab.LoadingProgress = 0;
            tab.ResetBlocked();
            var mem = App.Memory.CaptureSnapshot(_tabs);
            var engineVer = App.CefHost.EngineVersion;
            var html = App.InternalPages.BuildInternalPageHtml(
                tab.Url, s, App.DataStore, mem, App.Updates.LastInfo, App.Privacy.TotalBlockedLifetime, engineVer);
            tab.Browser.LoadHtml(html, base64Encode: true);
            if (tab == _activeTab)
            {
                SyncToolbarToActiveTab();
                SyncAddressProgressToTab(tab);
            }
            return;
        }

        var target = NormalizeUserInput(url);
        target = App.Privacy.CleanTrackingUrl(target, s);
        tab.Url = target;
        tab.LoadingProgress = 0.08;
        tab.ResetBlocked();
        tab.Favicon = App.Favicons.GetCached(target);
        tab.Browser.LoadUrl(target);
        if (tab == _activeTab)
        {
            _lastFindQuery = null;
            SyncToolbarToActiveTab();
            UpdateAddressProgress(0.08, isLoading: true);
        }
    }

    private string NormalizeUserInput(string input)
    {
        var trimmed = input.Trim();
        var s = App.SettingsService.Current;

        if (trimmed.StartsWith("aether://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("chrome://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
            return trimmed;

        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            return s.HttpsOnlyMode && !IsLocalHost(trimmed) ? "https://" + trimmed[7..] : trimmed;

        if (trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            return trimmed;

        // Check if it looks like a domain / IP / localhost (no spaces, has dot or localhost:port)
        if (!trimmed.Contains(' ') &&
            (trimmed.Contains('.') || trimmed.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)))
        {
            var scheme = trimmed.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) ? "http://" : "https://";
            if (Uri.TryCreate(scheme + trimmed, UriKind.Absolute, out var uri))
                return uri.AbsoluteUri;
        }

        return s.ResolveSearchUrl(trimmed);
    }

    private static bool IsLocalHost(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        return u.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || u.Host == "127.0.0.1" || u.Host == "::1";
    }

    // ==================== CEF BROWSER EVENTS ====================

    private void OnBrowserAddressChanged(BrowserTab tab, string address)
    {
        if (string.IsNullOrWhiteSpace(address) ||
            address.Equals("about:blank", StringComparison.OrdinalIgnoreCase) ||
            address.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return;

        var previousHost = Uri.TryCreate(tab.Url, UriKind.Absolute, out var prevUri) ? prevUri.Host : null;
        tab.Url = address;
        tab.IsSecure = address.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                       address.StartsWith("aether://", StringComparison.OrdinalIgnoreCase) ||
                       address.StartsWith("chrome://", StringComparison.OrdinalIgnoreCase) ||
                       address.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase);

        var newHost = Uri.TryCreate(address, UriKind.Absolute, out var nextUri) ? nextUri.Host : null;
        if (!string.Equals(previousHost, newHost, StringComparison.OrdinalIgnoreCase))
        {
            tab.ResetBlocked();
            tab.Favicon = App.Favicons.GetCached(address);
        }

        if (tab == _activeTab)
        {
            _lastFindQuery = null;
            SyncToolbarToActiveTab();
        }
    }

    private void OnBrowserLoadingStateChanged(BrowserTab tab, LoadingStateChangedEventArgs args)
    {
        tab.IsLoading = args.IsLoading;
        tab.CanGoBack = args.CanGoBack;
        tab.CanGoForward = args.CanGoForward;

        if (!args.IsLoading)
        {
            tab.LoadingProgress = 1.0;

            if (!tab.IsBlankTab && !tab.IsInternalPage && !_isPrivate)
            {
                var url = tab.Url;
                if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    App.DataStore.RecordHistory(url, tab.Title);
                }
            }

            if (tab.Browser is { IsDisposed: false, IsBrowserInitialized: true } b &&
                Math.Abs(tab.ZoomFactor - 1.0) > 0.001)
            {
                b.SetZoomLevel(CefHostService.ZoomFactorToCefLevel(tab.ZoomFactor));
            }

            if (tab == _activeTab)
            {
                SyncToolbarToActiveTab();
                FinishAddressProgressAnimation();
            }
        }
        else if (tab == _activeTab)
        {
            SyncToolbarToActiveTab();
            if (!tab.IsBlankTab && !tab.IsInternalPage)
                UpdateAddressProgress(Math.Max(tab.LoadingProgress, 0.1), isLoading: true);
        }
    }

    private void OnBrowserLoadingProgressChanged(BrowserTab tab, double progress)
    {
        if (tab.IsBlankTab || tab.IsInternalPage)
            return;

        tab.LoadingProgress = progress;
        if (tab == _activeTab)
            UpdateAddressProgress(progress, isLoading: tab.IsLoading || progress < 0.999);
    }

    private void OnBrowserFaviconUrlChanged(BrowserTab tab, IList<string> urls)
    {
        if (tab.IsBlankTab || tab.IsInternalPage || urls.Count == 0)
            return;

        var pageUrl = tab.Url;
        if (!pageUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !pageUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return;

        var candidates = new string[urls.Count];
        for (int i = 0; i < urls.Count; i++)
            candidates[i] = urls[i];

        _ = Task.Run(async () =>
        {
            ImageSource? img = null;
            foreach (var iconUrl in candidates)
            {
                if (string.IsNullOrWhiteSpace(iconUrl)) continue;
                img = await App.Favicons.FetchByIconUrlAsync(pageUrl, iconUrl);
                if (img is not null) break;
            }

            img ??= await App.Favicons.FetchDirectFaviconAsync(pageUrl);
            if (img is not null)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (string.Equals(tab.Url, pageUrl, StringComparison.OrdinalIgnoreCase))
                        tab.Favicon = img;
                });
            }
        });
    }

    private static void ReloadBrowser(BrowserTab? tab)
    {
        if (tab?.Browser is { IsDisposed: false, IsBrowserInitialized: true } b)
            b.Reload();
    }

    // ==================== INTERNAL PAGES BRIDGE (aether://settings etc.) ====================

    private async Task OnInternalWebMessageAsync(BrowserTab tab, string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson)) return;
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            var action = root.TryGetProperty("action", out var a) ? a.GetString() : "";

            switch (action)
            {
                case "updateSetting":
                {
                    var key = root.GetProperty("key").GetString() ?? "";
                    var valEl = root.GetProperty("value");
                    ApplyDynamicSetting(key, valEl);
                    break;
                }
                case "reloadInternal":
                {
                    if (tab.IsInternalPage) NavigateTabCore(tab, tab.Url);
                    foreach (var t in _tabs.Where(x => x.IsBlankTab && x.Browser is { IsDisposed: false, IsBrowserInitialized: true }))
                        NavigateTabCore(t, "about:blank");
                    break;
                }
                case "setDefaultBrowser":
                {
                    var (_, msg) = DefaultBrowserService.PromptSetAsDefaultBrowser();
                    SendToInternalPage(tab, new { type = "toast", text = msg });
                    break;
                }
                case "updateAdBlockLists":
                {
                    var (_, msg) = await App.AdBlocker.UpdateCommunityFilterListsAsync();
                    ReloadInternalPageWithToast(tab, msg);
                    break;
                }
                case "sleepAllTabs":
                {
                    await SleepAllInactiveTabsAsync();
                    if (tab.IsInternalPage) NavigateTabCore(tab, tab.Url);
                    break;
                }
                case "purgeMemory":
                {
                    await SleepAllInactiveTabsAsync();
                    App.Memory.TrimWorkingSet(aggressiveGc: true);
                    await RefreshMemoryBadgeAsync();
                    if (tab.IsInternalPage) NavigateTabCore(tab, tab.Url);
                    break;
                }
                case "clearBrowsingData":
                {
                    App.DataStore.ClearHistory();
                    App.DataStore.ClearDownloads();
                    await CefHostService.ClearBrowsingDataAsync(
                        _isPrivate ? _privateRequestContext : App.CefHost.GetGlobalRequestContext(),
                        clearCookies: true,
                        clearCache: true);
                    break;
                }
                case "installExtensionFolder":
                {
                    var dlg = new OpenFolderDialog { Title = "Select Unpacked Extension Folder (containing manifest.json)" };
                    if (dlg.ShowDialog(this) == true)
                    {
                        var (ok, msg, _) = await App.Extensions.InstallFromUnpackedFolderAsync(dlg.FolderName);
                        ReloadInternalPageWithToast(tab, msg);
                    }
                    break;
                }
                case "installExtensionFile":
                {
                    var dlg = new OpenFileDialog
                    {
                        Title = "Select Chrome Extension Package (.crx or .zip)",
                        Filter = "Extension Packages (*.crx;*.zip)|*.crx;*.zip|All Files (*.*)|*.*"
                    };
                    if (dlg.ShowDialog(this) == true)
                    {
                        var (ok, msg, _) = await App.Extensions.InstallFromPackageFileAsync(dlg.FileName);
                        ReloadInternalPageWithToast(tab, msg);
                    }
                    break;
                }
                case "installExtensionCws":
                {
                    var input = root.GetProperty("input").GetString() ?? "";
                    var (ok, msg, _) = await App.Extensions.InstallFromChromeWebStoreAsync(input);
                    ReloadInternalPageWithToast(tab, msg);
                    break;
                }
                case "toggleExtension":
                {
                    var id = root.GetProperty("id").GetString() ?? "";
                    var en = root.GetProperty("enabled").GetBoolean();
                    await App.Extensions.SetEnabledAsync(id, en);
                    SendToInternalPage(tab, new { type = "toast", text = en ? "Extension enabled" : "Extension disabled" });
                    break;
                }
                case "removeExtension":
                {
                    var id = root.GetProperty("id").GetString() ?? "";
                    await App.Extensions.RemoveAsync(id);
                    if (tab.IsInternalPage) NavigateTabCore(tab, tab.Url);
                    break;
                }
                case "openExtensionPage":
                {
                    var id = root.GetProperty("id").GetString() ?? "";
                    var rec = App.DataStore.Extensions.FirstOrDefault(x => x.Id == id);
                    if (rec is not null)
                    {
                        var url = App.Extensions.GetExtensionPageUrl(rec);
                        if (!string.IsNullOrEmpty(url))
                            await CreateTabAsync(url, select: true);
                    }
                    break;
                }
                case "checkUpdates":
                {
                    var info = await App.Updates.CheckForUpdatesAsync(manual: true);
                    ReloadInternalPageWithToast(tab, info.StatusMessage);
                    break;
                }
                case "applyUpdate":
                {
                    var (ok, msg) = await App.Updates.DownloadAndApplyUpdateAsync(autoRestart: true);
                    SendToInternalPage(tab, new { type = "toast", text = msg });
                    break;
                }
                case "exportSettings":
                {
                    var dlg = new SaveFileDialog { FileName = "aether-settings.json", Filter = "JSON (*.json)|*.json" };
                    if (dlg.ShowDialog(this) == true)
                    {
                        File.WriteAllText(dlg.FileName, App.SettingsService.ExportJson());
                        SendToInternalPage(tab, new { type = "toast", text = "Settings exported!" });
                    }
                    break;
                }
                case "importSettings":
                {
                    var dlg = new OpenFileDialog { Filter = "JSON (*.json)|*.json" };
                    if (dlg.ShowDialog(this) == true)
                    {
                        var ok = App.SettingsService.ImportJson(File.ReadAllText(dlg.FileName));
                        ReloadInternalPageWithToast(tab, ok ? "Settings imported!" : "Invalid settings JSON.");
                    }
                    break;
                }
                case "resetSettings":
                {
                    App.SettingsService.ResetToDefaults();
                    if (tab.IsInternalPage) NavigateTabCore(tab, tab.Url);
                    break;
                }
                case "openUrl":
                {
                    var url = root.GetProperty("url").GetString() ?? "";
                    if (!string.IsNullOrEmpty(url))
                        await CreateTabAsync(url, select: true);
                    break;
                }
                case "clearHistory":
                {
                    App.DataStore.ClearHistory();
                    if (tab.IsInternalPage) NavigateTabCore(tab, tab.Url);
                    break;
                }
                case "removeHistory":
                {
                    App.DataStore.RemoveHistoryEntry(root.GetProperty("id").GetString() ?? "");
                    if (tab.IsInternalPage) NavigateTabCore(tab, tab.Url);
                    break;
                }
                case "removeBookmark":
                {
                    App.DataStore.RemoveBookmark(root.GetProperty("id").GetString() ?? "");
                    if (tab.IsInternalPage) NavigateTabCore(tab, tab.Url);
                    break;
                }
                case "clearDownloads":
                {
                    App.DataStore.ClearDownloads();
                    if (tab.IsInternalPage) NavigateTabCore(tab, tab.Url);
                    break;
                }
                case "openDownloadFolder":
                {
                    var p = root.GetProperty("path").GetString();
                    if (!string.IsNullOrEmpty(p))
                        Process.Start("explorer.exe", File.Exists(p) ? $"/select,\"{p}\"" : Path.GetDirectoryName(p) ?? "");
                    break;
                }
            }
        }
        catch { }
    }

    private void ApplyDynamicSetting(string key, JsonElement value)
    {
        var prop = typeof(AetherSettings).GetProperty(key);
        if (prop is null || !prop.CanWrite) return;

        App.SettingsService.Update(s =>
        {
            if (prop.PropertyType == typeof(bool))
                prop.SetValue(s, value.GetBoolean());
            else if (prop.PropertyType == typeof(int))
                prop.SetValue(s, value.GetInt32());
            else if (prop.PropertyType == typeof(double))
                prop.SetValue(s, value.GetDouble());
            else if (prop.PropertyType == typeof(string))
                prop.SetValue(s, value.GetString() ?? "");
            else if (prop.PropertyType == typeof(List<string>) && value.ValueKind == JsonValueKind.Array)
                prop.SetValue(s, value.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => !string.IsNullOrWhiteSpace(x)).ToList());
        });

        // Push live CEF runtime preferences to active RequestContext
        var current = App.SettingsService.Current;
        CefHostService.ApplyRuntimePreferences(
            _isPrivate ? _privateRequestContext : App.CefHost.GetGlobalRequestContext(),
            current);
    }

    private void ReloadInternalPageWithToast(BrowserTab tab, string text)
    {
        if (!tab.IsInternalPage)
            return;

        var browser = tab.Browser;
        if (browser is not null && !browser.IsDisposed && tab.IsBrowserInitialized)
        {
            var script = $"window.aetherOnMessage && window.aetherOnMessage({JsonSerializer.Serialize(new { type = "toast", text })});";
            EventHandler<FrameLoadEndEventArgs>? handler = null;
            handler = (_, args) =>
            {
                if (!args.Frame.IsMain) return;
                browser.FrameLoadEnd -= handler;
                if (!browser.IsDisposed && tab.IsInternalPage)
                    args.Frame.ExecuteJavaScriptAsync(script);
            };
            browser.FrameLoadEnd += handler;
        }

        NavigateTabCore(tab, tab.Url);
    }

    private static void SendToInternalPage(BrowserTab tab, object payload)
    {
        if (tab.Browser is null || tab.Browser.IsDisposed || !tab.IsBrowserInitialized || !tab.IsInternalPage) return;
        var json = JsonSerializer.Serialize(payload);
        tab.Browser.ExecuteScriptAsyncWhenPageLoaded($"window.aetherOnMessage && window.aetherOnMessage({json});", oneTime: true);
    }

    private void BroadcastToInternalPages(object payload)
    {
        foreach (var t in _tabs.Where(x => x.IsInternalPage))
            SendToInternalPage(t, payload);
    }

    // ==================== MEMORY SAVER BACKGROUND LOOP ====================

    private async Task MemoryTickAsync()
    {
        var s = App.SettingsService.Current;
        var now = DateTime.UtcNow;

        foreach (var tab in _tabs.ToList())
        {
            if (tab == _activeTab || tab.IsBlankTab || tab.IsInternalPage)
                continue;
            if (s.ExcludePinnedFromSleep && tab.IsPinned)
                continue;
            if (s.ExcludeAudioFromSleep && tab.IsAudible)
                continue;

            var idleMinutes = (now - tab.LastActiveUtc).TotalMinutes;

            if (s.EnableAutoDiscardTabs && idleMinutes >= s.AutoDiscardMinutes && tab.MemoryState != TabMemoryState.Discarded)
            {
                App.Memory.DiscardTab(tab, t =>
                {
                    if (t.Browser is not null) WebViewHostGrid.Children.Remove(t.Browser);
                });
            }
            else if (s.EnableAutoSleepTabs && idleMinutes >= s.AutoSleepMinutes && tab.MemoryState == TabMemoryState.Active)
            {
                await App.Memory.TrySleepTabAsync(tab);
            }
        }

        await RefreshMemoryBadgeAsync();
    }

    private async Task SleepAllInactiveTabsAsync()
    {
        foreach (var tab in _tabs.ToList())
        {
            if (tab == _activeTab) continue;
            if (tab.MemoryState == TabMemoryState.Active)
                await App.Memory.TrySleepTabAsync(tab);
        }
        await RefreshMemoryBadgeAsync();
    }

    private async Task RefreshMemoryBadgeAsync()
    {
        if (!App.SettingsService.Current.ShowMemoryBadge) return;
        var tabsCopy = _tabs.ToList();
        var snap = await Task.Run(() => App.Memory.CaptureSnapshot(tabsCopy));
        MemoryBadgeText.Text = $"{Math.Round(snap.TotalMb)} MB";
        StatusRightText.Text = $"RAM {Math.Round(snap.TotalMb)} MB · {snap.ActiveTabs} active · {snap.SleepingTabs + snap.DiscardedTabs} sleeping · {App.Privacy.TotalBlockedLifetime} blocked";
    }

    // ==================== CHROME & TOOLBAR SYNC ====================

    private void ApplySettingsToChrome(AetherSettings s)
    {
        var barHeight = s.CompactMode ? 36 : 42;
        TopBarBorder.Height = barHeight;
        Chrome.CaptionHeight = barHeight;

        NotifyUi(nameof(TabUniformWidth));
        NotifyUi(nameof(CompactAddressBarWidth));
        NotifyUi(nameof(BarControlHeight));
        NotifyUi(nameof(PillCornerRadius));
        RecalculateEffectiveTabWidth();

        // Vertical vs Horizontal tabs
        if (s.VerticalTabs)
        {
            HorizontalTabsHost.Visibility = Visibility.Collapsed;
            VerticalTabsSidebar.Visibility = Visibility.Visible;
            SidebarColumn.Width = new GridLength(Math.Clamp(s.SidebarWidth, 170, 340));
        }
        else
        {
            HorizontalTabsHost.Visibility = Visibility.Visible;
            VerticalTabsSidebar.Visibility = Visibility.Collapsed;
            SidebarColumn.Width = new GridLength(0);
        }

        BookmarksBar.Visibility = s.ShowBookmarksBar ? Visibility.Visible : Visibility.Collapsed;
        HomeBtn.Visibility = s.ShowHomeButton ? Visibility.Visible : Visibility.Collapsed;
        PrivacyShieldBtn.Visibility = s.ShowPrivacyShieldButton ? Visibility.Visible : Visibility.Collapsed;
        ExtensionsBtn.Visibility = s.ShowExtensionsButton ? Visibility.Visible : Visibility.Collapsed;
        DownloadsBtn.Visibility = s.ShowDownloadsButton ? Visibility.Visible : Visibility.Collapsed;
        MemoryBadgeBtn.Visibility = s.ShowMemoryBadge ? Visibility.Visible : Visibility.Collapsed;
        SidebarToggleBtn.Visibility = s.ShowSidebarToggleButton ? Visibility.Visible : Visibility.Collapsed;
        StatusBarBorder.Visibility = s.ShowStatusBar ? Visibility.Visible : Visibility.Collapsed;

        AddressPlaceholder.Text = $"Search {s.DefaultSearchEngine} or URL...";
    }

    private void HorizontalTabsHost_SizeChanged(object sender, SizeChangedEventArgs e) =>
        RecalculateEffectiveTabWidth();

    private void RecalculateEffectiveTabWidth()
    {
        var desired = TabUniformWidth;
        var avail = HorizontalTabsHost?.ActualWidth ?? 0;
        if (avail <= 60 || _tabs.Count == 0)
        {
            EffectiveTabWidth = desired;
            return;
        }

        const double tabHorizontalMargin = 5; // Margin="1,2,4,2"
        const double plusButtonReserve = 36;
        int pinnedCount = _tabs.Count(t => t.IsPinned);
        int normalCount = _tabs.Count - pinnedCount;

        if (normalCount <= 0)
        {
            EffectiveTabWidth = desired;
            return;
        }

        double usable = Math.Max(0, avail - plusButtonReserve - pinnedCount * (34 + tabHorizontalMargin));
        double perTab = (usable / normalCount) - tabHorizontalMargin;
        EffectiveTabWidth = Math.Clamp(Math.Floor(perTab), 38, desired);
    }

    private void UpdateAddressProgress(double progress, bool isLoading)
    {
        var trackWidth = AddressProgressTrack.ActualWidth;
        if (trackWidth <= 10) trackWidth = Math.Max(100, CompactAddressBarWidth - 24);

        if (!isLoading || progress >= 0.999)
        {
            FinishAddressProgressAnimation();
            return;
        }

        AddressProgressFill.BeginAnimation(OpacityProperty, null);
        AddressProgressFill.Opacity = 1;

        var targetWidth = Math.Max(trackWidth * 0.08, trackWidth * Math.Clamp(progress, 0.0, 0.98));
        var anim = new DoubleAnimation
        {
            To = targetWidth,
            Duration = TimeSpan.FromMilliseconds(160),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        AddressProgressFill.BeginAnimation(WidthProperty, anim);
    }

    private void FinishAddressProgressAnimation()
    {
        var trackWidth = AddressProgressTrack.ActualWidth;
        if (trackWidth <= 10) trackWidth = Math.Max(100, CompactAddressBarWidth - 24);

        if (AddressProgressFill.Opacity <= 0.01 && AddressProgressFill.ActualWidth <= 1)
            return;

        var widthAnim = new DoubleAnimation
        {
            To = trackWidth,
            Duration = TimeSpan.FromMilliseconds(150),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        var fadeAnim = new DoubleAnimation
        {
            To = 0,
            BeginTime = TimeSpan.FromMilliseconds(140),
            Duration = TimeSpan.FromMilliseconds(200)
        };
        fadeAnim.Completed += (_, _) =>
        {
            if (_activeTab is not { IsLoading: true })
            {
                AddressProgressFill.BeginAnimation(WidthProperty, null);
                AddressProgressFill.Width = 0;
            }
        };

        AddressProgressFill.BeginAnimation(WidthProperty, widthAnim);
        AddressProgressFill.BeginAnimation(OpacityProperty, fadeAnim);
    }

    private void SyncAddressProgressToTab(BrowserTab tab)
    {
        if (tab.IsLoading && !tab.IsBlankTab && !tab.IsInternalPage)
        {
            UpdateAddressProgress(Math.Max(tab.LoadingProgress, 0.1), isLoading: true);
        }
        else
        {
            AddressProgressFill.BeginAnimation(OpacityProperty, null);
            AddressProgressFill.BeginAnimation(WidthProperty, null);
            AddressProgressFill.Opacity = 0;
            AddressProgressFill.Width = 0;
        }
    }

    private void SyncToolbarToActiveTab(bool forceAddressUpdate = false)
    {
        if (_activeTab is null) return;

        BackBtn.IsEnabled = _activeTab.CanGoBack;
        ForwardBtn.IsEnabled = _activeTab.CanGoForward;
        ReloadIcon.IconKey = _activeTab.IsLoading ? "Icon.X" : "Icon.RotateCw";

        // Address text
        if (forceAddressUpdate || !AddressBox.IsFocused)
        {
            _isUpdatingAddressBox = true;
            if (_activeTab.IsBlankTab)
            {
                AddressBox.Text = "";
                AddressPlaceholder.Visibility = Visibility.Visible;
            }
            else
            {
                var display = _activeTab.Url;
                if (!App.SettingsService.Current.ShowFullUrl && display.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    display = display[8..].TrimEnd('/');
                AddressBox.Text = display;
                AddressPlaceholder.Visibility = Visibility.Collapsed;
            }
            _isUpdatingAddressBox = false;
        }

        // Security icon
        if (_activeTab.IsBlankTab)
        {
            SecurityIcon.IconKey = "Icon.Search";
            SecurityIcon.IconBrush = (Brush)FindResource("MutedBrush");
        }
        else if (_activeTab.IsSecure)
        {
            SecurityIcon.IconKey = "Icon.Lock";
            SecurityIcon.IconBrush = (Brush)FindResource("SuccessBrush");
        }
        else
        {
            SecurityIcon.IconKey = "Icon.LockOpen";
            SecurityIcon.IconBrush = (Brush)FindResource("DangerBrush");
        }

        UpdateShieldBadge(_activeTab);
        UpdateBookmarkStar(_activeTab);
        UpdateZoomUi(_activeTab.ZoomFactor);
        UpdateWindowTitle();
    }

    private void UpdateShieldBadge(BrowserTab tab)
    {
        var enabled = App.SettingsService.Current.AdTrackerBlockerEnabled && App.SettingsService.Current.BlockerMode != "Off";
        ShieldIcon.IconKey = enabled ? "Icon.ShieldCheck" : "Icon.ShieldOff";
        ShieldIcon.IconBrush = enabled ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("MutedBrush");

        if (tab.BlockedCount > 0 && enabled)
        {
            ShieldCountText.Text = tab.BlockedCount.ToString();
            ShieldCountText.Visibility = Visibility.Visible;
        }
        else
        {
            ShieldCountText.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateBookmarkStar(BrowserTab tab)
    {
        var bookmarked = !tab.IsBlankTab && !tab.IsInternalPage && App.DataStore.IsBookmarked(tab.Url);
        BookmarkIcon.IconBrush = bookmarked ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("MutedBrush");
    }

    private void UpdateZoomUi(double factor)
    {
        var pct = (int)Math.Round(factor * 100);
        MenuZoomLabel.Text = $"{pct}%";
        ZoomBadgeText.Text = $"{pct}%";
        ZoomBadgeBtn.Visibility = pct == 100 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateWindowTitle()
    {
        if (_activeTab is null) return;
        var suffix = _isPrivate ? " — Aether Browser (Private)" : " — Aether Browser";
        Title = _activeTab.IsBlankTab ? (_isPrivate ? "Aether Browser (Private)" : "Aether Browser") : _activeTab.DisplayTitle + suffix;
    }

    private void RefreshBookmarksBar()
    {
        BookmarksBarList.ItemsSource = null;
        BookmarksBarList.ItemsSource = App.DataStore.Bookmarks.Take(30).ToList();
    }

    private void OnUpdateStatusChanged(UpdateInfo info)
    {
        if (info.IsUpdateAvailable)
        {
            UpdateBannerText.Text = $"Aether Browser v{info.LatestVersion} is available (current: v{info.CurrentVersion}).";
            UpdateBanner.Visibility = Visibility.Visible;
        }
    }

    // ==================== ADDRESS BAR & AUTOCOMPLETE ====================

    private void AddressBox_GotFocus(object sender, RoutedEventArgs e)
    {
        AddressBarBorder.BorderBrush = (Brush)FindResource("AccentBrush");
        if (_activeTab is not null && !_activeTab.IsBlankTab)
        {
            _isUpdatingAddressBox = true;
            AddressBox.Text = _activeTab.Url;
            _isUpdatingAddressBox = false;
        }
        AddressBox.SelectAll();
    }

    private void AddressBox_LostFocus(object sender, RoutedEventArgs e)
    {
        AddressBarBorder.BorderBrush = (Brush)FindResource("BorderBrush");
        // Defer closing slightly so clicking an item inside AutocompletePopup always fires first!
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (!AddressBox.IsKeyboardFocusWithin && !AutocompletePopup.IsMouseOver)
            {
                AutocompletePopup.IsOpen = false;
                _highlightedAutocompleteIndex = -1;
                SyncToolbarToActiveTab();
            }
        }, DispatcherPriority.Input);
    }

    private void AddressBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!AddressBox.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            AddressBox.Focus();
        }
    }

    private void AddressBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        AddressPlaceholder.Visibility = string.IsNullOrEmpty(AddressBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        if (_isUpdatingAddressBox || !AddressBox.IsFocused) return;

        var query = AddressBox.Text.Trim();
        if (string.IsNullOrEmpty(query))
        {
            AutocompletePopup.IsOpen = false;
            _autocompleteItems.Clear();
            _highlightedAutocompleteIndex = -1;
            return;
        }

        var s = App.SettingsService.Current;
        var built = new List<AutocompleteItem>();

        if (s.AutocompleteFromBookmarks)
        {
            foreach (var b in App.DataStore.Bookmarks.Where(b =>
                b.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                b.Url.Contains(query, StringComparison.OrdinalIgnoreCase)).Take(3))
            {
                built.Add(CreateAutocompleteItem("Icon.Star", b.Title, b.Url, b.Url, "Bookmark", fetchFavicon: true));
            }
        }

        if (s.AutocompleteFromHistory)
        {
            foreach (var h in App.DataStore.History.Where(h =>
                (h.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                 h.Url.Contains(query, StringComparison.OrdinalIgnoreCase)) &&
                !built.Any(x => x.Url.Equals(h.Url, StringComparison.OrdinalIgnoreCase))).Take(5))
            {
                built.Add(CreateAutocompleteItem("Icon.History", h.Title, h.Url, h.Url, "History", fetchFavicon: true));
            }
        }

        var searchUrl = s.ResolveSearchUrl(query);
        built.Add(CreateAutocompleteItem("Icon.Search", $"Search {s.DefaultSearchEngine} for \"{query}\"", s.DefaultSearchEngine, searchUrl, "Search", fetchFavicon: true));

        _autocompleteItems.Clear();
        foreach (var item in built)
            _autocompleteItems.Add(item);

        _highlightedAutocompleteIndex = -1;
        AutocompletePopup.IsOpen = _autocompleteItems.Count > 0;
    }

    private AutocompleteItem CreateAutocompleteItem(string iconKey, string title, string subtitle, string url, string badge, bool fetchFavicon)
    {
        var cached = App.Favicons.GetCached(url);
        var cleanSub = subtitle.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? subtitle[8..].TrimEnd('/') : subtitle;
        var item = new AutocompleteItem(iconKey, string.IsNullOrWhiteSpace(title) ? cleanSub : title, cleanSub, url, badge, cached);

        if (cached is null && fetchFavicon && url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            _ = Task.Run(async () =>
            {
                var fetched = await App.Favicons.FetchDirectFaviconAsync(url);
                if (fetched is not null)
                    await Dispatcher.InvokeAsync(() => item.Favicon = fetched);
            });
        }

        return item;
    }

    private void AddressBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && AutocompletePopup.IsOpen && _autocompleteItems.Count > 0)
        {
            MoveAutocompleteHighlight(1);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Up && AutocompletePopup.IsOpen && _autocompleteItems.Count > 0)
        {
            MoveAutocompleteHighlight(-1);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Enter && _activeTab is not null)
        {
            string target = AddressBox.Text;
            if (AutocompletePopup.IsOpen && _highlightedAutocompleteIndex >= 0 && _highlightedAutocompleteIndex < _autocompleteItems.Count)
                target = _autocompleteItems[_highlightedAutocompleteIndex].Url;

            AutocompletePopup.IsOpen = false;
            _highlightedAutocompleteIndex = -1;
            NavigateTabCore(_activeTab, target);
            _activeTab.Browser?.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            AutocompletePopup.IsOpen = false;
            _highlightedAutocompleteIndex = -1;
            SyncToolbarToActiveTab();
            _activeTab?.Browser?.Focus();
            e.Handled = true;
        }
    }

    private void MoveAutocompleteHighlight(int delta)
    {
        if (_highlightedAutocompleteIndex >= 0 && _highlightedAutocompleteIndex < _autocompleteItems.Count)
            _autocompleteItems[_highlightedAutocompleteIndex].IsHighlighted = false;

        _highlightedAutocompleteIndex = (_highlightedAutocompleteIndex + delta + _autocompleteItems.Count) % _autocompleteItems.Count;
        _autocompleteItems[_highlightedAutocompleteIndex].IsHighlighted = true;
    }

    private void AutocompleteItem_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string url } && _activeTab is not null)
        {
            e.Handled = true;
            AutocompletePopup.IsOpen = false;
            _highlightedAutocompleteIndex = -1;
            NavigateTabCore(_activeTab, url);
            _activeTab.Browser?.Focus();
        }
    }

    // ==================== KEYBOARD SHORTCUTS ====================

    private async void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        var alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;

        if (ctrl && !shift && e.Key == Key.T) { e.Handled = true; await CreateTabAsync(App.SettingsService.Current.GetNewTabUrl(), true); }
        else if (ctrl && shift && e.Key == Key.T) { e.Handled = true; if (_closedTabs.TryPop(out var u)) await CreateTabAsync(u, true); }
        else if (ctrl && !shift && e.Key == Key.W) { e.Handled = true; if (_activeTab is not null) await CloseTabAsync(_activeTab); }
        else if (ctrl && !shift && e.Key == Key.N) { e.Handled = true; new MainWindow(false).Show(); }
        else if (ctrl && shift && e.Key == Key.N) { e.Handled = true; new MainWindow(true).Show(); }
        else if ((ctrl && e.Key == Key.L) || (alt && e.Key == Key.D)) { e.Handled = true; AddressBox.Focus(); AddressBox.SelectAll(); }
        else if ((ctrl && e.Key == Key.R) || e.Key == Key.F5) { e.Handled = true; ReloadButton_Click(sender, e); }
        else if (ctrl && e.Key == Key.F) { e.Handled = true; FindBar.Visibility = Visibility.Visible; FindBox.Focus(); FindBox.SelectAll(); }
        else if (ctrl && e.Key == Key.D) { e.Handled = true; ToggleBookmark_Click(sender, e); }
        else if (ctrl && e.Key == Key.H) { e.Handled = true; await OpenOrSwitchInternalTabAsync("aether://history"); }
        else if (ctrl && e.Key == Key.J) { e.Handled = true; await OpenOrSwitchInternalTabAsync("aether://downloads"); }
        else if (ctrl && e.Key == Key.OemComma) { e.Handled = true; await OpenOrSwitchInternalTabAsync("aether://settings"); }
        else if (ctrl && e.Key == Key.Tab) { e.Handled = true; await CycleTabAsync(shift ? -1 : 1); }
        else if (alt && e.Key == Key.Left) { e.Handled = true; BackButton_Click(sender, e); }
        else if (alt && e.Key == Key.Right) { e.Handled = true; ForwardButton_Click(sender, e); }
        else if (ctrl && (e.Key == Key.OemPlus || e.Key == Key.Add)) { e.Handled = true; AdjustZoom(0.1); }
        else if (ctrl && (e.Key == Key.OemMinus || e.Key == Key.Subtract)) { e.Handled = true; AdjustZoom(-0.1); }
        else if (ctrl && (e.Key == Key.D0 || e.Key == Key.NumPad0)) { e.Handled = true; SetZoom(1.0); }
        else if (e.Key == Key.F11) { e.Handled = true; ToggleFullscreen(); }
        else if (e.Key == Key.F12)
        {
            e.Handled = true;
            if (_activeTab?.Browser is { IsDisposed: false, IsBrowserInitialized: true } b)
                b.ShowDevTools();
        }
        else if (e.Key == Key.Escape && FindBar.Visibility == Visibility.Visible) { e.Handled = true; CloseFindBar(); }
        else if (ctrl && e.Key >= Key.D1 && e.Key <= Key.D9)
        {
            var idx = e.Key - Key.D1;
            if (idx < _tabs.Count) { e.Handled = true; await SelectTabAsync(_tabs[idx]); }
        }
    }

    private async Task CycleTabAsync(int delta)
    {
        if (_tabs.Count <= 1 || _activeTab is null) return;
        var idx = (_tabs.IndexOf(_activeTab) + delta + _tabs.Count) % _tabs.Count;
        await SelectTabAsync(_tabs[idx]);
    }

    private void ToggleFullscreen()
    {
        _isFullscreen = !_isFullscreen;
        if (_isFullscreen)
        {
            _preFullscreenState = WindowState;
            TopBarBorder.Visibility = Visibility.Collapsed;
            BookmarksBar.Visibility = Visibility.Collapsed;
            WindowState = WindowState.Maximized;
        }
        else
        {
            TopBarBorder.Visibility = Visibility.Visible;
            BookmarksBar.Visibility = App.SettingsService.Current.ShowBookmarksBar ? Visibility.Visible : Visibility.Collapsed;
            WindowState = _preFullscreenState;
        }
    }

    private async Task CloseTabAsync(BrowserTab tab)
    {
        var idx = _tabs.IndexOf(tab);
        if (idx < 0) return;

        if (!tab.IsBlankTab && !tab.IsInternalPage)
            _closedTabs.Push(tab.Url);

        DisposeTabBrowser(tab);
        _tabs.Remove(tab);

        if (_tabs.Count == 0)
        {
            Close();
            return;
        }

        if (tab == _activeTab)
        {
            _activeTab = null;
            await SelectTabAsync(_tabs[Math.Min(idx, _tabs.Count - 1)]);
        }

        if (App.SettingsService.Current.AggressiveGcOnTabClose)
            App.Memory.TrimWorkingSet(aggressiveGc: false);
    }

    private async Task OpenOrSwitchInternalTabAsync(string internalUrl)
    {
        var existing = _tabs.FirstOrDefault(t => t.Url.Equals(internalUrl, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            await SelectTabAsync(existing);
            NavigateTabCore(existing, internalUrl);
        }
        else
        {
            await CreateTabAsync(internalUrl, select: true);
        }
    }

    // ==================== UI BUTTON & POPUP HANDLERS ====================

    private async void NewTabButton_Click(object sender, RoutedEventArgs e) =>
        await CreateTabAsync(App.SettingsService.Current.GetNewTabUrl(), select: true);

    private async void CloseTabButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { Tag: BrowserTab tab })
            await CloseTabAsync(tab);
    }

    private async void TabItem_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInsideButton(e.OriginalSource as DependencyObject))
            return;

        if (sender is FrameworkElement { DataContext: BrowserTab tab })
        {
            if (e.ClickCount == 2 && App.SettingsService.Current.DoubleClickCloseTab)
            {
                await CloseTabAsync(tab);
                return;
            }

            _dragCandidateTab = tab;
            _dragStartPoint = e.GetPosition(this);
            _dragAnchorInWindow = _dragStartPoint;
            _isDraggingTab = false;
            _isDraggingSingleTabWindow = false;
            _isTearOffReady = false;
            _dropTargetWindow = null;

            await SelectTabAsync(tab);
        }
    }

    private void TabItem_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragCandidateTab is null || _isDraggingTab)
            return;

        var pos = e.GetPosition(this);
        if ((pos - _dragStartPoint).Length < 6)
            return;

        _isDraggingTab = true;
        _dragAnchorInWindow = pos;
        TabTearOffTitle.Text = _dragCandidateTab.DisplayTitle;
        CaptureMouse();
        Cursor = Cursors.Hand;

        if (_tabs.Count <= 1)
        {
            // Single-tab window: move the whole window smoothly AND allow docking back into any other Aether window!
            _isDraggingSingleTabWindow = true;
            var screenDip = ToScreenDip(pos);
            if (WindowState == WindowState.Maximized)
            {
                WindowState = WindowState.Normal;
                Left = screenDip.X - Math.Min(260, Width * 0.4);
                Top = screenDip.Y - 21;
                _dragAnchorInWindow = new Point(Math.Min(260, Width * 0.4), 21);
            }
            return;
        }

        // Multi-tab window: gently lift & scale the dragged tab for tactile feedback
        var container = GetTabContainer(_dragCandidateTab);
        if (container is not null)
        {
            _elevatedTabContainer = container;
            Panel.SetZIndex(container, 100);
            var (scale, _) = EnsureTabTransforms(container);
            AnimateDouble(scale, ScaleTransform.ScaleXProperty, 1.04, 120);
            AnimateDouble(scale, ScaleTransform.ScaleYProperty, 1.04, 120);
        }
    }

    private void Window_TabDragMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingTab || _dragCandidateTab is null)
            return;

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            CancelTabDrag();
            return;
        }

        var pos = e.GetPosition(this);
        var screenDip = ToScreenDip(pos);

        // CASE 1: Dragging a single-tab window -> glides with cursor & can dock right back into another Aether window!
        if (_isDraggingSingleTabWindow)
        {
            Left = screenDip.X - _dragAnchorInWindow.X;
            Top = screenDip.Y - _dragAnchorInWindow.Y;

            var target = FindDropTargetWindow(screenDip);
            UpdateDropTargetHighlight(target);

            if (target is not null)
            {
                _isTearOffReady = true;
                AnimateWindowOpacity(this, 0.52, 120);
                ShowTearOffPopup(screenDip, _dragCandidateTab.DisplayTitle, "Release to dock tab back into this window");
            }
            else
            {
                _isTearOffReady = false;
                AnimateWindowOpacity(this, 1.0, 120);
                HideTearOffPopup();
            }
            return;
        }

        // CASE 2: Dragging a tab inside a multi-tab window
        var isVertical = App.SettingsService.Current.VerticalTabs;
        bool outsideStrip = isVertical
            ? (pos.X > VerticalTabsSidebar.ActualWidth + 46 || pos.X < -24 || pos.Y < -24 || pos.Y > ActualHeight + 24)
            : (Math.Abs(pos.Y - (TopBarBorder.ActualHeight / 2.0)) > 44 || pos.X < -24 || pos.X > ActualWidth + 24);

        if (outsideStrip)
        {
            _isTearOffReady = true;

            if (_elevatedTabContainer is not null)
            {
                var (_, tr) = EnsureTabTransforms(_elevatedTabContainer);
                AnimateDouble(tr, TranslateTransform.XProperty, 0, 130);
                AnimateDouble(tr, TranslateTransform.YProperty, 0, 130);
                AnimateDouble(_elevatedTabContainer, OpacityProperty, 0.38, 120);
            }

            var target = FindDropTargetWindow(screenDip);
            UpdateDropTargetHighlight(target);
            var hint = target is not null
                ? "Release to dock tab into this window"
                : "Release to open in new window";
            ShowTearOffPopup(screenDip, _dragCandidateTab.DisplayTitle, hint);
            return;
        }

        // Back inside tab strip -> smooth 1:1 follow + live reorder animation!
        _isTearOffReady = false;
        UpdateDropTargetHighlight(null);
        HideTearOffPopup();

        var container = GetTabContainer(_dragCandidateTab);
        if (container is not null)
        {
            if (_elevatedTabContainer != container)
            {
                if (_elevatedTabContainer is not null) Panel.SetZIndex(_elevatedTabContainer, 0);
                _elevatedTabContainer = container;
                Panel.SetZIndex(container, 100);
            }
            container.BeginAnimation(OpacityProperty, null);
            container.Opacity = 1.0;

            var (_, tr) = EnsureTabTransforms(container);
            tr.BeginAnimation(TranslateTransform.XProperty, null);
            tr.BeginAnimation(TranslateTransform.YProperty, null);
            if (isVertical)
            {
                tr.X = 0;
                tr.Y = pos.Y - _dragAnchorInWindow.Y;
            }
            else
            {
                tr.X = pos.X - _dragAnchorInWindow.X;
                tr.Y = 0;
            }
        }

        ReorderDraggedTab(pos, isVertical);
    }

    private async void Window_TabDragMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingTab && _dragCandidateTab is not null)
        {
            _dragCandidateTab = null;
            return;
        }

        if (!_isDraggingTab || _dragCandidateTab is null)
            return;

        var draggedTab = _dragCandidateTab;
        var wasSingleTabWin = _isDraggingSingleTabWindow;
        var shouldDockOrTearOff = _isTearOffReady;
        var targetWin = _dropTargetWindow;
        var dropDip = ToScreenDip(e.GetPosition(this));

        CancelTabDrag(animateSpringBack: !shouldDockOrTearOff);

        // Single-tab window dropped over another Aether window -> dock it right back in!
        if (wasSingleTabWin)
        {
            if (shouldDockOrTearOff && targetWin is not null)
            {
                var insertIdx = targetWin.CalculateInsertIndexFromScreenDip(dropDip, draggedTab.IsPinned);
                await targetWin.AdoptTabAtAsync(draggedTab.Url, draggedTab.Title, draggedTab.IsPinned, insertIdx);
                targetWin.Activate();
                await RemoveMovedTabAsync(draggedTab);
            }
            return;
        }

        if (!shouldDockOrTearOff || _tabs.Count <= 1)
            return;

        var urlToMove = draggedTab.Url;
        var wasPinned = draggedTab.IsPinned;
        var titleToMove = draggedTab.Title;

        if (targetWin is not null)
        {
            var insertIdx = targetWin.CalculateInsertIndexFromScreenDip(dropDip, wasPinned);
            await targetWin.AdoptTabAtAsync(urlToMove, titleToMove, wasPinned, insertIdx);
            targetWin.Activate();
            await RemoveMovedTabAsync(draggedTab);
        }
        else
        {
            var newLeft = Math.Max(0, dropDip.X - 240);
            var newTop = Math.Max(0, dropDip.Y - 22);
            var newWin = new MainWindow(isPrivate: _isPrivate, initialUrl: urlToMove, initialLeft: newLeft, initialTop: newTop)
            {
                Opacity = 0
            };
            newWin.Show();
            newWin.Activate();
            AnimateWindowOpacity(newWin, 1.0, 160);
            await RemoveMovedTabAsync(draggedTab);
        }
    }

    private void ReorderDraggedTab(Point windowPos, bool isVertical)
    {
        if (_dragCandidateTab is null) return;
        var curIdx = _tabs.IndexOf(_dragCandidateTab);
        if (curIdx < 0) return;

        var list = isVertical ? VerticalTabsList : HorizontalTabsList;
        for (int i = 0; i < _tabs.Count; i++)
        {
            if (i == curIdx) continue;
            if (_tabs[i].IsPinned != _dragCandidateTab.IsPinned) continue;

            if (list.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement neighbor)
                continue;

            var origin = neighbor.TranslatePoint(new Point(0, 0), this);
            bool shouldSwap = isVertical
                ? (i < curIdx && windowPos.Y < origin.Y + neighbor.ActualHeight / 2.0) ||
                  (i > curIdx && windowPos.Y > origin.Y + neighbor.ActualHeight / 2.0)
                : (i < curIdx && windowPos.X < origin.X + neighbor.ActualWidth / 2.0) ||
                  (i > curIdx && windowPos.X > origin.X + neighbor.ActualWidth / 2.0);

            if (!shouldSwap)
                continue;

            var slotStep = isVertical
                ? (neighbor.ActualHeight > 0 ? neighbor.ActualHeight : 38)
                : (neighbor.ActualWidth > 0 ? neighbor.ActualWidth : EffectiveTabWidth + 5);
            var direction = i > curIdx ? 1 : -1;

            _tabs.Move(curIdx, i);

            // Re-anchor dragged tab so it remains seamlessly glued under the cursor without jumping
            if (isVertical)
                _dragAnchorInWindow = new Point(_dragAnchorInWindow.X, _dragAnchorInWindow.Y + direction * slotStep);
            else
                _dragAnchorInWindow = new Point(_dragAnchorInWindow.X + direction * slotStep, _dragAnchorInWindow.Y);

            // Smoothly slide the displaced neighbor(s) into their new slots!
            var minMoved = Math.Min(curIdx, i);
            var maxMoved = Math.Max(curIdx, i);
            var draggedRef = _dragCandidateTab;
            _ = Dispatcher.BeginInvoke(() =>
            {
                for (int k = minMoved; k <= maxMoved; k++)
                {
                    if (k < 0 || k >= _tabs.Count || _tabs[k] == draggedRef) continue;
                    if (list.ItemContainerGenerator.ContainerFromIndex(k) is FrameworkElement shifted)
                    {
                        var (_, tr) = EnsureTabTransforms(shifted);
                        var prop = isVertical ? TranslateTransform.YProperty : TranslateTransform.XProperty;
                        tr.BeginAnimation(prop, null);
                        if (isVertical) tr.Y = direction * slotStep;
                        else tr.X = direction * slotStep;
                        AnimateDouble(tr, prop, 0, 180);
                    }
                }
            }, DispatcherPriority.Loaded);

            return;
        }
    }

    public int CalculateInsertIndexFromScreenDip(Point screenDip, bool isPinned)
    {
        var isVertical = App.SettingsService.Current.VerticalTabs;
        var list = isVertical ? VerticalTabsList : HorizontalTabsList;
        var localPos = FromScreenDip(screenDip);

        int firstMatching = _tabs.Count;
        int lastMatching = -1;
        for (int i = 0; i < _tabs.Count; i++)
        {
            if (_tabs[i].IsPinned != isPinned) continue;
            if (firstMatching == _tabs.Count) firstMatching = i;
            lastMatching = i;

            if (list.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container)
                continue;
            var origin = container.TranslatePoint(new Point(0, 0), this);
            var mid = isVertical
                ? origin.Y + container.ActualHeight / 2.0
                : origin.X + container.ActualWidth / 2.0;
            var cursorCoord = isVertical ? localPos.Y : localPos.X;
            if (cursorCoord < mid)
                return i;
        }

        return lastMatching >= 0 ? lastMatching + 1 : (isPinned ? 0 : _tabs.Count);
    }

    public async Task AdoptTabAtAsync(string url, string title, bool isPinned, int insertIndex)
    {
        var s = App.SettingsService.Current;
        var tab = new BrowserTab
        {
            Url = string.IsNullOrWhiteSpace(url) ? "about:blank" : url,
            Title = string.IsNullOrWhiteSpace(title) ? (url == "about:blank" ? "New Tab" : url) : title,
            IsPinned = isPinned,
            ZoomFactor = s.DefaultZoomPercent / 100.0
        };

        var clampedIdx = Math.Clamp(insertIndex, 0, _tabs.Count);
        _tabs.Insert(clampedIdx, tab);
        await SelectTabAsync(tab);

        // Smooth pop-in slide animation on the newly docked tab!
        _ = Dispatcher.BeginInvoke(() =>
        {
            var container = GetTabContainer(tab);
            if (container is not null)
            {
                var (scale, tr) = EnsureTabTransforms(container);
                scale.ScaleX = 0.88;
                scale.ScaleY = 0.88;
                tr.Y = -8;
                AnimateDouble(scale, ScaleTransform.ScaleXProperty, 1.0, 180);
                AnimateDouble(scale, ScaleTransform.ScaleYProperty, 1.0, 180);
                AnimateDouble(tr, TranslateTransform.YProperty, 0, 180);
            }
        }, DispatcherPriority.Loaded);
    }

    public void SetDockHighlight(bool active)
    {
        AnimateDouble(HorizontalDockHighlight, OpacityProperty, active ? 0.9 : 0.0, 140);
    }

    private void UpdateDropTargetHighlight(MainWindow? newTarget)
    {
        if (_dropTargetWindow == newTarget) return;
        _dropTargetWindow?.SetDockHighlight(false);
        _dropTargetWindow = newTarget;
        _dropTargetWindow?.SetDockHighlight(true);
    }

    private void ShowTearOffPopup(Point screenDip, string title, string hint)
    {
        TabTearOffTitle.Text = title;
        TabTearOffHint.Text = hint;
        TabTearOffPopup.HorizontalOffset = screenDip.X + 14;
        TabTearOffPopup.VerticalOffset = screenDip.Y + 14;

        if (!TabTearOffPopup.IsOpen)
        {
            TabTearOffPopup.IsOpen = true;
            TabTearOffBorder.Opacity = 0;
            TabTearOffScale.ScaleX = 0.88;
            TabTearOffScale.ScaleY = 0.88;
            AnimateDouble(TabTearOffBorder, OpacityProperty, 0.96, 140);
            AnimateDouble(TabTearOffScale, ScaleTransform.ScaleXProperty, 1.0, 140);
            AnimateDouble(TabTearOffScale, ScaleTransform.ScaleYProperty, 1.0, 140);
        }
    }

    private void HideTearOffPopup()
    {
        if (TabTearOffPopup.IsOpen)
            TabTearOffPopup.IsOpen = false;
    }

    private async Task RemoveMovedTabAsync(BrowserTab tab)
    {
        var idx = _tabs.IndexOf(tab);
        if (idx < 0) return;

        DisposeTabBrowser(tab);
        _tabs.Remove(tab);

        if (_tabs.Count == 0)
        {
            Close();
            return;
        }

        if (tab == _activeTab)
        {
            _activeTab = null;
            await SelectTabAsync(_tabs[Math.Min(idx, _tabs.Count - 1)]);
        }
    }

    private void CancelTabDrag(bool animateSpringBack = true)
    {
        var elevated = _elevatedTabContainer;
        _elevatedTabContainer = null;

        if (_isDraggingSingleTabWindow)
            AnimateWindowOpacity(this, 1.0, 120);

        UpdateDropTargetHighlight(null);
        HideTearOffPopup();

        _dragCandidateTab = null;
        _isDraggingTab = false;
        _isDraggingSingleTabWindow = false;
        _isTearOffReady = false;

        if (IsMouseCaptured)
            ReleaseMouseCapture();
        Cursor = null;

        if (elevated is not null)
        {
            var (scale, tr) = EnsureTabTransforms(elevated);
            if (animateSpringBack)
            {
                AnimateDouble(tr, TranslateTransform.XProperty, 0, 160);
                AnimateDouble(tr, TranslateTransform.YProperty, 0, 160);
                AnimateDouble(scale, ScaleTransform.ScaleXProperty, 1.0, 160);
                AnimateDouble(scale, ScaleTransform.ScaleYProperty, 1.0, 160);
                AnimateDouble(elevated, OpacityProperty, 1.0, 140);
            }
            else
            {
                tr.BeginAnimation(TranslateTransform.XProperty, null);
                tr.BeginAnimation(TranslateTransform.YProperty, null);
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                elevated.BeginAnimation(OpacityProperty, null);
                tr.X = 0; tr.Y = 0; scale.ScaleX = 1; scale.ScaleY = 1; elevated.Opacity = 1;
            }
            Panel.SetZIndex(elevated, 0);
        }
    }

    private FrameworkElement? GetTabContainer(BrowserTab tab)
    {
        var idx = _tabs.IndexOf(tab);
        if (idx < 0) return null;
        var list = App.SettingsService.Current.VerticalTabs ? VerticalTabsList : HorizontalTabsList;
        return list.ItemContainerGenerator.ContainerFromIndex(idx) as FrameworkElement;
    }

    private static (ScaleTransform Scale, TranslateTransform Translate) EnsureTabTransforms(FrameworkElement el)
    {
        if (el.RenderTransform is TransformGroup tg && tg.Children.Count == 2 &&
            tg.Children[0] is ScaleTransform s && tg.Children[1] is TranslateTransform t)
            return (s, t);

        var scale = new ScaleTransform(1, 1);
        var translate = new TranslateTransform(0, 0);
        var group = new TransformGroup();
        group.Children.Add(scale);
        group.Children.Add(translate);
        el.RenderTransform = group;
        return (scale, translate);
    }

    private static void AnimateDouble(IAnimatable target, DependencyProperty prop, double to, int durationMs)
    {
        var anim = new DoubleAnimation
        {
            To = to,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        target.BeginAnimation(prop, anim);
    }

    private static void AnimateWindowOpacity(Window win, double to, int durationMs)
    {
        var anim = new DoubleAnimation
        {
            To = to,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        win.BeginAnimation(OpacityProperty, anim);
    }

    private Point ToScreenDip(Point windowPoint)
    {
        var physical = PointToScreen(windowPoint);
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is not null)
            return source.CompositionTarget.TransformFromDevice.Transform(physical);
        return physical;
    }

    private Point FromScreenDip(Point screenDip)
    {
        var source = PresentationSource.FromVisual(this);
        var physical = source?.CompositionTarget is not null
            ? source.CompositionTarget.TransformToDevice.Transform(screenDip)
            : screenDip;
        return PointFromScreen(physical);
    }

    private MainWindow? FindDropTargetWindow(Point screenDip)
    {
        foreach (var win in Application.Current.Windows.OfType<MainWindow>())
        {
            if (win == this || !win.IsVisible || win.WindowState == WindowState.Minimized)
                continue;
            if (win._isPrivate != _isPrivate)
                continue;

            // Exact DPI-aware screen origin works for Normal, Maximized, and Multi-Monitor setups!
            var topLeft = win.ToScreenDip(new Point(0, 0));
            bool inTopBar = screenDip.X >= topLeft.X - 12 &&
                            screenDip.X <= topLeft.X + win.ActualWidth + 12 &&
                            screenDip.Y >= topLeft.Y - 16 &&
                            screenDip.Y <= topLeft.Y + 86;

            bool inVerticalSidebar = App.SettingsService.Current.VerticalTabs &&
                                     screenDip.X >= topLeft.X - 12 &&
                                     screenDip.X <= topLeft.X + win.VerticalTabsSidebar.ActualWidth + 32 &&
                                     screenDip.Y >= topLeft.Y &&
                                     screenDip.Y <= topLeft.Y + win.ActualHeight;

            if (inTopBar || inVerticalSidebar)
                return win;
        }
        return null;
    }

    private static bool IsInsideButton(DependencyObject? d)
    {
        while (d is not null)
        {
            if (d is Button) return true;
            d = VisualTreeHelper.GetParent(d);
        }
        return false;
    }

    private async void TabItem_MouseMiddleDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && App.SettingsService.Current.MiddleClickCloseTab &&
            sender is FrameworkElement { DataContext: BrowserTab tab })
        {
            e.Handled = true;
            await CloseTabAsync(tab);
        }
    }

    private void HorizontalTabs_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is ScrollViewer sv)
        {
            sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta * 0.4);
            e.Handled = true;
        }
    }

    // Tab Context Menu handlers
    private static BrowserTab? TabFromMenu(object sender) =>
        ((sender as MenuItem)?.Parent as ContextMenu)?.Tag as BrowserTab;

    private async void TabMenu_Duplicate_Click(object sender, RoutedEventArgs e) { if (TabFromMenu(sender) is { } t) await CreateTabAsync(t.Url, true); }
    private void TabMenu_Pin_Click(object sender, RoutedEventArgs e)
    {
        if (TabFromMenu(sender) is { } t)
        {
            t.IsPinned = !t.IsPinned;
            RecalculateEffectiveTabWidth();
        }
    }
    private void TabMenu_Mute_Click(object sender, RoutedEventArgs e)
    {
        if (TabFromMenu(sender) is { Browser: { IsDisposed: false, IsBrowserInitialized: true } browser } t)
        {
            var host = browser.GetBrowserHost();
            if (host is not null)
            {
                var nextMuted = !t.IsMuted;
                host.SetAudioMuted(nextMuted);
                t.IsMuted = nextMuted;
            }
        }
    }
    private async void TabMenu_Sleep_Click(object sender, RoutedEventArgs e)
    {
        if (TabFromMenu(sender) is { } t && t != _activeTab)
            await App.Memory.TrySleepTabAsync(t);
    }
    private void TabMenu_Discard_Click(object sender, RoutedEventArgs e)
    {
        if (TabFromMenu(sender) is { } t && t != _activeTab)
            App.Memory.DiscardTab(t, x => { if (x.Browser is not null) WebViewHostGrid.Children.Remove(x.Browser); });
    }
    private async void TabMenu_Close_Click(object sender, RoutedEventArgs e) { if (TabFromMenu(sender) is { } t) await CloseTabAsync(t); }
    private async void TabMenu_CloseOthers_Click(object sender, RoutedEventArgs e)
    {
        if (TabFromMenu(sender) is not { } keep) return;
        foreach (var other in _tabs.Where(x => x != keep && !x.IsPinned).ToList())
            await CloseTabAsync(other);
    }

    // Navigation buttons
    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab?.Browser is { IsDisposed: false, IsBrowserInitialized: true } b && b.CanGoBack)
            b.Back();
    }
    private void ForwardButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab?.Browser is { IsDisposed: false, IsBrowserInitialized: true } b && b.CanGoForward)
            b.Forward();
    }
    private void ReloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab is null) return;
        if (_activeTab.IsInternalPage || _activeTab.IsBlankTab)
        {
            NavigateTabCore(_activeTab, _activeTab.Url);
        }
        else if (_activeTab.IsLoading)
        {
            if (_activeTab.Browser is { IsDisposed: false, IsBrowserInitialized: true } b)
                b.Stop();
            _activeTab.IsLoading = false;
            FinishAddressProgressAnimation();
            SyncToolbarToActiveTab();
        }
        else
        {
            UpdateAddressProgress(0.08, isLoading: true);
            ReloadBrowser(_activeTab);
        }
    }
    private void HomeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab is not null)
            NavigateTabCore(_activeTab, App.SettingsService.Current.HomePageUrl);
    }

    private void ToggleVerticalTabs_Click(object sender, RoutedEventArgs e) =>
        App.SettingsService.Update(s => s.VerticalTabs = !s.VerticalTabs);

    private void ToggleBookmark_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab is null || _activeTab.IsBlankTab || _activeTab.IsInternalPage) return;
        App.DataStore.ToggleBookmark(_activeTab.Url, _activeTab.Title);
        UpdateBookmarkStar(_activeTab);
    }

    private void BookmarkBarItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url } && _activeTab is not null)
            NavigateTabCore(_activeTab, url);
    }

    private static CustomPopupPlacement[] RightAlignPopupBelow(Size popupSize, Size targetSize, Point offset)
    {
        // Right edge of popup aligns flush with the right edge of its trigger button, 6px below it.
        var x = targetSize.Width - popupSize.Width;
        var y = targetSize.Height + 6;
        return new[]
        {
            new CustomPopupPlacement(new Point(x, y), PopupPrimaryAxis.Horizontal)
        };
    }

    private static CustomPopupPlacement[] LeftAlignPopupBelow(Size popupSize, Size targetSize, Point offset)
    {
        // Left edge of popup aligns flush with the left edge of its trigger control, 6px below it.
        var y = targetSize.Height + 6;
        return new[]
        {
            new CustomPopupPlacement(new Point(0, y), PopupPrimaryAxis.Horizontal)
        };
    }

    private void PrivacyShield_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab is null) return;
        var s = App.SettingsService.Current;
        var host = Uri.TryCreate(_activeTab.Url, UriKind.Absolute, out var u) && u.Scheme.StartsWith("http") ? u.Host : "Internal / Blank Page";
        var paused = s.CustomAllowedDomains.Contains(host, StringComparer.OrdinalIgnoreCase);
        var adBlockOn = s.AdTrackerBlockerEnabled && s.BlockAds && s.BlockerMode != "Off";

        PrivacyPopupHost.Text = host;
        PrivacyPopupCount.Text = $"{_activeTab.BlockedCount} blocked";
        PrivacyPopupRulesInfo.Text = $"{App.AdBlocker.TotalRuleCount:N0} active adblock rules · {App.Privacy.TotalBlockedLifetime:N0} blocked total";
        AdBlockerMasterText.Text = adBlockOn ? "AdBlocker: ON" : "AdBlocker: OFF";
        AdBlockerMasterBtn.Background = (Brush)FindResource(adBlockOn ? "AccentSoftBrush" : "Surface2Brush");

        PrivacyToggleSiteBtn.Tag = host;
        PrivacyToggleSiteBtn.IsEnabled = host.Contains('.');
        PrivacyToggleSiteText.Text = paused ? "Resume on site" : "Pause on site";
        UpdateAdBlockPopupText.Text = "Update Community Filter Lists";
        PrivacyBlockedList.ItemsSource = _activeTab.BlockedDomains.Count > 0
            ? _activeTab.BlockedDomains.ToList()
            : new List<string> { "No ads or trackers detected on this tab." };

        PrivacyPopup.IsOpen = true;
    }

    private void AdBlockerMasterToggle_Click(object sender, RoutedEventArgs e)
    {
        App.SettingsService.Update(s =>
        {
            var currentlyOn = s.AdTrackerBlockerEnabled && s.BlockAds && s.BlockerMode != "Off";
            if (currentlyOn)
            {
                s.BlockAds = false;
                s.AdTrackerBlockerEnabled = false;
            }
            else
            {
                s.AdTrackerBlockerEnabled = true;
                s.BlockAds = true;
                if (s.BlockerMode == "Off") s.BlockerMode = "Strict";
            }
        });

        PrivacyPopup.IsOpen = false;
        if (_activeTab is not null)
        {
            UpdateShieldBadge(_activeTab);
            ReloadBrowser(_activeTab);
        }
    }

    private async void UpdateAdBlockListsPopup_Click(object sender, RoutedEventArgs e)
    {
        UpdateAdBlockPopupText.Text = "Downloading filter lists...";
        var (_, msg) = await App.AdBlocker.UpdateCommunityFilterListsAsync();
        PrivacyPopupRulesInfo.Text = $"{App.AdBlocker.TotalRuleCount:N0} active adblock rules · {App.Privacy.TotalBlockedLifetime:N0} blocked total";
        UpdateAdBlockPopupText.Text = msg;
    }

    private void PrivacyToggleSite_Click(object sender, RoutedEventArgs e)
    {
        if (PrivacyToggleSiteBtn.Tag is not string host || !host.Contains('.')) return;
        App.SettingsService.Update(s =>
        {
            if (s.CustomAllowedDomains.Contains(host, StringComparer.OrdinalIgnoreCase))
                s.CustomAllowedDomains.RemoveAll(x => x.Equals(host, StringComparison.OrdinalIgnoreCase));
            else
                s.CustomAllowedDomains.Add(host);
        });
        PrivacyPopup.IsOpen = false;
        ReloadBrowser(_activeTab);
    }

    private void ExtensionsButton_Click(object sender, RoutedEventArgs e)
    {
        var exts = App.DataStore.Extensions.Where(x => x.IsEnabled).ToList();
        ExtensionsPopupList.ItemsSource = exts;
        ExtensionsEmptyText.Visibility = exts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ExtensionsPopup.IsOpen = true;
    }

    private async void ExtensionPopupItem_Click(object sender, RoutedEventArgs e)
    {
        ExtensionsPopup.IsOpen = false;
        if (sender is Button { Tag: string id })
        {
            var rec = App.DataStore.Extensions.FirstOrDefault(x => x.Id == id);
            var url = rec is not null ? App.Extensions.GetExtensionPageUrl(rec) : null;
            if (!string.IsNullOrEmpty(url))
                await CreateTabAsync(url, select: true);
            else
                await OpenOrSwitchInternalTabAsync("aether://extensions");
        }
    }

    private async void OpenExtensionsPage_Click(object sender, RoutedEventArgs e)
    {
        ExtensionsPopup.IsOpen = false;
        MainMenuPopup.IsOpen = false;
        await OpenOrSwitchInternalTabAsync("aether://extensions");
    }

    private async void OpenPrivacySettings_Click(object sender, RoutedEventArgs e)
    {
        PrivacyPopup.IsOpen = false;
        await OpenOrSwitchInternalTabAsync("aether://settings");
    }

    private async void MemoryBadge_Click(object sender, RoutedEventArgs e) =>
        await OpenOrSwitchInternalTabAsync("aether://settings");

    private void DownloadsButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshDownloadsUi();
        DownloadsPopup.IsOpen = !DownloadsPopup.IsOpen;
    }

    private void RefreshDownloadsUi()
    {
        var recent = App.DataStore.Downloads.Take(20).ToList();
        DownloadsPopupList.ItemsSource = null;
        DownloadsPopupList.ItemsSource = recent;
        DownloadsEmptyText.Visibility = recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DownloadActiveDot.Visibility = recent.Any(d => d.IsInProgress) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DownloadsPopup_OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        DownloadsPopup.IsOpen = false;
        var dir = App.SettingsService.Current.DownloadDirectory;
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
        }
    }

    private void DownloadsPopup_Clear_Click(object sender, RoutedEventArgs e)
    {
        App.DataStore.ClearDownloads();
        RefreshDownloadsUi();
    }

    private async void DownloadsPopup_ViewAll_Click(object sender, RoutedEventArgs e)
    {
        DownloadsPopup.IsOpen = false;
        await OpenOrSwitchInternalTabAsync("aether://downloads");
    }

    private void DownloadPopupItem_OpenFile_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string filePath } && File.Exists(filePath))
        {
            DownloadsPopup.IsOpen = false;
            try { Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true }); } catch { }
        }
    }

    private void DownloadPopupItem_ShowInFolder_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { Tag: string filePath } && !string.IsNullOrWhiteSpace(filePath))
        {
            DownloadsPopup.IsOpen = false;
            var arg = File.Exists(filePath) ? $"/select,\"{filePath}\"" : Path.GetDirectoryName(filePath) ?? "";
            if (!string.IsNullOrEmpty(arg))
                Process.Start("explorer.exe", arg);
        }
    }

    private void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        MenuSetDefaultBtn.Visibility = DefaultBrowserService.IsDefaultBrowser() ? Visibility.Collapsed : Visibility.Visible;
        MainMenuPopup.IsOpen = true;
    }

    private void Menu_SetDefaultBrowser_Click(object sender, RoutedEventArgs e)
    {
        MainMenuPopup.IsOpen = false;
        DefaultBrowserService.PromptSetAsDefaultBrowser();
    }

    private async void Menu_NewTab_Click(object sender, RoutedEventArgs e) { MainMenuPopup.IsOpen = false; await CreateTabAsync(App.SettingsService.Current.GetNewTabUrl(), true); }
    private void Menu_NewWindow_Click(object sender, RoutedEventArgs e) { MainMenuPopup.IsOpen = false; new MainWindow(false).Show(); }
    private void Menu_NewPrivateWindow_Click(object sender, RoutedEventArgs e) { MainMenuPopup.IsOpen = false; new MainWindow(true).Show(); }
    private async void Menu_History_Click(object sender, RoutedEventArgs e) { MainMenuPopup.IsOpen = false; await OpenOrSwitchInternalTabAsync("aether://history"); }
    private async void Menu_Bookmarks_Click(object sender, RoutedEventArgs e) { MainMenuPopup.IsOpen = false; await OpenOrSwitchInternalTabAsync("aether://bookmarks"); }
    private async void Menu_Downloads_Click(object sender, RoutedEventArgs e) { MainMenuPopup.IsOpen = false; await OpenOrSwitchInternalTabAsync("aether://downloads"); }
    private async void Menu_SleepInactive_Click(object sender, RoutedEventArgs e) { MainMenuPopup.IsOpen = false; await SleepAllInactiveTabsAsync(); App.Memory.TrimWorkingSet(true); }
    private async void Menu_CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        MainMenuPopup.IsOpen = false;
        var info = await App.Updates.CheckForUpdatesAsync(manual: true);
        if (!info.IsUpdateAvailable)
            await OpenOrSwitchInternalTabAsync("aether://settings");
    }
    private void Menu_DevTools_Click(object sender, RoutedEventArgs e)
    {
        MainMenuPopup.IsOpen = false;
        if (_activeTab?.Browser is { IsDisposed: false, IsBrowserInitialized: true } b)
            b.ShowDevTools();
    }
    private async void OpenSettings_Click(object sender, RoutedEventArgs e) { MainMenuPopup.IsOpen = false; await OpenOrSwitchInternalTabAsync("aether://settings"); }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => AdjustZoom(0.1);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => AdjustZoom(-0.1);
    private void ResetZoom_Click(object sender, RoutedEventArgs e) => SetZoom(1.0);

    private void AdjustZoom(double delta)
    {
        if (_activeTab is null) return;
        SetZoom(Math.Clamp(_activeTab.ZoomFactor + delta, 0.5, 2.5));
    }

    private void SetZoom(double factor)
    {
        if (_activeTab is null) return;
        _activeTab.ZoomFactor = factor;
        if (_activeTab.Browser is { IsDisposed: false, IsBrowserInitialized: true } b)
            b.SetZoomLevel(CefHostService.ZoomFactorToCefLevel(factor));
        UpdateZoomUi(factor);
    }

    // Native CEF Find in page
    private void FindBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ExecuteFind(forward: (Keyboard.Modifiers & ModifierKeys.Shift) == 0);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseFindBar();
            e.Handled = true;
        }
    }
    private void FindNext_Click(object sender, RoutedEventArgs e) => ExecuteFind(forward: true);
    private void FindClose_Click(object sender, RoutedEventArgs e) => CloseFindBar();

    private void CloseFindBar()
    {
        FindBar.Visibility = Visibility.Collapsed;
        _lastFindQuery = null;
        if (_activeTab?.Browser is { IsDisposed: false, IsBrowserInitialized: true } b)
            b.StopFinding(clearSelection: true);
    }

    private void ExecuteFind(bool forward = true)
    {
        var q = FindBox.Text;
        if (_activeTab?.Browser is not { IsDisposed: false, IsBrowserInitialized: true } b)
            return;

        if (string.IsNullOrEmpty(q))
        {
            _lastFindQuery = null;
            b.StopFinding(clearSelection: true);
            return;
        }

        bool findNext = string.Equals(_lastFindQuery, q, StringComparison.Ordinal);
        _lastFindQuery = q;
        b.Find(q, forward: forward, matchCase: false, findNext: findNext);
    }

    // Update Banner
    private async void InstallUpdateBanner_Click(object sender, RoutedEventArgs e)
    {
        UpdateBannerText.Text = "Downloading update...";
        var (ok, msg) = await App.Updates.DownloadAndApplyUpdateAsync(autoRestart: true);
        if (!ok) UpdateBannerText.Text = msg;
    }
    private void DismissUpdateBanner_Click(object sender, RoutedEventArgs e) =>
        UpdateBanner.Visibility = Visibility.Collapsed;

    // Window Caption Controls
    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseWindowButton_Click(object sender, RoutedEventArgs e) => Close();

    private sealed class AutocompleteItem : INotifyPropertyChanged
    {
        private ImageSource? _favicon;
        private bool _isHighlighted;

        public string IconKey { get; }
        public string Title { get; }
        public string Subtitle { get; }
        public string Url { get; }
        public string Badge { get; }

        public ImageSource? Favicon
        {
            get => _favicon;
            set
            {
                if (_favicon != value)
                {
                    _favicon = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Favicon)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasFavicon)));
                }
            }
        }

        public bool HasFavicon => _favicon is not null;

        public bool IsHighlighted
        {
            get => _isHighlighted;
            set
            {
                if (_isHighlighted != value)
                {
                    _isHighlighted = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsHighlighted)));
                }
            }
        }

        public AutocompleteItem(string iconKey, string title, string subtitle, string url, string badge, ImageSource? favicon)
        {
            IconKey = iconKey;
            Title = title;
            Subtitle = subtitle;
            Url = url;
            Badge = badge;
            _favicon = favicon;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
