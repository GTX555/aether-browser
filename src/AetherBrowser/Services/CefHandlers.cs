using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Input;
using AetherBrowser.Models;
using CefSharp;
using CefSharp.Handler;
using CefSharp.Wpf.HwndHost;
using AetherSettings = AetherBrowser.Models.BrowserSettings;

namespace AetherBrowser.Services;

/// <summary>
/// Network-level privacy shield, HTTPS-Only upgrade, tracking-parameter URL cleaner,
/// middle-click / Ctrl+click new-tab interceptor, and early privacy script injector.
/// </summary>
public sealed class AetherRequestHandler : RequestHandler
{
    private readonly BrowserTab _tab;
    private readonly AetherResourceRequestHandler _resourceHandler;

    public event Action<string, string>? RequestBlocked; // (host, reason)
    public event Action<string, bool>? OpenUrlInNewTabRequested; // (url, switchToTab)

    public AetherRequestHandler(BrowserTab tab)
    {
        _tab = tab;
        _resourceHandler = new AetherResourceRequestHandler(tab, (host, reason) => RequestBlocked?.Invoke(host, reason));
    }

    protected override bool OnBeforeBrowse(
        IWebBrowser chromiumWebBrowser,
        IBrowser browser,
        IFrame frame,
        IRequest request,
        bool userGesture,
        bool isRedirect)
    {
        if (!frame.IsMain)
            return false;

        var url = request.Url;
        if (string.IsNullOrWhiteSpace(url) || IsInternalOrSpecialUrl(url))
            return false;

        var s = App.SettingsService.Current;

        // HTTPS-Only automatic upgrade (skip localhost / loopback)
        if (s.HttpsOnlyMode &&
            url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !IsLocalHost(url))
        {
            var upgraded = "https://" + url[7..];
            frame.LoadUrl(upgraded);
            return true;
        }

        // Strip tracking query parameters (utm_*, fbclid, gclid, etc.)
        var cleaned = App.Privacy.CleanTrackingUrl(url, s);
        if (!string.Equals(cleaned, url, StringComparison.Ordinal))
        {
            frame.LoadUrl(cleaned);
            return true;
        }

        return false;
    }

    protected override bool OnOpenUrlFromTab(
        IWebBrowser chromiumWebBrowser,
        IBrowser browser,
        IFrame frame,
        string targetUrl,
        WindowOpenDisposition targetDisposition,
        bool userGesture)
    {
        if (string.IsNullOrWhiteSpace(targetUrl))
            return false;

        if (targetDisposition is WindowOpenDisposition.NewForegroundTab
            or WindowOpenDisposition.NewBackgroundTab
            or WindowOpenDisposition.NewWindow
            or WindowOpenDisposition.NewPopup)
        {
            var switchToTab = targetDisposition != WindowOpenDisposition.NewBackgroundTab
                              && App.SettingsService.Current.SwitchToNewTab;
            OpenUrlInNewTabRequested?.Invoke(targetUrl, switchToTab);
            return true;
        }

        return false;
    }

    protected override void OnDocumentAvailableInMainFrame(IWebBrowser chromiumWebBrowser, IBrowser browser)
    {
        base.OnDocumentAvailableInMainFrame(chromiumWebBrowser, browser);
        InjectPrivacyScript(browser.MainFrame, App.SettingsService.Current);
    }

    protected override IResourceRequestHandler? GetResourceRequestHandler(
        IWebBrowser chromiumWebBrowser,
        IBrowser browser,
        IFrame frame,
        IRequest request,
        bool isNavigation,
        bool isDownload,
        string requestInitiator,
        ref bool disableDefaultHandling)
    {
        var url = request.Url;
        if (string.IsNullOrEmpty(url) ||
            (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
             !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        return _resourceHandler;
    }

    /// <summary>
    /// Injects Aether's anti-fingerprinting, GPC/DNT navigator, WebRTC, animation, and cosmetic adblock script
    /// into web frames as early as possible (skips internal aether://, about:blank, chrome://, data: pages).
    /// </summary>
    public static void InjectPrivacyScript(IFrame? frame, AetherSettings settings)
    {
        if (frame is null || !frame.IsValid)
            return;

        var url = frame.Url;
        if (string.IsNullOrWhiteSpace(url) || IsInternalOrSpecialUrl(url))
            return;

        try
        {
            var script = App.Privacy.BuildPrivacyInjectionScript(settings);
            if (!string.IsNullOrEmpty(script))
                frame.ExecuteJavaScriptAsync(script);
        }
        catch { }
    }

    internal static bool IsInternalOrSpecialUrl(string url) =>
        url.Equals("about:blank", StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith("aether://", StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith("chrome://", StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith("devtools://", StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith("blob:", StringComparison.OrdinalIgnoreCase);

    internal static bool IsLocalHost(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        return u.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
               u.Host == "127.0.0.1" ||
               u.Host == "::1";
    }
}

/// <summary>
/// Per-resource interceptor: blocks ads/trackers/telemetry/cryptominers via App.Privacy.ShouldBlockRequest,
/// enforces Ultra-Lite font/image blocking, and injects Sec-GPC, DNT, and Referrer policies.
/// </summary>
public sealed class AetherResourceRequestHandler : ResourceRequestHandler
{
    private readonly BrowserTab _tab;
    private readonly Action<string, string>? _onBlocked;

    public AetherResourceRequestHandler(BrowserTab tab, Action<string, string>? onBlocked = null)
    {
        _tab = tab;
        _onBlocked = onBlocked;
    }

    protected override CefReturnValue OnBeforeResourceLoad(
        IWebBrowser chromiumWebBrowser,
        IBrowser browser,
        IFrame frame,
        IRequest request,
        IRequestCallback callback)
    {
        var s = App.SettingsService.Current;
        var url = request.Url;

        // Ultra-Lite mode resource blocking
        if (s.BlockWebFonts && request.ResourceType == ResourceType.FontResource)
            return CefReturnValue.Cancel;

        if (s.BlockImagesDataSaver && request.ResourceType == ResourceType.Image)
            return CefReturnValue.Cancel;

        // Ad, Tracker, Telemetry & Cryptominer Shield
        if (App.Privacy.ShouldBlockRequest(url, _tab.Url, s, out var reason))
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var blockedUri) && !string.IsNullOrEmpty(blockedUri.Host))
                _onBlocked?.Invoke(blockedUri.Host, reason);
            return CefReturnValue.Cancel;
        }

        // Privacy headers: Sec-GPC, DNT & Referrer Policy
        try
        {
            if (s.SendGlobalPrivacyControl)
                request.SetHeaderByName("Sec-GPC", "1", overwrite: true);

            if (s.SendDoNotTrack)
                request.SetHeaderByName("DNT", "1", overwrite: true);

            switch (s.ReferrerPolicy)
            {
                case "no-referrer":
                    request.SetReferrer("", ReferrerPolicy.NoReferrer);
                    break;
                case "same-origin":
                    if (!string.IsNullOrEmpty(request.ReferrerUrl))
                        request.SetReferrer(request.ReferrerUrl, ReferrerPolicy.ClearReferrerOnTransitionCrossOrigin);
                    break;
                case "origin":
                    if (!string.IsNullOrEmpty(request.ReferrerUrl))
                        request.SetReferrer(request.ReferrerUrl, ReferrerPolicy.Origin);
                    break;
                case "strict-origin-when-cross-origin":
                    if (!string.IsNullOrEmpty(request.ReferrerUrl))
                        request.SetReferrer(request.ReferrerUrl, ReferrerPolicy.ReduceReferrerGranularityOnTransitionCrossOrigin);
                    break;
            }
        }
        catch { }

        return CefReturnValue.Continue;
    }
}

/// <summary>
/// Intercepts popups (honoring BlockPopups) and routes allowed new-window requests into Aether tabs.
/// </summary>
public sealed class AetherLifeSpanHandler : LifeSpanHandler
{
    public event Action<string, bool>? PopupRequested; // (targetUrl, switchToTab)

    protected override bool OnBeforePopup(
        IWebBrowser chromiumWebBrowser,
        IBrowser browser,
        IFrame frame,
        string targetUrl,
        string targetFrameName,
        WindowOpenDisposition targetDisposition,
        bool userGesture,
        IPopupFeatures popupFeatures,
        IWindowInfo windowInfo,
        IBrowserSettings browserSettings,
        ref bool noJavascriptAccess,
        out IWebBrowser? newBrowser)
    {
        newBrowser = null;
        var s = App.SettingsService.Current;

        if (s.BlockPopups && !userGesture)
            return true;

        if (!string.IsNullOrWhiteSpace(targetUrl))
        {
            var switchToTab = targetDisposition != WindowOpenDisposition.NewBackgroundTab && s.SwitchToNewTab;
            PopupRequested?.Invoke(targetUrl, switchToTab);
        }

        return true; // Always handle in Aether tab strip rather than spawning unmanaged CEF popups
    }
}

/// <summary>
/// Feeds CEF downloads directly into App.DataStore and DownloadsPopup with live ReceivedBytes/TotalBytes updates.
/// </summary>
public sealed class AetherDownloadHandler : DownloadHandler
{
    private readonly ConcurrentDictionary<int, DownloadRecord> _active = new();

    public event Action<DownloadRecord>? DownloadStarted;
    public event Action<DownloadRecord>? DownloadUpdated;

    protected override bool CanDownload(IWebBrowser chromiumWebBrowser, IBrowser browser, string url, string requestMethod) =>
        true;

    protected override bool OnBeforeDownload(
        IWebBrowser chromiumWebBrowser,
        IBrowser browser,
        DownloadItem downloadItem,
        IBeforeDownloadCallback callback)
    {
        var s = App.SettingsService.Current;
        var suggestedName = string.IsNullOrWhiteSpace(downloadItem.SuggestedFileName)
            ? "download"
            : downloadItem.SuggestedFileName;

        var downloadDir = string.IsNullOrWhiteSpace(s.DownloadDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
            : s.DownloadDirectory;

        try { Directory.CreateDirectory(downloadDir); } catch { }

        var targetPath = s.AskDownloadLocation
            ? Path.Combine(downloadDir, suggestedName)
            : GetUniqueFilePath(downloadDir, suggestedName);

        var record = new DownloadRecord
        {
            FileName = Path.GetFileName(targetPath),
            FilePath = targetPath,
            Url = downloadItem.Url ?? "",
            TotalBytes = Math.Max(0, downloadItem.TotalBytes),
            ReceivedBytes = Math.Max(0, downloadItem.ReceivedBytes),
            State = "InProgress"
        };

        _active[downloadItem.Id] = record;

        RunOnUi(() =>
        {
            App.DataStore.UpsertDownload(record);
            DownloadStarted?.Invoke(record);
        });

        if (!callback.IsDisposed)
        {
            using (callback)
            {
                callback.Continue(targetPath, showDialog: s.AskDownloadLocation);
            }
        }

        return true; // Suppress Chrome's built-in download shelf; use Aether's native DownloadsPopup
    }

    protected override void OnDownloadUpdated(
        IWebBrowser chromiumWebBrowser,
        IBrowser browser,
        DownloadItem downloadItem,
        IDownloadItemCallback callback)
    {
        if (!_active.TryGetValue(downloadItem.Id, out var record))
            return;

        var received = Math.Max(0, downloadItem.ReceivedBytes);
        var total = Math.Max(0, downloadItem.TotalBytes);
        var fullPath = downloadItem.FullPath;

        string state;
        bool terminal = false;
        if (downloadItem.IsComplete)
        {
            state = "Completed";
            terminal = true;
        }
        else if (downloadItem.IsCancelled)
        {
            state = "Cancelled";
            terminal = true;
        }
        else if (downloadItem.IsInterrupted)
        {
            state = "Interrupted";
            terminal = true;
        }
        else
        {
            state = "InProgress";
        }

        if (terminal)
            _active.TryRemove(downloadItem.Id, out _);

        RunOnUi(() =>
        {
            if (!string.IsNullOrWhiteSpace(fullPath))
            {
                record.FilePath = fullPath;
                record.FileName = Path.GetFileName(fullPath);
            }
            if (total > 0)
                record.TotalBytes = total;
            record.ReceivedBytes = received;
            record.State = state;

            if (terminal)
                App.DataStore.UpsertDownload(record);

            DownloadUpdated?.Invoke(record);
        });
    }

    private static string GetUniqueFilePath(string dir, string fileName)
    {
        var candidate = Path.Combine(dir, fileName);
        if (!File.Exists(candidate))
            return candidate;

        var name = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (int i = 1; i < 1000; i++)
        {
            candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate))
                return candidate;
        }
        return Path.Combine(dir, $"{name}_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}{ext}");
    }

    private static void RunOnUi(Action action)
    {
        var disp = Application.Current?.Dispatcher;
        if (disp is null || disp.CheckAccess()) action();
        else disp.BeginInvoke(action);
    }
}

/// <summary>
/// Surfaces real Chromium display signals: page title, address, real 0..1 loading progress,
/// favicon URLs, hover status URL, and HTML5 fullscreen state.
/// </summary>
public sealed class AetherDisplayHandler : DisplayHandler
{
    public event Action<string>? TitleChanged;
    public event Action<string>? AddressChanged;
    public event Action<double>? LoadingProgressChanged; // Real 0.0 .. 1.0
    public event Action<IList<string>>? FaviconUrlChanged;
    public event Action<string>? StatusMessageChanged;
    public event Action<bool>? FullscreenModeChanged;

    protected override void OnTitleChanged(IWebBrowser chromiumWebBrowser, TitleChangedEventArgs titleChangedArgs)
    {
        base.OnTitleChanged(chromiumWebBrowser, titleChangedArgs);
        TitleChanged?.Invoke(titleChangedArgs.Title ?? "");
    }

    protected override void OnAddressChanged(IWebBrowser chromiumWebBrowser, AddressChangedEventArgs addressChangedArgs)
    {
        base.OnAddressChanged(chromiumWebBrowser, addressChangedArgs);
        AddressChanged?.Invoke(addressChangedArgs.Address ?? "");
    }

    protected override void OnLoadingProgressChange(IWebBrowser chromiumWebBrowser, IBrowser browser, double progress)
    {
        base.OnLoadingProgressChange(chromiumWebBrowser, browser, progress);
        LoadingProgressChanged?.Invoke(Math.Clamp(progress, 0.0, 1.0));
    }

    protected override void OnFaviconUrlChange(IWebBrowser chromiumWebBrowser, IBrowser browser, IList<string> urls)
    {
        base.OnFaviconUrlChange(chromiumWebBrowser, browser, urls);
        if (urls is { Count: > 0 })
            FaviconUrlChanged?.Invoke(urls);
    }

    protected override void OnStatusMessage(IWebBrowser chromiumWebBrowser, StatusMessageEventArgs statusMessageArgs)
    {
        base.OnStatusMessage(chromiumWebBrowser, statusMessageArgs);
        StatusMessageChanged?.Invoke(statusMessageArgs.Value ?? "");
    }

    protected override void OnFullscreenModeChange(IWebBrowser chromiumWebBrowser, IBrowser browser, bool fullscreen)
    {
        base.OnFullscreenModeChange(chromiumWebBrowser, browser, fullscreen);
        FullscreenModeChanged?.Invoke(fullscreen);
    }
}

/// <summary>
/// Enforces Aether's per-capability privacy permission rules (Notifications, Geolocation, Camera, Microphone, Clipboard).
/// </summary>
public sealed class AetherPermissionHandler : PermissionHandler
{
    protected override bool OnShowPermissionPrompt(
        IWebBrowser chromiumWebBrowser,
        IBrowser browser,
        ulong promptId,
        string requestingOrigin,
        PermissionRequestType requestedPermissions,
        IPermissionPromptCallback callback)
    {
        var s = App.SettingsService.Current;
        string policy = "Ask";

        if (requestedPermissions.HasFlag(PermissionRequestType.Notifications))
            policy = s.PermissionNotifications;
        else if (requestedPermissions.HasFlag(PermissionRequestType.Geolocation))
            policy = s.PermissionGeolocation;
        else if (requestedPermissions.HasFlag(PermissionRequestType.CameraStream))
            policy = s.PermissionCamera;
        else if (requestedPermissions.HasFlag(PermissionRequestType.MicStream))
            policy = s.PermissionMicrophone;
        else if (requestedPermissions.ToString().Contains("Clipboard", StringComparison.OrdinalIgnoreCase))
            policy = s.PermissionClipboard;

        if (policy == "Allow")
        {
            using (callback) { callback.Continue(PermissionRequestResult.Accept); }
            return true;
        }

        if (policy == "Block")
        {
            using (callback) { callback.Continue(PermissionRequestResult.Deny); }
            return true;
        }

        return false; // "Ask" -> let Chrome runtime display native prompt
    }

    protected override bool OnRequestMediaAccessPermission(
        IWebBrowser chromiumWebBrowser,
        IBrowser browser,
        IFrame frame,
        string requestingOrigin,
        MediaAccessPermissionType requestedPermissions,
        IMediaAccessCallback callback)
    {
        var s = App.SettingsService.Current;
        var wantsVideo = requestedPermissions.HasFlag(MediaAccessPermissionType.VideoCapture);
        var wantsAudio = requestedPermissions.HasFlag(MediaAccessPermissionType.AudioCapture);

        var allowVideo = !wantsVideo || s.PermissionCamera == "Allow";
        var denyVideo = wantsVideo && s.PermissionCamera == "Block";
        var allowAudio = !wantsAudio || s.PermissionMicrophone == "Allow";
        var denyAudio = wantsAudio && s.PermissionMicrophone == "Block";

        if (denyVideo || denyAudio)
        {
            using (callback) { callback.Cancel(); }
            return true;
        }

        if (allowVideo && allowAudio)
        {
            using (callback) { callback.Continue(requestedPermissions); }
            return true;
        }

        return false; // "Ask" -> native prompt
    }
}

/// <summary>
/// JS-to-C# bridge registered as "aetherBridge" on ChromiumWebBrowser.JavascriptObjectRepository.
/// Internal pages (aether://settings, etc.) invoke window.aetherBridge.postMessage(json).
/// </summary>
public sealed class AetherJsBridge
{
    public const string ObjectName = "aetherBridge";

    public event Action<string>? MessageReceived;

    /// <summary>
    /// Exposed to JavaScript as window.aetherBridge.postMessage(json) via CefSharp's default camelCase converter.
    /// </summary>
    public void PostMessage(string json)
    {
        if (!string.IsNullOrWhiteSpace(json))
            MessageReceived?.Invoke(json);
    }

    /// <summary>
    /// Registers this bridge synchronously on window.aetherBridge so internal pages can call it immediately on load.
    /// Must be called before the browser initializes or navigates.
    /// </summary>
    public static AetherJsBridge RegisterOn(ChromiumWebBrowser browser)
    {
        var bridge = new AetherJsBridge();
        browser.JavascriptObjectRepository.Settings.LegacyBindingEnabled = true;
        browser.JavascriptObjectRepository.Register(ObjectName, bridge, options: BindingOptions.DefaultBinder);
        return bridge;
    }
}

/// <summary>
/// Intercepts global browser keyboard shortcuts directly inside CEF before webpages consume them
/// (ensures Ctrl+T, Ctrl+W, Ctrl+R, F5, Ctrl+L, Ctrl+Tab, Ctrl+1..9, etc. work even when a webpage has focus).
/// </summary>
public sealed class AetherKeyboardHandler : KeyboardHandler
{
    private readonly Func<Key, bool, bool, bool, bool> _isHandledShortcut;
    private readonly Action<Key, bool, bool, bool> _onShortcut;

    public AetherKeyboardHandler(
        Func<Key, bool, bool, bool, bool> isHandledShortcut,
        Action<Key, bool, bool, bool> onShortcut)
    {
        _isHandledShortcut = isHandledShortcut;
        _onShortcut = onShortcut;
    }

    protected override bool OnPreKeyEvent(
        IWebBrowser chromiumWebBrowser,
        IBrowser browser,
        KeyType type,
        int windowsKeyCode,
        int nativeKeyCode,
        CefEventFlags modifiers,
        bool isSystemKey,
        ref bool isKeyboardShortcut)
    {
        if (type != KeyType.RawKeyDown && type != KeyType.KeyUp)
            return false;

        var ctrl = (modifiers & CefEventFlags.ControlDown) != 0;
        var shift = (modifiers & CefEventFlags.ShiftDown) != 0;
        var alt = (modifiers & CefEventFlags.AltDown) != 0;

        // Never swallow AltGr (Ctrl+Alt on European/German keyboards for @, €, [, ], {, }, \)
        if (ctrl && alt)
            return false;

        var key = KeyInterop.KeyFromVirtualKey(windowsKeyCode);
        if (key == Key.None)
            return false;

        if (!_isHandledShortcut(key, ctrl, shift, alt))
            return false;

        isKeyboardShortcut = true;
        if (type == KeyType.RawKeyDown)
            _onShortcut(key, ctrl, shift, alt);
        return true;
    }
}

/// <summary>
/// Notifies WPF when the embedded CEF HWND takes keyboard focus (e.g., clicking into a webpage)
/// so the address bar clears its focus ring immediately.
/// </summary>
public sealed class AetherFocusHandler : CefSharp.Wpf.HwndHost.FocusHandler
{
    public event Action? BrowserGotFocus;

    protected override void OnGotFocus(IWebBrowser chromiumWebBrowser, IBrowser browser)
    {
        base.OnGotFocus(chromiumWebBrowser, browser);
        BrowserGotFocus?.Invoke();
    }
}
