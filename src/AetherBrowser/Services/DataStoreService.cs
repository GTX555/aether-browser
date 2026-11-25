using System.IO;
using System.Text.Json;
using System.Threading;
using AetherBrowser.Models;

namespace AetherBrowser.Services;

public sealed class DataStoreService : IDisposable
{
    private const int DebounceMs = 500;
    private const int MaxCoalesceDelayMs = 2000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _dataDir;
    private readonly string _bookmarksPath;
    private readonly string _historyPath;
    private readonly string _downloadsPath;
    private readonly string _sessionPath;
    private readonly string _extensionsPath;

    private readonly object _dataLock = new();
    private readonly object _ioLock = new();
    private readonly Timer _debounceTimer;

    private bool _dirtyBookmarks;
    private bool _dirtyHistory;
    private bool _dirtyDownloads;
    private bool _dirtyExtensions;
    private long _firstDirtyTick;
    private bool _disposed;

    public List<BookmarkItem> Bookmarks { get; private set; }
    public List<HistoryEntry> History { get; private set; }
    public List<DownloadRecord> Downloads { get; private set; }
    public List<ExtensionRecord> Extensions { get; private set; }

    public event Action? BookmarksChanged;
    public event Action? HistoryChanged;
    public event Action? DownloadsChanged;
    public event Action? ExtensionsChanged;

    public DataStoreService(string dataDirectory)
    {
        _dataDir = dataDirectory;
        Directory.CreateDirectory(_dataDir);
        _bookmarksPath = Path.Combine(_dataDir, "bookmarks.json");
        _historyPath = Path.Combine(_dataDir, "history.json");
        _downloadsPath = Path.Combine(_dataDir, "downloads.json");
        _sessionPath = Path.Combine(_dataDir, "session.json");
        _extensionsPath = Path.Combine(_dataDir, "extensions.json");

        Bookmarks = LoadList<BookmarkItem>(_bookmarksPath);
        History = LoadList<HistoryEntry>(_historyPath);
        Downloads = LoadList<DownloadRecord>(_downloadsPath);
        Extensions = LoadList<ExtensionRecord>(_extensionsPath);

        _debounceTimer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    public bool IsBookmarked(string url)
    {
        lock (_dataLock)
        {
            return Bookmarks.Any(b => string.Equals(b.Url, url, StringComparison.OrdinalIgnoreCase));
        }
    }

    public void ToggleBookmark(string url, string title)
    {
        if (string.IsNullOrWhiteSpace(url) || url == "about:blank" || url.StartsWith("aether://", StringComparison.OrdinalIgnoreCase))
            return;

        lock (_dataLock)
        {
            var existing = Bookmarks.FirstOrDefault(b => string.Equals(b.Url, url, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                Bookmarks.Remove(existing);
            }
            else
            {
                Bookmarks.Insert(0, new BookmarkItem
                {
                    Url = url,
                    Title = string.IsNullOrWhiteSpace(title) ? url : title
                });
            }
            _dirtyBookmarks = true;
            ScheduleDebouncedFlushLocked();
        }
        BookmarksChanged?.Invoke();
    }

    public void RemoveBookmark(string id)
    {
        lock (_dataLock)
        {
            Bookmarks.RemoveAll(b => b.Id == id);
            _dirtyBookmarks = true;
            ScheduleDebouncedFlushLocked();
        }
        BookmarksChanged?.Invoke();
    }

    public void RecordHistory(string url, string title)
    {
        if (string.IsNullOrWhiteSpace(url) || url == "about:blank" || url.StartsWith("aether://", StringComparison.OrdinalIgnoreCase))
            return;

        lock (_dataLock)
        {
            var existing = History.FirstOrDefault(h => string.Equals(h.Url, url, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                existing.Title = string.IsNullOrWhiteSpace(title) ? existing.Title : title;
                existing.VisitedUtc = DateTime.UtcNow;
                existing.VisitCount++;
                History.Remove(existing);
                History.Insert(0, existing);
            }
            else
            {
                History.Insert(0, new HistoryEntry
                {
                    Url = url,
                    Title = string.IsNullOrWhiteSpace(title) ? url : title,
                    VisitedUtc = DateTime.UtcNow
                });
            }

            if (History.Count > 2500)
                History.RemoveRange(2500, History.Count - 2500);

            _dirtyHistory = true;
            ScheduleDebouncedFlushLocked();
        }
        HistoryChanged?.Invoke();
    }

    public void RemoveHistoryEntry(string id)
    {
        lock (_dataLock)
        {
            History.RemoveAll(h => h.Id == id);
            _dirtyHistory = true;
            ScheduleDebouncedFlushLocked();
        }
        HistoryChanged?.Invoke();
    }

    public void ClearHistory()
    {
        lock (_dataLock)
        {
            History.Clear();
            _dirtyHistory = true;
            ScheduleDebouncedFlushLocked();
        }
        HistoryChanged?.Invoke();
    }

    public void UpsertDownload(DownloadRecord record)
    {
        lock (_dataLock)
        {
            var idx = Downloads.FindIndex(d => d.Id == record.Id);
            if (idx >= 0) Downloads[idx] = record;
            else Downloads.Insert(0, record);

            if (Downloads.Count > 500)
                Downloads.RemoveRange(500, Downloads.Count - 500);

            _dirtyDownloads = true;
            ScheduleDebouncedFlushLocked();
        }
        DownloadsChanged?.Invoke();
    }

    public void ClearDownloads()
    {
        lock (_dataLock)
        {
            Downloads.Clear();
            _dirtyDownloads = true;
            ScheduleDebouncedFlushLocked();
        }
        DownloadsChanged?.Invoke();
    }

    public void SaveExtensions()
    {
        lock (_dataLock)
        {
            _dirtyExtensions = true;
            ScheduleDebouncedFlushLocked();
        }
        ExtensionsChanged?.Invoke();
    }

    public void SaveSession(IEnumerable<SavedSessionTab> tabs)
    {
        var snapshot = tabs.ToList();
        lock (_ioLock)
        {
            SaveList(_sessionPath, snapshot);
        }
    }

    public List<SavedSessionTab> LoadSession() => LoadList<SavedSessionTab>(_sessionPath);

    /// <summary>
    /// Immediately flushes any pending debounced JSON writes to disk. Call on application shutdown.
    /// </summary>
    public void Flush()
    {
        List<BookmarkItem>? bookmarksSnap = null;
        List<HistoryEntry>? historySnap = null;
        List<DownloadRecord>? downloadsSnap = null;
        List<ExtensionRecord>? extensionsSnap = null;

        lock (_dataLock)
        {
            if (!_disposed)
            {
                try { _debounceTimer.Change(Timeout.Infinite, Timeout.Infinite); }
                catch (ObjectDisposedException) { }
            }

            _firstDirtyTick = 0;

            if (_dirtyBookmarks)
            {
                bookmarksSnap = Bookmarks.ToList();
                _dirtyBookmarks = false;
            }
            if (_dirtyHistory)
            {
                historySnap = History.ToList();
                _dirtyHistory = false;
            }
            if (_dirtyDownloads)
            {
                downloadsSnap = Downloads.ToList();
                _dirtyDownloads = false;
            }
            if (_dirtyExtensions)
            {
                extensionsSnap = Extensions.ToList();
                _dirtyExtensions = false;
            }
        }

        if (bookmarksSnap is null && historySnap is null && downloadsSnap is null && extensionsSnap is null)
            return;

        lock (_ioLock)
        {
            if (bookmarksSnap is not null) SaveList(_bookmarksPath, bookmarksSnap);
            if (historySnap is not null) SaveList(_historyPath, historySnap);
            if (downloadsSnap is not null) SaveList(_downloadsPath, downloadsSnap);
            if (extensionsSnap is not null) SaveList(_extensionsPath, extensionsSnap);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        Flush();
        _disposed = true;
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        _debounceTimer.Dispose();
    }

    private void OnProcessExit(object? sender, EventArgs e) => Flush();

    private void ScheduleDebouncedFlushLocked()
    {
        if (_disposed) return;

        var now = Environment.TickCount64;
        if (_firstDirtyTick == 0)
        {
            _firstDirtyTick = now;
        }
        else if (now - _firstDirtyTick >= MaxCoalesceDelayMs)
        {
            // Bounded staleness during continuous bursts: let the already-armed timer fire soon
            return;
        }

        try
        {
            _debounceTimer.Change(DebounceMs, Timeout.Infinite);
        }
        catch (ObjectDisposedException) { }
    }

    private static List<T> LoadList<T>(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<List<T>>(json, JsonOptions) ?? new();
            }
        }
        catch { }
        return new();
    }

    private static void SaveList<T>(string path, List<T> items)
    {
        try
        {
            var json = JsonSerializer.Serialize(items, JsonOptions);
            var tmpPath = path + ".tmp";
            File.WriteAllText(tmpPath, json);
            File.Move(tmpPath, path, overwrite: true);
        }
        catch
        {
            try
            {
                File.WriteAllText(path, JsonSerializer.Serialize(items, JsonOptions));
            }
            catch { }
        }
    }
}
