using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace AetherBrowser.Models;

public sealed class BookmarkItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string Folder { get; set; } = "Bookmarks Bar";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class HistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public DateTime VisitedUtc { get; set; } = DateTime.UtcNow;
    public int VisitCount { get; set; } = 1;
}

public sealed class DownloadRecord : INotifyPropertyChanged
{
    private long _totalBytes;
    private long _receivedBytes;
    private string _state = "InProgress";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string FileName { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string Url { get; set; } = "";
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;

    public long TotalBytes
    {
        get => _totalBytes;
        set { if (_totalBytes != value) { _totalBytes = value; Notify(); Notify(nameof(ProgressPercent)); Notify(nameof(StatusLine)); } }
    }

    public long ReceivedBytes
    {
        get => _receivedBytes;
        set { if (_receivedBytes != value) { _receivedBytes = value; Notify(); Notify(nameof(ProgressPercent)); Notify(nameof(StatusLine)); } }
    }

    public string State
    {
        get => _state;
        set { if (_state != value) { _state = value; Notify(); Notify(nameof(IsInProgress)); Notify(nameof(IsCompleted)); Notify(nameof(StatusLine)); } }
    }

    [JsonIgnore]
    public bool IsInProgress => string.Equals(_state, "InProgress", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsCompleted => string.Equals(_state, "Completed", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public double ProgressPercent => _totalBytes > 0 ? Math.Clamp(_receivedBytes * 100.0 / _totalBytes, 0, 100) : (IsCompleted ? 100 : 0);

    [JsonIgnore]
    public string StatusLine
    {
        get
        {
            if (IsInProgress)
                return _totalBytes > 0
                    ? $"Downloading · {FormatBytes(_receivedBytes)} / {FormatBytes(_totalBytes)}"
                    : $"Downloading · {FormatBytes(_receivedBytes)}";
            if (IsCompleted)
                return _totalBytes > 0 ? $"Completed · {FormatBytes(_totalBytes)}" : "Completed";
            return _state;
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):0.#} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):0.##} GB";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class ExtensionRecord
{
    public string Id { get; set; } = "";
    public string WebViewExtensionId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "1.0.0";
    public string Description { get; set; } = "";
    public string InstallPath { get; set; } = "";
    public bool IsEnabled { get; set; } = true;
    public string? PopupPage { get; set; }
    public string? OptionsPage { get; set; }
    public List<string> Permissions { get; set; } = new();
    public DateTime InstalledUtc { get; set; } = DateTime.UtcNow;
}

public sealed class SavedSessionTab
{
    public string Url { get; set; } = "about:blank";
    public string Title { get; set; } = "New Tab";
    public bool IsPinned { get; set; }
    public bool IsActive { get; set; }
}

public sealed class MemorySnapshot
{
    public double AppWorkingSetMb { get; set; }
    public double WebViewProcessesMb { get; set; }
    public double TotalMb => Math.Round(AppWorkingSetMb + WebViewProcessesMb, 1);
    public int TotalTabs { get; set; }
    public int ActiveTabs { get; set; }
    public int SleepingTabs { get; set; }
    public int DiscardedTabs { get; set; }
    public int WebViewProcessCount { get; set; }
}

public sealed class UpdateInfo
{
    public bool IsUpdateAvailable { get; set; }
    public string CurrentVersion { get; set; } = "1.0.0";
    public string LatestVersion { get; set; } = "1.0.0";
    public string ReleaseNotes { get; set; } = "";
    public string? DownloadUrl { get; set; }
    public string? ReleasePageUrl { get; set; }
    public string StatusMessage { get; set; } = "Up to date";
    public bool IsVelopackUpdate { get; set; }
    public DateTime CheckedUtc { get; set; } = DateTime.UtcNow;
}
