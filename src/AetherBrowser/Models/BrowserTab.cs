using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using CefSharp.Wpf.HwndHost;

namespace AetherBrowser.Models;

public enum TabMemoryState
{
    Active,
    Sleeping,   // CEF IBrowserHost.WasHidden(true) + low-memory renderer trim
    Discarded   // ChromiumWebBrowser disposed & unloaded -> 0 MB renderer RAM
}

public sealed class BrowserTab : INotifyPropertyChanged
{
    private string _title = "New Tab";
    private string _url = "about:blank";
    private bool _isSelected;
    private bool _isLoading;
    private double _loadingProgress;
    private bool _isPinned;
    private bool _isMuted;
    private bool _isAudible;
    private bool _canGoBack;
    private bool _canGoForward;
    private bool _isSecure;
    private int _blockedCount;
    private TabMemoryState _memoryState = TabMemoryState.Active;
    private ImageSource? _favicon;
    private double _zoomFactor = 1.0;

    public string Id { get; } = Guid.NewGuid().ToString("N");
    public ChromiumWebBrowser? Browser { get; set; }
    public bool IsBrowserInitialized { get; set; }
    public bool IsInternalPage => Url.StartsWith("aether://", StringComparison.OrdinalIgnoreCase);
    public bool IsBlankTab => string.Equals(Url, "about:blank", StringComparison.OrdinalIgnoreCase);
    public DateTime LastActiveUtc { get; set; } = DateTime.UtcNow;
    public List<string> BlockedDomains { get; } = new();
    public string? InternalSection { get; set; }

    public string Title
    {
        get => _title;
        set
        {
            if (_title != value)
            {
                _title = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayTitle));
            }
        }
    }

    public string DisplayTitle => string.IsNullOrWhiteSpace(_title) ? "New Tab" : _title;

    public string Url
    {
        get => _url;
        set
        {
            if (_url != value)
            {
                _url = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsInternalPage));
                OnPropertyChanged(nameof(IsBlankTab));
            }
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                if (value) LastActiveUtc = DateTime.UtcNow;
                OnPropertyChanged();
            }
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        set { if (_isLoading != value) { _isLoading = value; OnPropertyChanged(); } }
    }

    /// <summary>
    /// Real page loading progress reported by CEF DisplayHandler (0.0 to 1.0).
    /// </summary>
    public double LoadingProgress
    {
        get => _loadingProgress;
        set
        {
            var clamped = Math.Clamp(value, 0.0, 1.0);
            if (Math.Abs(_loadingProgress - clamped) > 0.001)
            {
                _loadingProgress = clamped;
                OnPropertyChanged();
            }
        }
    }

    public bool IsPinned
    {
        get => _isPinned;
        set { if (_isPinned != value) { _isPinned = value; OnPropertyChanged(); } }
    }

    public bool IsMuted
    {
        get => _isMuted;
        set { if (_isMuted != value) { _isMuted = value; OnPropertyChanged(); } }
    }

    public bool IsAudible
    {
        get => _isAudible;
        set { if (_isAudible != value) { _isAudible = value; OnPropertyChanged(); } }
    }

    public bool CanGoBack
    {
        get => _canGoBack;
        set { if (_canGoBack != value) { _canGoBack = value; OnPropertyChanged(); } }
    }

    public bool CanGoForward
    {
        get => _canGoForward;
        set { if (_canGoForward != value) { _canGoForward = value; OnPropertyChanged(); } }
    }

    public bool IsSecure
    {
        get => _isSecure;
        set { if (_isSecure != value) { _isSecure = value; OnPropertyChanged(); } }
    }

    public int BlockedCount
    {
        get => _blockedCount;
        set { if (_blockedCount != value) { _blockedCount = value; OnPropertyChanged(); } }
    }

    public TabMemoryState MemoryState
    {
        get => _memoryState;
        set
        {
            if (_memoryState != value)
            {
                _memoryState = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsSleepingOrDiscarded));
                OnPropertyChanged(nameof(MemoryStateLabel));
            }
        }
    }

    public bool IsSleepingOrDiscarded => _memoryState != TabMemoryState.Active;

    public string MemoryStateLabel => _memoryState switch
    {
        TabMemoryState.Sleeping => "Sleeping (Low RAM)",
        TabMemoryState.Discarded => "Hibernated (0 MB)",
        _ => "Active"
    };

    public ImageSource? Favicon
    {
        get => _favicon;
        set { if (_favicon != value) { _favicon = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasFavicon)); } }
    }

    public bool HasFavicon => _favicon is not null;

    public double ZoomFactor
    {
        get => _zoomFactor;
        set { if (Math.Abs(_zoomFactor - value) > 0.001) { _zoomFactor = value; OnPropertyChanged(); } }
    }

    public void RecordBlocked(string host)
    {
        BlockedCount++;
        if (BlockedDomains.Count < 100 && !BlockedDomains.Contains(host, StringComparer.OrdinalIgnoreCase))
            BlockedDomains.Add(host);
    }

    public void ResetBlocked()
    {
        BlockedCount = 0;
        BlockedDomains.Clear();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
