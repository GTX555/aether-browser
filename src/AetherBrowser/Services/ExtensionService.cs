using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AetherBrowser.Models;
using CefSharp;

namespace AetherBrowser.Services;

/// <summary>
/// Manages Chromium Manifest V3 / V2 extensions for Open-Source CEF Chrome Runtime:
/// installs from unpacked folders, .crx (CRX2/CRX3), .zip, or Chrome Web Store IDs/URLs,
/// supplies --load-extension startup arguments for persisted enabled extensions,
/// and supports native chrome://extensions management.
/// </summary>
public sealed class ExtensionService
{
    public const string ChromeExtensionsUrl = "chrome://extensions";

    private readonly DataStoreService _store;
    private readonly string _extensionsRoot;

    public string ExtensionsRoot => _extensionsRoot;

    /// <summary>
    /// True once an extension has been installed, toggled, or removed during the current session
    /// (CEF Chrome Runtime loads unpacked extensions via --load-extension on startup, or live via chrome://extensions).
    /// </summary>
    public bool RequiresRestartToApply { get; private set; }

    public ExtensionService(DataStoreService store, string dataDirectory)
    {
        _store = store;
        _extensionsRoot = Path.Combine(dataDirectory, "Extensions");
        Directory.CreateDirectory(_extensionsRoot);
        EnsureExtensionIdsComputed();
    }

    /// <summary>
    /// Returns all persisted, enabled extension directories that exist on disk and contain a valid manifest.json.
    /// </summary>
    public IReadOnlyList<string> GetEnabledExtensionPaths()
    {
        var paths = new List<string>();
        foreach (var rec in _store.Extensions)
        {
            if (!rec.IsEnabled || string.IsNullOrWhiteSpace(rec.InstallPath))
                continue;
            if (Directory.Exists(rec.InstallPath) && File.Exists(Path.Combine(rec.InstallPath, "manifest.json")))
                paths.Add(Path.GetFullPath(rec.InstallPath));
        }
        return paths;
    }

    /// <summary>
    /// Builds the comma-separated directory list passed to CEF's --load-extension switch at startup.
    /// </summary>
    public string GetLoadExtensionCommandLineArg()
    {
        var paths = GetEnabledExtensionPaths();
        return paths.Count == 0 ? string.Empty : string.Join(",", paths);
    }

    public Task<(bool Ok, string Message, ExtensionRecord? Record)> InstallFromUnpackedFolderAsync(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            return Task.FromResult<(bool, string, ExtensionRecord?)>((false, "Folder does not exist.", null));

        var manifestPath = Path.Combine(folderPath, "manifest.json");
        if (!File.Exists(manifestPath))
            return Task.FromResult<(bool, string, ExtensionRecord?)>((false, "No manifest.json found in the selected folder.", null));

        var id = Guid.NewGuid().ToString("N")[..16];
        var targetDir = Path.Combine(_extensionsRoot, id);
        CopyDirectory(folderPath, targetDir);

        return Task.FromResult(RegisterInstalledDirectory(id, targetDir));
    }

    public async Task<(bool Ok, string Message, ExtensionRecord? Record)> InstallFromPackageFileAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return (false, "File does not exist.", null);

        var bytes = await File.ReadAllBytesAsync(filePath);
        return InstallFromArchiveBytes(bytes, Path.GetFileNameWithoutExtension(filePath));
    }

    public async Task<(bool Ok, string Message, ExtensionRecord? Record)> InstallFromChromeWebStoreAsync(string urlOrId)
    {
        if (string.IsNullOrWhiteSpace(urlOrId))
            return (false, "Please enter a Chrome Web Store URL or 32-character Extension ID.", null);

        var match = Regex.Match(urlOrId.Trim(), @"\b([a-p]{32})\b", RegexOptions.IgnoreCase);
        if (!match.Success)
            return (false, "Could not find a valid 32-character Chrome Extension ID.", null);

        var extId = match.Groups[1].Value.ToLowerInvariant();
        var chromiumVer = (Cef.IsInitialized == true && !string.IsNullOrWhiteSpace(Cef.ChromiumVersion))
            ? Cef.ChromiumVersion
            : "136.0.0.0";

        var crxUrl =
            $"https://clients2.google.com/service/update2/crx?response=redirect&prodversion={chromiumVer}&acceptformat=crx2,crx3&x=id%3D{extId}%26uc";

        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{chromiumVer} Safari/537.36");
            var bytes = await http.GetByteArrayAsync(crxUrl);
            if (bytes.Length < 100)
                return (false, "Chrome Web Store returned an empty package. Check the Extension ID.", null);

            return InstallFromArchiveBytes(bytes, extId);
        }
        catch (Exception ex)
        {
            return (false, $"Download failed: {ex.Message}", null);
        }
    }

    private (bool Ok, string Message, ExtensionRecord? Record) InstallFromArchiveBytes(byte[] bytes, string preferredId)
    {
        var zipOffset = FindZipOffset(bytes);
        if (zipOffset < 0)
            return (false, "File is not a valid .zip or .crx (CRX2/CRX3) archive.", null);

        var id = SanitizeId(preferredId);
        if (string.IsNullOrEmpty(id)) id = Guid.NewGuid().ToString("N")[..16];
        var targetDir = Path.Combine(_extensionsRoot, id);

        if (Directory.Exists(targetDir))
        {
            try
            {
                Directory.Delete(targetDir, true);
            }
            catch
            {
                id += "_" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                targetDir = Path.Combine(_extensionsRoot, id);
            }
        }
        Directory.CreateDirectory(targetDir);

        try
        {
            using var ms = new MemoryStream(bytes, zipOffset, bytes.Length - zipOffset, writable: false);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
            foreach (var entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;
                var safeRel = entry.FullName.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
                if (safeRel.Contains("..")) continue;
                var destPath = Path.Combine(targetDir, safeRel);
                var destFolder = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(destFolder))
                    Directory.CreateDirectory(destFolder);
                entry.ExtractToFile(destPath, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            return (false, $"Failed to extract extension archive: {ex.Message}", null);
        }

        // Handle archives with a single top-level folder containing manifest.json
        var manifestDir = targetDir;
        if (!File.Exists(Path.Combine(manifestDir, "manifest.json")))
        {
            var subDirs = Directory.GetDirectories(targetDir);
            if (subDirs.Length == 1 && File.Exists(Path.Combine(subDirs[0], "manifest.json")))
                manifestDir = subDirs[0];
            else
                return (false, "No manifest.json found inside the archive.", null);
        }

        return RegisterInstalledDirectory(id, manifestDir);
    }

    private (bool Ok, string Message, ExtensionRecord? Record) RegisterInstalledDirectory(string id, string manifestDir)
    {
        var fullDir = Path.GetFullPath(manifestDir);
        var manifestPath = Path.Combine(fullDir, "manifest.json");
        var rec = ParseManifest(id, fullDir, manifestPath);

        // Compute the exact Chromium extension ID assigned when loaded via --load-extension
        rec.WebViewExtensionId = ComputeChromiumExtensionId(fullDir, manifestPath);

        _store.Extensions.RemoveAll(e =>
            e.Id == rec.Id ||
            (!string.IsNullOrEmpty(rec.WebViewExtensionId) && e.WebViewExtensionId == rec.WebViewExtensionId));
        _store.Extensions.Insert(0, rec);
        _store.SaveExtensions();
        RequiresRestartToApply = true;

        return (true, $"Installed '{rec.Name}' v{rec.Version}. Restart Aether Browser (or load in chrome://extensions) to activate.", rec);
    }

    public Task<bool> SetEnabledAsync(string id, bool enabled)
    {
        var rec = _store.Extensions.FirstOrDefault(e => e.Id == id);
        if (rec is null) return Task.FromResult(false);

        rec.IsEnabled = enabled;
        _store.SaveExtensions();
        RequiresRestartToApply = true;
        return Task.FromResult(true);
    }

    public Task<bool> RemoveAsync(string id)
    {
        var rec = _store.Extensions.FirstOrDefault(e => e.Id == id);
        if (rec is null) return Task.FromResult(false);

        _store.Extensions.Remove(rec);
        _store.SaveExtensions();
        RequiresRestartToApply = true;

        try
        {
            if (Directory.Exists(rec.InstallPath) &&
                rec.InstallPath.StartsWith(_extensionsRoot, StringComparison.OrdinalIgnoreCase))
            {
                Directory.Delete(rec.InstallPath, true);
            }
        }
        catch { }

        return Task.FromResult(true);
    }

    public string? GetExtensionPageUrl(ExtensionRecord rec, bool preferOptions = false)
    {
        if (string.IsNullOrEmpty(rec.WebViewExtensionId))
            return null;

        var page = preferOptions
            ? (rec.OptionsPage ?? rec.PopupPage)
            : (rec.PopupPage ?? rec.OptionsPage);

        if (string.IsNullOrWhiteSpace(page))
            return null;

        return $"chrome-extension://{rec.WebViewExtensionId}/{page.TrimStart('/')}";
    }

    private void EnsureExtensionIdsComputed()
    {
        bool changed = false;
        foreach (var rec in _store.Extensions)
        {
            if (string.IsNullOrWhiteSpace(rec.InstallPath) || !Directory.Exists(rec.InstallPath))
                continue;

            var manifestPath = Path.Combine(rec.InstallPath, "manifest.json");
            if (!File.Exists(manifestPath))
                continue;

            if (string.IsNullOrWhiteSpace(rec.WebViewExtensionId))
            {
                rec.WebViewExtensionId = ComputeChromiumExtensionId(Path.GetFullPath(rec.InstallPath), manifestPath);
                changed = true;
            }
        }

        if (changed)
            _store.SaveExtensions();
    }

    /// <summary>
    /// Computes Chromium's deterministic 32-char [a-p] extension ID:
    /// uses base64 "key" from manifest.json if present, otherwise SHA-256 of the UTF-16LE normalized Windows path.
    /// </summary>
    private static string ComputeChromiumExtensionId(string fullDir, string manifestPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (doc.RootElement.TryGetProperty("key", out var keyEl))
            {
                var keyB64 = keyEl.GetString();
                if (!string.IsNullOrWhiteSpace(keyB64))
                {
                    var keyBytes = Convert.FromBase64String(keyB64);
                    return HashToExtensionId(SHA256.HashData(keyBytes));
                }
            }
        }
        catch { }

        // Chromium Windows unpacked path hash: drive letter uppercase, no trailing slash, UTF-16LE bytes
        var normalized = fullDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (normalized.Length >= 2 && normalized[1] == ':')
            normalized = char.ToUpperInvariant(normalized[0]) + normalized[1..];

        var pathBytes = Encoding.Unicode.GetBytes(normalized);
        return HashToExtensionId(SHA256.HashData(pathBytes));
    }

    private static string HashToExtensionId(byte[] sha256)
    {
        Span<char> chars = stackalloc char[32];
        for (int i = 0; i < 16; i++)
        {
            byte b = sha256[i];
            chars[i * 2] = (char)('a' + (b >> 4));
            chars[i * 2 + 1] = (char)('a' + (b & 0x0F));
        }
        return new string(chars);
    }

    private static int FindZipOffset(byte[] bytes)
    {
        if (bytes.Length < 16) return -1;

        // Plain ZIP ("PK\x03\x04")
        if (bytes[0] == 0x50 && bytes[1] == 0x4B && bytes[2] == 0x03 && bytes[3] == 0x04)
            return 0;

        // CRX ("Cr24")
        if (bytes[0] == 0x43 && bytes[1] == 0x72 && bytes[2] == 0x32 && bytes[3] == 0x34)
        {
            var version = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4));
            if (version == 3 && bytes.Length >= 12)
            {
                var headerSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4));
                var offset = 12 + headerSize;
                if (offset < bytes.Length - 4 && bytes[offset] == 0x50 && bytes[offset + 1] == 0x4B)
                    return offset;
            }
            else if (version == 2 && bytes.Length >= 16)
            {
                var pubKeyLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4));
                var sigLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12, 4));
                var offset = 16 + pubKeyLen + sigLen;
                if (offset < bytes.Length - 4 && bytes[offset] == 0x50 && bytes[offset + 1] == 0x4B)
                    return offset;
            }
        }

        // Fallback: scan for PK\x03\x04 within first 64KB
        var maxScan = Math.Min(bytes.Length - 4, 65536);
        for (int i = 0; i < maxScan; i++)
        {
            if (bytes[i] == 0x50 && bytes[i + 1] == 0x4B && bytes[i + 2] == 0x03 && bytes[i + 3] == 0x04)
                return i;
        }
        return -1;
    }

    private static ExtensionRecord ParseManifest(string id, string dir, string manifestPath)
    {
        var rec = new ExtensionRecord
        {
            Id = id,
            Name = id,
            Version = "1.0.0",
            InstallPath = dir,
            IsEnabled = true,
            InstalledUtc = DateTime.UtcNow
        };

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = doc.RootElement;

            var rawName = root.TryGetProperty("name", out var n) ? n.GetString() ?? id : id;
            var rawDesc = root.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";

            rec.Name = ResolveLocaleString(dir, rawName);
            rec.Description = ResolveLocaleString(dir, rawDesc);

            if (root.TryGetProperty("version", out var v))
                rec.Version = v.GetString() ?? "1.0.0";

            if (root.TryGetProperty("action", out var action) && action.TryGetProperty("default_popup", out var pop))
                rec.PopupPage = pop.GetString();
            else if (root.TryGetProperty("browser_action", out var bAction) && bAction.TryGetProperty("default_popup", out var bPop))
                rec.PopupPage = bPop.GetString();

            if (root.TryGetProperty("options_page", out var opt))
                rec.OptionsPage = opt.GetString();
            else if (root.TryGetProperty("options_ui", out var optUi) && optUi.TryGetProperty("page", out var optPage))
                rec.OptionsPage = optPage.GetString();

            if (root.TryGetProperty("permissions", out var perms) && perms.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in perms.EnumerateArray())
                {
                    var s = p.GetString();
                    if (!string.IsNullOrEmpty(s)) rec.Permissions.Add(s);
                }
            }
        }
        catch { }

        return rec;
    }

    private static string ResolveLocaleString(string extDir, string value)
    {
        if (!value.StartsWith("__MSG_") || !value.EndsWith("__"))
            return value;

        var key = value[6..^2];
        var candidates = new[]
        {
            Path.Combine(extDir, "_locales", "en", "messages.json"),
            Path.Combine(extDir, "_locales", "en_US", "messages.json")
        };

        foreach (var file in candidates)
        {
            if (!File.Exists(file)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Name.Equals(key, StringComparison.OrdinalIgnoreCase) &&
                        prop.Value.TryGetProperty("message", out var msg))
                    {
                        return msg.GetString() ?? value;
                    }
                }
            }
            catch { }
        }
        return value;
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), true);
        foreach (var dir in Directory.GetDirectories(sourceDir))
            CopyDirectory(dir, Path.Combine(destDir, Path.GetFileName(dir)));
    }

    private static string SanitizeId(string input) =>
        Regex.Replace(input ?? "", @"[^a-zA-Z0-9_\-]", "");
}
