using System.Windows;
using System.Windows.Media;
using AetherBrowser.Models;
using AetherBrowser.Services;

namespace AetherBrowser;

public partial class App : Application
{
    public static SettingsService SettingsService { get; private set; } = null!;
    public static DataStoreService DataStore { get; private set; } = null!;
    public static AdBlockService AdBlocker { get; private set; } = null!;
    public static PrivacyEngine Privacy { get; private set; } = null!;
    public static ExtensionService Extensions { get; private set; } = null!;
    public static MemoryService Memory { get; private set; } = null!;
    public static UpdateService Updates { get; private set; } = null!;
    public static CefHostService CefHost { get; private set; } = null!;
    public static InternalPagesService InternalPages { get; private set; } = null!;
    public static FaviconService Favicons { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        SettingsService = new SettingsService();
        DataStore = new DataStoreService(SettingsService.DataDirectory);
        AdBlocker = new AdBlockService(SettingsService.DataDirectory);
        Privacy = new PrivacyEngine(AdBlocker);
        Extensions = new ExtensionService(DataStore, SettingsService.DataDirectory);
        Memory = new MemoryService();
        Updates = new UpdateService(SettingsService);
        InternalPages = new InternalPagesService();
        Favicons = new FaviconService(SettingsService.DataDirectory);

        // Initialize 100% Open-Source CEF (Chrome Runtime) once before any browser window opens
        CefHost = new CefHostService(SettingsService.DataDirectory);
        CefHost.Initialize(SettingsService.Current, Privacy, Extensions);

        // Ensure Aether Browser is registered in Windows Default Apps (HKCU, no UAC)
        DefaultBrowserService.RegisterBrowserCapabilities();

        ApplyTheme(SettingsService.Current);
        SettingsService.SettingsChanged += ApplyTheme;

        var initialUrl = e.Args.FirstOrDefault(a =>
            a.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            a.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

        var win = new MainWindow(isPrivate: false, initialUrl: initialUrl);
        MainWindow = win;
        win.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { DataStore?.Flush(); } catch { }
        try { CefHost?.Shutdown(); } catch { }
        base.OnExit(e);
    }

    public static void ApplyTheme(BrowserSettings s)
    {
        if (Current is null) return;
        var res = Current.Resources;

        var (bg, surface, surface2, border, text, muted) = InternalPagesService.GetThemePalette(s);
        var hover = s.Theme switch
        {
            "Light" => "#E5E5E9",
            "Amoled" => "#1E1E22",
            _ => "#2A2A2E"
        };

        SetBrush(res, "BgBrush", bg);
        SetBrush(res, "SurfaceBrush", surface);
        SetBrush(res, "Surface2Brush", surface2);
        SetBrush(res, "HoverBrush", hover);
        SetBrush(res, "BorderBrush", border);
        SetBrush(res, "TextBrush", text);
        SetBrush(res, "MutedBrush", muted);

        var accent = string.IsNullOrWhiteSpace(s.AccentColor) ? "#A1A1AA" : s.AccentColor;
        SetBrush(res, "AccentBrush", accent);

        try
        {
            var c = (Color)ColorConverter.ConvertFromString(accent);
            res["AccentSoftBrush"] = new SolidColorBrush(Color.FromArgb(40, c.R, c.G, c.B));
        }
        catch { }
    }

    private static void SetBrush(ResourceDictionary res, string key, string hex)
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            res[key] = new SolidColorBrush(color);
        }
        catch { }
    }
}
