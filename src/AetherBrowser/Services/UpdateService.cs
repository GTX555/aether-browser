using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using BrowserUpdateInfo = AetherBrowser.Models.UpdateInfo;

namespace AetherBrowser.Services;

public sealed class UpdateService
{
    private readonly SettingsService _settings;

    public BrowserUpdateInfo LastInfo { get; private set; }
    public event Action<BrowserUpdateInfo>? UpdateStatusChanged;
    public event Action<int>? DownloadProgressChanged;

    public static string CurrentVersion
    {
        get
        {
            var ver = Assembly.GetExecutingAssembly().GetName().Version;
            return ver is null ? "2.2.0" : $"{ver.Major}.{ver.Minor}.{ver.Build}";
        }
    }

    public UpdateService(SettingsService settings)
    {
        _settings = settings;
        LastInfo = new BrowserUpdateInfo
        {
            CurrentVersion = CurrentVersion,
            LatestVersion = CurrentVersion,
            StatusMessage = "Ready to scan Git for updates"
        };
    }

    public bool ShouldCheckOnStartup()
    {
        var s = _settings.Current;
        if (!s.CheckUpdatesOnStartup || s.UpdateCheckFrequency == "Manual")
            return false;

        if (s.UpdateCheckFrequency == "EveryStartup" || s.LastUpdateCheckUtc is null)
            return true;

        var elapsed = DateTime.UtcNow - s.LastUpdateCheckUtc.Value;
        return s.UpdateCheckFrequency switch
        {
            "Daily" => elapsed.TotalHours >= 24,
            "Weekly" => elapsed.TotalDays >= 7,
            _ => true
        };
    }

    public static string NormalizeGitHubRepo(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var s = raw.Trim();
        if (s.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            s = s[..^4];

        var sshMatch = Regex.Match(s, @"github\.com[:/]([^/\s]+/[^/\s]+)", RegexOptions.IgnoreCase);
        if (sshMatch.Success)
            return sshMatch.Groups[1].Value.Trim('/');

        return s.Trim('/');
    }

    public async Task<BrowserUpdateInfo> CheckForUpdatesAsync(bool manual = false)
    {
        var s = _settings.Current;
        var repo = NormalizeGitHubRepo(s.GitHubRepository ?? "");
        var customUrl = (s.CustomUpdateFeedUrl ?? "").Trim();

        if (string.IsNullOrEmpty(repo) && string.IsNullOrEmpty(customUrl))
        {
            LastInfo = new BrowserUpdateInfo
            {
                CurrentVersion = CurrentVersion,
                LatestVersion = CurrentVersion,
                IsUpdateAvailable = false,
                StatusMessage = "No Git repository configured. Enter owner/repo in Settings → Updates.",
                CheckedUtc = DateTime.UtcNow
            };
            UpdateStatusChanged?.Invoke(LastInfo);
            return LastInfo;
        }

        // 1. Custom raw JSON feed (e.g. raw releases.json in any Git repo)
        if (!string.IsNullOrEmpty(customUrl) && customUrl.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            var jsonResult = await CheckCustomJsonFeedAsync(customUrl);
            if (jsonResult is not null)
            {
                _settings.Update(cfg => cfg.LastUpdateCheckUtc = DateTime.UtcNow);
                LastInfo = jsonResult;
                UpdateStatusChanged?.Invoke(LastInfo);
                if (LastInfo.IsUpdateAvailable && s.AutoDownloadUpdates && !manual)
                    _ = DownloadAndApplyUpdateAsync(autoRestart: false);
                return LastInfo;
            }
        }

        // 2. GitHub Releases API (+ Git Tags fallback)
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"AetherBrowser/{CurrentVersion}");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            if (!string.IsNullOrEmpty(repo))
            {
                var apiUrl = s.IncludePreReleases
                    ? $"https://api.github.com/repos/{repo}/releases?per_page=1"
                    : $"https://api.github.com/repos/{repo}/releases/latest";

                var response = await http.GetAsync(apiUrl);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);
                    var release = doc.RootElement.ValueKind == JsonValueKind.Array
                        ? (doc.RootElement.GetArrayLength() > 0 ? doc.RootElement[0] : default)
                        : doc.RootElement;

                    if (release.ValueKind == JsonValueKind.Object)
                    {
                        var tag = (release.TryGetProperty("tag_name", out var t) ? t.GetString() : "") ?? "";
                        var cleanVer = tag.TrimStart('v', 'V');
                        var notes = (release.TryGetProperty("body", out var b) ? b.GetString() : "") ?? "";
                        var htmlUrl = release.TryGetProperty("html_url", out var h) ? h.GetString() : null;
                        string? setupAssetUrl = null;

                        if (release.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var asset in assets.EnumerateArray())
                            {
                                var name = asset.TryGetProperty("name", out var an) ? an.GetString() ?? "" : "";
                                var dl = asset.TryGetProperty("browser_download_url", out var du) ? du.GetString() : null;
                                if (name.EndsWith("Setup.exe", StringComparison.OrdinalIgnoreCase) ||
                                    name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                                {
                                    setupAssetUrl = dl;
                                    if (name.Contains("Setup", StringComparison.OrdinalIgnoreCase))
                                        break;
                                }
                            }
                        }

                        _settings.Update(cfg => cfg.LastUpdateCheckUtc = DateTime.UtcNow);
                        var isNewer = CompareVersions(cleanVer, CurrentVersion) > 0;
                        LastInfo = new BrowserUpdateInfo
                        {
                            IsUpdateAvailable = isNewer,
                            CurrentVersion = CurrentVersion,
                            LatestVersion = string.IsNullOrEmpty(cleanVer) ? CurrentVersion : cleanVer,
                            ReleaseNotes = notes,
                            DownloadUrl = setupAssetUrl,
                            ReleasePageUrl = htmlUrl,
                            StatusMessage = isNewer
                                ? $"Aether Browser v{cleanVer} is available on Git!"
                                : $"Aether Browser v{CurrentVersion} is up to date.",
                            CheckedUtc = DateTime.UtcNow
                        };
                        UpdateStatusChanged?.Invoke(LastInfo);
                        if (isNewer && s.AutoDownloadUpdates && !manual)
                            _ = DownloadAndApplyUpdateAsync(autoRestart: false);
                        return LastInfo;
                    }
                }

                // Fallback: check Git tags directly if no GitHub Release exists yet
                var tagsResp = await http.GetAsync($"https://api.github.com/repos/{repo}/tags?per_page=10");
                if (tagsResp.IsSuccessStatusCode)
                {
                    var tagsJson = await tagsResp.Content.ReadAsStringAsync();
                    using var tagsDoc = JsonDocument.Parse(tagsJson);
                    string? bestVer = null;
                    string? bestTag = null;
                    if (tagsDoc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in tagsDoc.RootElement.EnumerateArray())
                        {
                            var name = item.TryGetProperty("name", out var tn) ? tn.GetString() ?? "" : "";
                            var ver = name.TrimStart('v', 'V');
                            if (Regex.IsMatch(ver, @"^\d+\.\d+\.\d+") && (bestVer is null || CompareVersions(ver, bestVer) > 0))
                            {
                                bestVer = ver;
                                bestTag = name;
                            }
                        }
                    }

                    _settings.Update(cfg => cfg.LastUpdateCheckUtc = DateTime.UtcNow);
                    if (bestVer is not null)
                    {
                        var isNewer = CompareVersions(bestVer, CurrentVersion) > 0;
                        LastInfo = new BrowserUpdateInfo
                        {
                            IsUpdateAvailable = isNewer,
                            CurrentVersion = CurrentVersion,
                            LatestVersion = bestVer,
                            ReleasePageUrl = $"https://github.com/{repo}/releases/tag/{bestTag}",
                            StatusMessage = isNewer
                                ? $"New Git tag v{bestVer} found in github.com/{repo}!"
                                : $"Aether Browser v{CurrentVersion} matches latest Git tag.",
                            CheckedUtc = DateTime.UtcNow
                        };
                        UpdateStatusChanged?.Invoke(LastInfo);
                        return LastInfo;
                    }
                }

                LastInfo = new BrowserUpdateInfo
                {
                    CurrentVersion = CurrentVersion,
                    LatestVersion = CurrentVersion,
                    StatusMessage = $"No releases found yet at github.com/{repo} (HTTP {(int)response.StatusCode}).",
                    CheckedUtc = DateTime.UtcNow
                };
                UpdateStatusChanged?.Invoke(LastInfo);
                return LastInfo;
            }
        }
        catch (Exception ex)
        {
            LastInfo = new BrowserUpdateInfo
            {
                CurrentVersion = CurrentVersion,
                LatestVersion = CurrentVersion,
                StatusMessage = $"Could not reach Git update server: {ex.Message}",
                CheckedUtc = DateTime.UtcNow
            };
            UpdateStatusChanged?.Invoke(LastInfo);
            return LastInfo;
        }

        return LastInfo;
    }

    private async Task<BrowserUpdateInfo?> CheckCustomJsonFeedAsync(string jsonUrl)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"AetherBrowser/{CurrentVersion}");
            var text = await http.GetStringAsync(jsonUrl);
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var latest = (root.TryGetProperty("version", out var v) ? v.GetString() : "")?.TrimStart('v', 'V') ?? "";
            if (string.IsNullOrEmpty(latest)) return null;

            var notes = root.TryGetProperty("notes", out var n) ? n.GetString() ?? "" : "";
            var dlUrl = root.TryGetProperty("downloadUrl", out var d) ? d.GetString() : null;
            var pageUrl = root.TryGetProperty("releaseUrl", out var p) ? p.GetString() : null;
            var isNewer = CompareVersions(latest, CurrentVersion) > 0;

            return new BrowserUpdateInfo
            {
                IsUpdateAvailable = isNewer,
                CurrentVersion = CurrentVersion,
                LatestVersion = latest,
                ReleaseNotes = notes,
                DownloadUrl = dlUrl,
                ReleasePageUrl = pageUrl,
                StatusMessage = isNewer
                    ? $"Aether Browser v{latest} is available!"
                    : $"Aether Browser v{CurrentVersion} is up to date.",
                CheckedUtc = DateTime.UtcNow
            };
        }
        catch
        {
            return null;
        }
    }

    public async Task<(bool Ok, string Message)> DownloadAndApplyUpdateAsync(bool autoRestart = true)
    {
        if (!string.IsNullOrEmpty(LastInfo.DownloadUrl))
        {
            try
            {
                var tempPath = Path.Combine(Path.GetTempPath(), $"AetherBrowser-{LastInfo.LatestVersion}-Setup.exe");
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd($"AetherBrowser/{CurrentVersion}");
                using var resp = await http.GetAsync(LastInfo.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
                resp.EnsureSuccessStatusCode();

                var total = resp.Content.Headers.ContentLength ?? -1L;
                await using var input = await resp.Content.ReadAsStreamAsync();
                await using (var output = File.Create(tempPath))
                {
                    var buffer = new byte[65536];
                    long read = 0;
                    int n;
                    while ((n = await input.ReadAsync(buffer)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, n));
                        read += n;
                        if (total > 0)
                            DownloadProgressChanged?.Invoke((int)(read * 100 / total));
                    }
                }

                DownloadProgressChanged?.Invoke(100);

                var currentDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
                // Standard NSIS flags: /S (silent) /RELAUNCH /D=<dir> (must be last, unquoted per NSIS spec!)
                var args = autoRestart
                    ? $"/S /RELAUNCH /D={currentDir}"
                    : $"/D={currentDir}";

                Process.Start(new ProcessStartInfo(tempPath, args) { UseShellExecute = true });
                if (autoRestart)
                    Application.Current?.Dispatcher.BeginInvoke(() => Application.Current.Shutdown());

                return (true, "Update downloaded — installing and restarting...");
            }
            catch (Exception ex)
            {
                return (false, $"Download failed: {ex.Message}");
            }
        }

        if (!string.IsNullOrEmpty(LastInfo.ReleasePageUrl))
        {
            Process.Start(new ProcessStartInfo(LastInfo.ReleasePageUrl) { UseShellExecute = true });
            return (true, "Opened Git release page.");
        }

        return (false, "No downloadable installer found for this Git release.");
    }

    private static int CompareVersions(string latest, string current)
    {
        var a = ParseSemVer(latest);
        var b = ParseSemVer(current);
        for (int i = 0; i < 3; i++)
        {
            if (a[i] != b[i]) return a[i].CompareTo(b[i]);
        }
        return 0;
    }

    private static int[] ParseSemVer(string v)
    {
        var dash = v.IndexOf('-');
        if (dash >= 0) v = v[..dash];
        var parts = v.Split('.');
        int[] nums = new int[3];
        for (int i = 0; i < Math.Min(3, parts.Length); i++)
            int.TryParse(parts[i], out nums[i]);
        return nums;
    }
}
