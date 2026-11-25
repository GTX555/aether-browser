using System.IO;
using System.Windows;
using System.Windows.Media;
using CefSharp;
using CefSharp.Handler;
using CefSharp.Wpf.HwndHost;
using AetherSettings = AetherBrowser.Models.BrowserSettings;
using CefBrowserSettings = CefSharp.BrowserSettings;

namespace AetherBrowser.Services;

/// <summary>
/// Manages the 100% open-source Chromium Embedded Framework (CEF) lifecycle,
/// Chrome Runtime configuration (for Manifest V3 extensions), zero-telemetry switches,
/// lightweight RAM/cache tuning, and isolated in-memory RequestContexts for Private Windows.
/// </summary>
public sealed class CefHostService
{
    private readonly object _initLock = new();
    private bool _initialized;
    private bool _shutdown;

    public string UserDataFolder { get; }
    public string CachePath { get; }
    public bool IsInitialized => _initialized && Cef.IsInitialized == true;
    public string ChromiumVersion => IsInitialized ? Cef.ChromiumVersion : "Chromium (CEF)";
    public string CefVersion => IsInitialized ? Cef.CefVersion : "";
    public string EngineVersion => IsInitialized
        ? $"Chromium {Cef.ChromiumVersion} (Open-Source CEF {Cef.CefVersion})"
        : "Open-Source CEF / Chromium";

    public CefHostService(string dataDirectory)
    {
        UserDataFolder = Path.Combine(dataDirectory, "CefData");
        CachePath = Path.Combine(UserDataFolder, "Default");
        Directory.CreateDirectory(UserDataFolder);
        Directory.CreateDirectory(CachePath);
    }

    /// <summary>
    /// Initializes CEF exactly once with Chrome Runtime style, zero-telemetry switches,
    /// RAM/cache optimizations, DoH, WebRTC protection, and persisted MV3 extensions.
    /// </summary>
    public bool Initialize(AetherSettings settings, PrivacyEngine privacy, ExtensionService? extensions = null)
    {
        lock (_initLock)
        {
            if (_initialized || Cef.IsInitialized == true)
            {
                _initialized = true;
                return true;
            }

            // Use Alloy runtime style on CEF's Chrome bootstrap so browsers embed cleanly inside WPF HwndHost
            // without spawning rogue standalone Chrome_WidgetWin_1 windows or Chrome SessionService takeover,
            // while still preserving --load-extension support and clean shutdown.
            CefSharpSettings.RuntimeStyle = CefRuntimeStyle.Alloy;
            CefSharpSettings.ShutdownOnExit = false; // Explicitly called on App.OnExit
            CefSharpSettings.SubprocessExitIfParentProcessClosed = true;

            // Remove any stale Chromium Sessions directory so Chrome SessionService never restores ghost windows
            try
            {
                var sessionsDir = Path.Combine(CachePath, "Sessions");
                if (Directory.Exists(sessionsDir))
                    Directory.Delete(sessionsDir, recursive: true);
            }
            catch { }

            var isGerman = !string.IsNullOrWhiteSpace(settings.Language) &&
                           settings.Language.Equals("de", StringComparison.OrdinalIgnoreCase);
            var locale = isGerman ? "de" : "en-US"; // Matches CEF locales\de.pak and locales\en-US.pak
            var acceptLang = isGerman
                ? "de-DE,de;q=0.9,en-US;q=0.8,en;q=0.7"
                : "en-US,en;q=0.9";

            var cefSettings = new CefSettings
            {
                RootCachePath = UserDataFolder,
                CachePath = CachePath,
                PersistSessionCookies = !settings.ClearCookiesOnExit,
                Locale = locale,
                AcceptLanguageList = acceptLang,
                LogSeverity = LogSeverity.Disable,
                BackgroundColor = ParseCefColor(InternalPagesService.GetThemePalette(settings).Bg)
            };

            var ua = privacy.ResolveUserAgent(settings);
            if (!string.IsNullOrEmpty(ua))
                cefSettings.UserAgent = ua;

            var args = cefSettings.CefCommandLineArgs;

            // --- Zero-telemetry & De-Googled privacy defaults ---
            args["disable-background-networking"] = "1";
            args["disable-sync"] = "1";
            args["disable-component-update"] = "1";
            args["disable-domain-reliability"] = "1";
            args["disable-client-side-phishing-detection"] = "1";
            args["no-pings"] = "1";
            args["disable-breakpad"] = "1";
            args["disable-crash-reporter"] = "1";
            args["metrics-recording-only"] = "1";
            args["disable-default-apps"] = "1";
            args["disable-translate"] = "1";
            args["no-first-run"] = "1";
            args["no-default-browser-check"] = "1";
            args["disable-session-crashed-bubble"] = "1";
            args["process-per-site"] = "1";

            var disabledFeatures = new List<string>
            {
                "TranslateUI",
                "InterestFeedContentSuggestions",
                "OptimizationHints",
                "MediaRouter",
                "AutofillServerCommunication",
                "CertificateTransparencyComponentUpdater",
                "PrivacySandboxSettings4",
                "BrowsingTopics",
                "AttributionReportingCrossAppWeb",
                "Fledge",
                "InterestGroupStorage"
            };

            if (settings.DisablePreconnect)
            {
                args["dns-prefetch-disable"] = "1";
                disabledFeatures.Add("NetworkPrediction");
                disabledFeatures.Add("PreconnectToSearch");
            }

            if (settings.LowEndMemoryMode)
            {
                args["enable-low-end-device-mode"] = "1";
                disabledFeatures.Add("BackForwardCache");
                cefSettings.JavascriptFlags = "--optimize-for-size";
            }

            args["disable-features"] = string.Join(",", disabledFeatures);

            if (settings.ThrottleBackgroundTabs)
            {
                args["enable-features"] = "IntensiveWakeUpThrottling";
            }

            // --- Disk & media cache budget ---
            long diskCacheBytes = settings.DiskCacheSizeMb <= 0
                ? 1L
                : (long)Math.Clamp(settings.DiskCacheSizeMb, 1, 2048) * 1024L * 1024L;
            args["disk-cache-size"] = diskCacheBytes.ToString();
            args["media-cache-size"] = Math.Min(diskCacheBytes, 16L * 1024L * 1024L).ToString();

            // --- GPU / scrolling / media ---
            if (!settings.HardwareAcceleration)
                cefSettings.DisableGpuAcceleration();

            if (!settings.SmoothScrolling)
                args["disable-smooth-scrolling"] = "1";

            if (settings.BlockAutoplayMedia)
                args["autoplay-policy"] = "document-user-gesture-required";

            // --- Renderer process cap ---
            if (settings.MaxRendererProcesses > 0)
                args["renderer-process-limit"] = Math.Clamp(settings.MaxRendererProcesses, 1, 32).ToString();

            // --- WebRTC IP leak protection ---
            if (settings.BlockWebRtcIpLeak)
                args["force-webrtc-ip-handling-policy"] = "disable_non_proxied_udp";

            // --- DNS over HTTPS (DoH) ---
            var dohTemplate = ResolveDohTemplate(settings);
            if (!string.IsNullOrEmpty(dohTemplate))
            {
                args["dns-over-https-mode"] = "secure";
                args["dns-over-https-templates"] = dohTemplate;
            }

            // --- Manifest V3 Extensions (--load-extension) ---
            if (settings.ExtensionsEnabled && extensions is not null)
            {
                var extArg = extensions.GetLoadExtensionCommandLineArg();
                if (!string.IsNullOrEmpty(extArg))
                    args["load-extension"] = extArg;
            }
            else if (!settings.ExtensionsEnabled)
            {
                args["disable-extensions"] = "1";
            }

            // --- Theme color scheme hint ---
            if (settings.Theme.Equals("Light", StringComparison.OrdinalIgnoreCase))
                args["force-dark-mode"] = "0";

            _initialized = Cef.Initialize(cefSettings, performDependencyCheck: true, browserProcessHandler: new AetherBrowserProcessHandler());

            if (_initialized)
                ApplyRuntimePreferences(Cef.GetGlobalRequestContext(), settings);

            return _initialized;
        }
    }

    /// <summary>
    /// Creates an isolated in-memory RequestContext for Private Windows (zero disk cache, zero persisted cookies/storage).
    /// </summary>
    public IRequestContext CreatePrivateRequestContext(AetherSettings settings)
    {
        var isGerman = !string.IsNullOrWhiteSpace(settings.Language) &&
                       settings.Language.Equals("de", StringComparison.OrdinalIgnoreCase);

        var rcSettings = new RequestContextSettings
        {
            CachePath = string.Empty, // Empty CachePath => isolated in-memory incognito RequestContext
            PersistSessionCookies = false,
            AcceptLanguageList = isGerman
                ? "de-DE,de;q=0.9,en-US;q=0.8,en;q=0.7"
                : "en-US,en;q=0.9"
        };

        var ctx = new RequestContext(rcSettings);
        ApplyRuntimePreferences(ctx, settings);
        return ctx;
    }

    public IRequestContext? GetGlobalRequestContext() =>
        IsInitialized ? Cef.GetGlobalRequestContext() : null;

    /// <summary>
    /// Builds per-browser CEF BrowserSettings reflecting current JavaScript, Ultra-Lite, and theme preferences.
    /// </summary>
    public static IBrowserSettings CreateBrowserSettings(AetherSettings settings)
    {
        return new CefBrowserSettings
        {
            Javascript = settings.JavaScriptEnabled ? CefState.Enabled : CefState.Disabled,
            ImageLoading = settings.BlockImagesDataSaver ? CefState.Disabled : CefState.Enabled,
            RemoteFonts = settings.BlockWebFonts ? CefState.Disabled : CefState.Enabled,
            LocalStorage = CefState.Enabled,
            WebGl = CefState.Enabled,
            ChromeStatusBubble = CefState.Disabled, // Aether renders its own minimalist hover URL pill
            ChromeZoomBubble = CefState.Disabled,   // Aether renders its own zoom pill badge
            BackgroundColor = ParseCefColor(InternalPagesService.GetThemePalette(settings).Bg)
        };
    }

    /// <summary>
    /// Applies live Chromium RequestContext preferences on the CEF UI thread (cookies, DNT, autofill, passwords, SafeBrowsing, preconnect, WebRTC).
    /// </summary>
    public static void ApplyRuntimePreferences(IRequestContext? context, AetherSettings settings)
    {
        if (context is null || context.IsDisposed || Cef.IsInitialized != true)
            return;

        Cef.PostAction(CefThreadIds.TID_UI, () =>
        {
            if (context.IsDisposed) return;

            TrySetPref(context, "session.restore_on_startup", 5); // Never restore previous Chromium windows
            TrySetPref(context, "profile.block_third_party_cookies", settings.BlockThirdPartyCookies);
            TrySetPref(context, "profile.cookie_controls_mode", settings.BlockThirdPartyCookies ? 1 : 0);
            TrySetPref(context, "enable_do_not_track", settings.SendDoNotTrack);
            TrySetPref(context, "autofill.enabled", !settings.DisableAutofill);
            TrySetPref(context, "autofill.profile_enabled", !settings.DisableAutofill);
            TrySetPref(context, "autofill.credit_card_enabled", !settings.DisableAutofill);
            TrySetPref(context, "credentials_enable_service", !settings.DisablePasswordSaving);
            TrySetPref(context, "credentials_enable_autosignin", !settings.DisablePasswordSaving);
            TrySetPref(context, "safebrowsing.enabled", !settings.DisableSmartScreen);
            TrySetPref(context, "net.network_prediction_options", settings.DisablePreconnect ? 2 : 0);
            TrySetPref(context, "webrtc.ip_handling_policy",
                settings.BlockWebRtcIpLeak ? "disable_non_proxied_udp" : "default");

            if (!string.IsNullOrWhiteSpace(settings.DownloadDirectory) && Directory.Exists(settings.DownloadDirectory))
                TrySetPref(context, "download.default_directory", settings.DownloadDirectory);
        });
    }

    /// <summary>
    /// Pushes live settings updates to an existing open ChromiumWebBrowser tab and its RequestContext.
    /// </summary>
    public static void ApplyLiveSettings(ChromiumWebBrowser? browser, AetherSettings settings)
    {
        if (browser is null || browser.IsDisposed)
            return;

        var ctx = browser.RequestContext ?? (Cef.IsInitialized == true ? Cef.GetGlobalRequestContext() : null);
        ApplyRuntimePreferences(ctx, settings);
    }

    /// <summary>
    /// Clears cookies and/or HTTP cache for the given RequestContext (or the global RequestContext if null).
    /// </summary>
    public static async Task ClearBrowsingDataAsync(IRequestContext? context, bool clearCookies, bool clearCache)
    {
        if (Cef.IsInitialized != true)
            return;

        var targetCtx = context ?? Cef.GetGlobalRequestContext();
        if (targetCtx is null || targetCtx.IsDisposed)
            return;

        try
        {
            if (clearCookies)
            {
                var cm = targetCtx.GetCookieManager(null);
                if (cm is not null)
                    await cm.DeleteCookiesAsync("", "");
            }

            if (clearCache)
            {
                Cef.PostAction(CefThreadIds.TID_UI, () =>
                {
                    if (targetCtx.IsDisposed) return;
                    targetCtx.ClearHttpCache(null);
                    targetCtx.ClearHttpAuthCredentials(null);
                    targetCtx.ClearCertificateExceptions(null);
                });
            }
        }
        catch { }
    }

    /// <summary>
    /// Converts a human zoom factor (e.g. 1.0 = 100%, 1.2 = 120%) to CEF logarithmic ZoomLevel (0.0 = 100%).
    /// </summary>
    public static double ZoomFactorToCefLevel(double factor) =>
        Math.Log(Math.Clamp(factor, 0.25, 5.0)) / Math.Log(1.2);

    /// <summary>
    /// Converts a CEF logarithmic ZoomLevel (0.0 = 100%) back to a human zoom factor (1.0 = 100%).
    /// </summary>
    public static double CefLevelToZoomFactor(double cefLevel) =>
        Math.Clamp(Math.Pow(1.2, cefLevel), 0.25, 5.0);

    public static uint ParseCefColor(string hex)
    {
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            return Cef.ColorSetARGB(c.A, c.R, c.G, c.B);
        }
        catch
        {
            return Cef.ColorSetARGB(255, 13, 14, 17);
        }
    }

    public void Shutdown()
    {
        lock (_initLock)
        {
            if (_shutdown) return;
            _shutdown = true;

            try
            {
                if (Cef.IsInitialized == true)
                    Cef.Shutdown();
            }
            catch { }
        }
    }

    private static void TrySetPref(IRequestContext ctx, string name, object value)
    {
        try
        {
            if (ctx.CanSetPreference(name))
                ctx.SetPreference(name, value, out _);
        }
        catch { }
    }

    private static string ResolveDohTemplate(AetherSettings settings) => settings.DnsOverHttpsProvider switch
    {
        "Cloudflare" => "https://cloudflare-dns.com/dns-query",
        "Quad9" => "https://dns.quad9.net/dns-query",
        "Mullvad" => "https://dns.mullvad.net/dns-query",
        "AdGuard" => "https://dns.adguard-dns.com/dns-query",
        "Custom" when !string.IsNullOrWhiteSpace(settings.CustomDohUrl) => settings.CustomDohUrl.Trim(),
        _ => ""
    };
}

/// <summary>
/// Defense-in-depth handler: prevents Chromium's internal ProcessSingleton from ever spawning a
/// raw standalone Chromium window if a second launch somehow bypasses SingleInstanceService.
/// </summary>
internal sealed class AetherBrowserProcessHandler : BrowserProcessHandler
{
    protected override bool OnAlreadyRunningAppRelaunch(IReadOnlyDictionary<string, string> commandLine, string currentDirectory)
    {
        try
        {
            var argsList = new List<string>();
            if (commandLine is not null)
            {
                foreach (var kv in commandLine)
                {
                    if (!string.IsNullOrWhiteSpace(kv.Value)) argsList.Add(kv.Value);
                    if (!string.IsNullOrWhiteSpace(kv.Key)) argsList.Add(kv.Key);
                }
            }

            Application.Current?.Dispatcher.BeginInvoke(async () =>
                await App.HandleSecondaryLaunchAsync(argsList.ToArray()));
        }
        catch { }

        return true; // Handled by Aether — never spawn a raw Chromium window!
    }
}
