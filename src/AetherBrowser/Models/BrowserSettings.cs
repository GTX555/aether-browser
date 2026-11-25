using System.IO;
using System.Text.Json.Serialization;

namespace AetherBrowser.Models;

public sealed class BrowserSettings
{
    // --- Schema ---
    public int SettingsSchemaVersion { get; set; } = 2;

    // --- General ---
    public string Language { get; set; } = "en";
    public string StartupMode { get; set; } = "BlankTab"; // BlankTab, RestoreSession, SpecificUrls
    public string StartupUrls { get; set; } = "";
    public string HomePageUrl { get; set; } = "about:blank";
    public bool ConfirmCloseMultipleTabs { get; set; } = false;
    public bool AskDownloadLocation { get; set; } = false;
    public string DownloadDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    public bool HardwareAcceleration { get; set; } = true;
    public bool SmoothScrolling { get; set; } = true;
    public bool RestoreWindowBounds { get; set; } = true;
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 820;
    public bool WindowMaximized { get; set; } = false;
    public bool ShowStatusBar { get; set; } = false;

    // --- Appearance & Minimal UI ---
    public string Theme { get; set; } = "Dark"; // Dark, Amoled, Light
    public string AccentColor { get; set; } = "#A1A1AA";
    public bool CompactMode { get; set; } = false;
    public bool VerticalTabs { get; set; } = false;
    public double SidebarWidth { get; set; } = 220;
    public bool ShowBookmarksBar { get; set; } = false;
    public bool ShowHomeButton { get; set; } = false;
    public bool ShowDownloadsButton { get; set; } = true;
    public bool ShowExtensionsButton { get; set; } = true;
    public bool ShowPrivacyShieldButton { get; set; } = true;
    public bool ShowMemoryBadge { get; set; } = true;
    public bool ShowSidebarToggleButton { get; set; } = true;
    public int DefaultZoomPercent { get; set; } = 100;
    public int TabMaxWidth { get; set; } = 156;
    public int AddressBarWidth { get; set; } = 320;
    public bool ShowCloseButtonOnInactiveTabs { get; set; } = false;
    public bool RoundedAddressBar { get; set; } = true;

    // --- Memory & Performance (Lightweight Engine) ---
    public string MemoryTargetLevel { get; set; } = "Low"; // Low, Normal
    public bool EnableAutoSleepTabs { get; set; } = true;
    public int AutoSleepMinutes { get; set; } = 3;
    public bool EnableAutoDiscardTabs { get; set; } = true;
    public int AutoDiscardMinutes { get; set; } = 15;
    public bool ExcludePinnedFromSleep { get; set; } = true;
    public bool ExcludeAudioFromSleep { get; set; } = true;
    public bool LazyLoadRestoredTabs { get; set; } = true;
    public int MaxRendererProcesses { get; set; } = 0; // 0 = auto, or 2..16
    public bool AggressiveGcOnTabClose { get; set; } = true;
    public bool TrimWorkingSetOnMinimize { get; set; } = true;
    public bool BlockWebFonts { get; set; } = false;
    public bool BlockImagesDataSaver { get; set; } = false;
    public bool DisableWebAnimations { get; set; } = false;
    public int DiskCacheSizeMb { get; set; } = 50;
    public bool LowEndMemoryMode { get; set; } = true;
    public bool ThrottleBackgroundTabs { get; set; } = true;
    public bool DisablePreconnect { get; set; } = true;

    // --- Privacy & Security ---
    public bool AdTrackerBlockerEnabled { get; set; } = true;
    public string BlockerMode { get; set; } = "Strict"; // Off, Standard, Strict, Aggressive
    public bool BlockAds { get; set; } = true;
    public bool BlockTrackers { get; set; } = true;
    public bool BlockTelemetry { get; set; } = true;
    public bool BlockCryptominers { get; set; } = true;
    public bool BlockSocialWidgets { get; set; } = false;
    public bool BlockAnnoyances { get; set; } = true;
    public List<string> CustomBlockedDomains { get; set; } = new();
    public List<string> CustomAllowedDomains { get; set; } = new();
    public bool StripTrackingParameters { get; set; } = true;
    public bool AntiFingerprintCanvas { get; set; } = true;
    public bool AntiFingerprintWebGL { get; set; } = true;
    public bool AntiFingerprintAudio { get; set; } = true;
    public bool SpoofHardwareInfo { get; set; } = true;
    public bool BlockWebRtcIpLeak { get; set; } = true;
    public bool SendGlobalPrivacyControl { get; set; } = true;
    public bool SendDoNotTrack { get; set; } = true;
    public string ReferrerPolicy { get; set; } = "strict-origin-when-cross-origin"; // no-referrer, same-origin, origin, strict-origin-when-cross-origin
    public bool HttpsOnlyMode { get; set; } = true;
    public bool BlockThirdPartyCookies { get; set; } = true;
    public string UserAgentPreset { get; set; } = "Default"; // Default, MinimalChrome, Firefox, Safari, Custom
    public string CustomUserAgent { get; set; } = "";
    public string DnsOverHttpsProvider { get; set; } = "System"; // System, Cloudflare, Quad9, Mullvad, AdGuard, Custom
    public string CustomDohUrl { get; set; } = "";
    public bool DisableAutofill { get; set; } = true;
    public bool DisablePasswordSaving { get; set; } = true;
    public bool DisableSmartScreen { get; set; } = true;
    public bool ClearHistoryOnExit { get; set; } = false;
    public bool ClearCookiesOnExit { get; set; } = false;
    public bool ClearCacheOnExit { get; set; } = false;
    public bool ClearDownloadsOnExit { get; set; } = false;

    // --- Search & Address Bar ---
    public string DefaultSearchEngine { get; set; } = "DuckDuckGo";
    public string CustomSearchUrl { get; set; } = "https://duckduckgo.com/?q={query}";
    public bool SearchSuggestionsEnabled { get; set; } = false;
    public bool ShowFullUrl { get; set; } = false;
    public bool AutocompleteFromHistory { get; set; } = true;
    public bool AutocompleteFromBookmarks { get; set; } = true;
    public bool EnableBangShortcuts { get; set; } = true;

    // --- Tabs & New Tab ---
    public string NewTabMode { get; set; } = "Blank"; // Blank, CustomUrl
    public string CustomNewTabUrl { get; set; } = "about:blank";
    public bool OpenNewTabNextToCurrent { get; set; } = true;
    public bool SwitchToNewTab { get; set; } = true;
    public bool MiddleClickCloseTab { get; set; } = true;
    public bool DoubleClickCloseTab { get; set; } = false;
    public bool ShowTabMemoryTooltip { get; set; } = true;

    // --- Permissions ---
    public string PermissionNotifications { get; set; } = "Block"; // Allow, Ask, Block
    public string PermissionGeolocation { get; set; } = "Block";
    public string PermissionCamera { get; set; } = "Ask";
    public string PermissionMicrophone { get; set; } = "Ask";
    public string PermissionClipboard { get; set; } = "Ask";
    public bool BlockPopups { get; set; } = true;
    public bool BlockAutoplayMedia { get; set; } = false;
    public bool JavaScriptEnabled { get; set; } = true;

    // --- Extensions ---
    public bool ExtensionsEnabled { get; set; } = true;
    public bool ExtensionDeveloperMode { get; set; } = false;

    // --- Updates ---
    public bool CheckUpdatesOnStartup { get; set; } = true;
    public string UpdateCheckFrequency { get; set; } = "EveryStartup"; // EveryStartup, Daily, Weekly, Manual
    public bool AutoDownloadUpdates { get; set; } = false;
    public bool IncludePreReleases { get; set; } = false;
    public string GitHubRepository { get; set; } = "";
    public string CustomUpdateFeedUrl { get; set; } = "";
    public DateTime? LastUpdateCheckUtc { get; set; } = null;

    [JsonIgnore]
    public static readonly IReadOnlyList<SearchEngineInfo> BuiltInSearchEngines = new[]
    {
        new SearchEngineInfo("DuckDuckGo", "https://duckduckgo.com/?q={query}", "!ddg", "Privacy-focused default search"),
        new SearchEngineInfo("DuckDuckGo Lite", "https://lite.duckduckgo.com/lite/?q={query}", "!ddgl", "Ultra-lightweight HTML-only DuckDuckGo"),
        new SearchEngineInfo("Startpage", "https://www.startpage.com/do/dsearch?query={query}", "!sp", "Private Google results proxy"),
        new SearchEngineInfo("Brave Search", "https://search.brave.com/search?q={query}", "!br", "Independent privacy search"),
        new SearchEngineInfo("Qwant", "https://www.qwant.com/?q={query}", "!qw", "European privacy search"),
        new SearchEngineInfo("Kagi", "https://kagi.com/search?q={query}", "!kagi", "Ad-free subscription search"),
        new SearchEngineInfo("Ecosia", "https://www.ecosia.org/search?q={query}", "!eco", "Eco-friendly search"),
        new SearchEngineInfo("Wikipedia", "https://en.wikipedia.org/wiki/Special:Search?search={query}", "!w", "Free encyclopedia"),
        new SearchEngineInfo("GitHub", "https://github.com/search?q={query}", "!gh", "Code & repositories"),
        new SearchEngineInfo("Google", "https://www.google.com/search?q={query}", "!g", "Google Search"),
        new SearchEngineInfo("Custom", "", "!c", "Custom search URL template")
    };

    public string ResolveSearchUrl(string query)
    {
        var encoded = Uri.EscapeDataString(query);
        var trimmed = query.Trim();

        if (EnableBangShortcuts && trimmed.StartsWith('!'))
        {
            var spaceIdx = trimmed.IndexOf(' ');
            if (spaceIdx > 1)
            {
                var bang = trimmed[..spaceIdx].ToLowerInvariant();
                var rest = Uri.EscapeDataString(trimmed[(spaceIdx + 1)..].Trim());
                var match = BuiltInSearchEngines.FirstOrDefault(e => e.Bang == bang && !string.IsNullOrEmpty(e.UrlTemplate));
                if (match is not null)
                    return match.UrlTemplate.Replace("{query}", rest);
                if (bang == "!yt") return $"https://www.youtube.com/results?search_query={rest}";
                if (bang == "!mdn") return $"https://developer.mozilla.org/en-US/search?q={rest}";
                if (bang == "!r") return $"https://www.reddit.com/search/?q={rest}";
            }
        }

        if (DefaultSearchEngine == "Custom" && !string.IsNullOrWhiteSpace(CustomSearchUrl))
        {
            return CustomSearchUrl.Contains("{query}")
                ? CustomSearchUrl.Replace("{query}", encoded)
                : CustomSearchUrl + encoded;
        }

        var engine = BuiltInSearchEngines.FirstOrDefault(e => e.Name == DefaultSearchEngine)
                     ?? BuiltInSearchEngines[0];
        return engine.UrlTemplate.Replace("{query}", encoded);
    }

    public string GetNewTabUrl()
    {
        if (NewTabMode == "CustomUrl" && !string.IsNullOrWhiteSpace(CustomNewTabUrl))
            return CustomNewTabUrl.Trim();
        return "about:blank";
    }
}

public sealed record SearchEngineInfo(string Name, string UrlTemplate, string Bang, string Description);
