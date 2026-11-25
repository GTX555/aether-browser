using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AetherBrowser.Services;

public sealed class FaviconService
{
    private const int MaxMemoryCacheEntries = 150;

    private readonly string _cacheDir;
    private readonly object _lruLock = new();
    private readonly Dictionary<string, LinkedListNode<(string Host, ImageSource Image)>> _lruMap =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<(string Host, ImageSource Image)> _lruOrder = new();
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        return client;
    }

    public FaviconService(string dataDirectory)
    {
        _cacheDir = Path.Combine(dataDirectory, "Favicons");
        Directory.CreateDirectory(_cacheDir);
    }

    public ImageSource? SaveAndDecode(string pageUrl, byte[] imageBytes)
    {
        if (imageBytes.Length == 0) return null;
        var host = ExtractHost(pageUrl);
        if (string.IsNullOrEmpty(host)) return null;

        var img = DecodeFrozen(imageBytes);
        if (img is null) return null;

        PutInMemoryCache(host, img);

        var cachePath = GetCachePath(host);
        _ = Task.Run(() =>
        {
            try { File.WriteAllBytes(cachePath, imageBytes); }
            catch { }
        });

        return img;
    }

    public ImageSource? GetCached(string url)
    {
        var host = ExtractHost(url);
        if (string.IsNullOrEmpty(host)) return null;

        if (TryGetFromMemoryCache(host, out var cached))
            return cached;

        var path = GetCachePath(host);
        if (File.Exists(path))
        {
            try
            {
                var bytes = File.ReadAllBytes(path);
                var img = DecodeFrozen(bytes);
                if (img is not null)
                {
                    PutInMemoryCache(host, img);
                    return img;
                }
            }
            catch { }
        }

        return null;
    }

    public async Task<ImageSource?> FetchDirectFaviconAsync(string url)
    {
        var host = ExtractHost(url);
        if (string.IsNullOrEmpty(host)) return null;

        var existing = GetCached(url);
        if (existing is not null) return existing;

        return await FetchByIconUrlAsync(url, $"https://{host}/favicon.ico");
    }

    public async Task<ImageSource?> FetchByIconUrlAsync(string pageUrl, string faviconUrl)
    {
        if (string.IsNullOrWhiteSpace(faviconUrl)) return null;

        var host = ExtractHost(pageUrl);
        if (string.IsNullOrEmpty(host)) return null;

        var existing = GetCached(pageUrl);
        if (existing is not null) return existing;

        // Handle inline data:image/...;base64,... favicons without network I/O
        if (faviconUrl.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            var commaIdx = faviconUrl.IndexOf(',');
            if (commaIdx > 0 && faviconUrl.AsSpan(0, commaIdx).Contains("base64".AsSpan(), StringComparison.OrdinalIgnoreCase) &&
                !faviconUrl.AsSpan(0, commaIdx).Contains("svg".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var bytes = Convert.FromBase64String(faviconUrl[(commaIdx + 1)..]);
                    if (bytes.Length >= 32 && bytes.Length <= 512_000)
                        return SaveAndDecode(pageUrl, bytes);
                }
                catch { }
            }
            return null;
        }

        if (!Uri.TryCreate(faviconUrl, UriKind.Absolute, out var iconUri) ||
            iconUri.Scheme is not ("http" or "https"))
            return null;

        // WPF BitmapImage does not decode SVG natively
        if (iconUri.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            return null;

        if (!_inFlight.TryAdd(host, 1))
            return null;

        try
        {
            using var resp = await Http.GetAsync(iconUri);
            if (!resp.IsSuccessStatusCode) return null;

            var mediaType = resp.Content.Headers.ContentType?.MediaType;
            if (mediaType is not null && mediaType.Contains("svg", StringComparison.OrdinalIgnoreCase))
                return null;

            var bytes = await resp.Content.ReadAsByteArrayAsync();
            if (bytes.Length < 32 || bytes.Length > 512_000) return null;

            return SaveAndDecode(pageUrl, bytes);
        }
        catch
        {
            return null;
        }
        finally
        {
            _inFlight.TryRemove(host, out _);
        }
    }

    private bool TryGetFromMemoryCache(string host, out ImageSource? image)
    {
        lock (_lruLock)
        {
            if (_lruMap.TryGetValue(host, out var node))
            {
                if (node != _lruOrder.First)
                {
                    _lruOrder.Remove(node);
                    _lruOrder.AddFirst(node);
                }
                image = node.Value.Image;
                return true;
            }
        }
        image = null;
        return false;
    }

    private void PutInMemoryCache(string host, ImageSource image)
    {
        lock (_lruLock)
        {
            if (_lruMap.TryGetValue(host, out var existing))
            {
                existing.Value = (host, image);
                if (existing != _lruOrder.First)
                {
                    _lruOrder.Remove(existing);
                    _lruOrder.AddFirst(existing);
                }
                return;
            }

            var node = new LinkedListNode<(string Host, ImageSource Image)>((host, image));
            _lruOrder.AddFirst(node);
            _lruMap[host] = node;

            while (_lruMap.Count > MaxMemoryCacheEntries && _lruOrder.Last is not null)
            {
                var lru = _lruOrder.Last;
                _lruOrder.RemoveLast();
                _lruMap.Remove(lru.Value.Host);
            }
        }
    }

    private string GetCachePath(string host)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(host.ToLowerInvariant())))[..16];
        return Path.Combine(_cacheDir, $"{hash}.ico");
    }

    private static string ExtractHost(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            return uri.Host;
        return "";
    }

    private static ImageSource? DecodeFrozen(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.DecodePixelWidth = 32;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }
}
