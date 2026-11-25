using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace AetherBrowser.Services;

public static class DefaultBrowserService
{
    private const string AppId = "AetherBrowser";
    private const string ProgId = "AetherHTML";

    public static string ExecutablePath =>
        Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";

    public static bool IsDefaultBrowser()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");
            var progId = key?.GetValue("ProgId") as string;
            return string.Equals(progId, ProgId, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static (bool Ok, string Message) RegisterBrowserCapabilities()
    {
        var exe = ExecutablePath;
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            return (false, "Could not locate AetherBrowser.exe.");

        try
        {
            var iconRef = $"\"{exe}\",0";
            var openCmd = $"\"{exe}\" \"%1\"";

            // 1. Register ProgID (AetherHTML) under HKCU\Software\Classes
            using (var progKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
            {
                progKey.SetValue("", "Aether Browser HTML Document");
                progKey.SetValue("URL Protocol", "");
                using var iconKey = progKey.CreateSubKey("DefaultIcon");
                iconKey.SetValue("", iconRef);
                using var cmdKey = progKey.CreateSubKey(@"shell\open\command");
                cmdKey.SetValue("", openCmd);
            }

            // 2. Register StartMenuInternet client entry (per-user HKCU — no UAC required)
            var clientRoot = $@"Software\Clients\StartMenuInternet\{AppId}";
            using (var clientKey = Registry.CurrentUser.CreateSubKey(clientRoot))
            {
                clientKey.SetValue("", "Aether Browser");

                using (var iconKey = clientKey.CreateSubKey("DefaultIcon"))
                    iconKey.SetValue("", iconRef);

                using (var cmdKey = clientKey.CreateSubKey(@"shell\open\command"))
                    cmdKey.SetValue("", $"\"{exe}\"");

                using var capKey = clientKey.CreateSubKey("Capabilities");
                capKey.SetValue("ApplicationName", "Aether Browser");
                capKey.SetValue("ApplicationDescription", "Ultra-lightweight, privacy-first, minimalist web browser.");
                capKey.SetValue("ApplicationIcon", iconRef);

                using (var urlAssoc = capKey.CreateSubKey("URLAssociations"))
                {
                    urlAssoc.SetValue("http", ProgId);
                    urlAssoc.SetValue("https", ProgId);
                }

                using (var fileAssoc = capKey.CreateSubKey("FileAssociations"))
                {
                    fileAssoc.SetValue(".htm", ProgId);
                    fileAssoc.SetValue(".html", ProgId);
                    fileAssoc.SetValue(".shtml", ProgId);
                    fileAssoc.SetValue(".xhtml", ProgId);
                    fileAssoc.SetValue(".webp", ProgId);
                    fileAssoc.SetValue(".svg", ProgId);
                }
            }

            // 3. Register under HKCU\Software\RegisteredApplications so Windows Default Apps lists Aether Browser
            using (var regApps = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
            {
                regApps.SetValue(AppId, $@"{clientRoot}\Capabilities");
            }

            // 4. Register Applications\AetherBrowser.exe for "Open With"
            using (var appKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Applications\AetherBrowser.exe"))
            {
                appKey.SetValue("FriendlyAppName", "Aether Browser");
                using var cmdKey = appKey.CreateSubKey(@"shell\open\command");
                cmdKey.SetValue("", openCmd);
            }

            return (true, "Aether Browser registered with Windows.");
        }
        catch (Exception ex)
        {
            return (false, $"Registration failed: {ex.Message}");
        }
    }

    public static (bool Ok, string Message) PromptSetAsDefaultBrowser()
    {
        var (ok, msg) = RegisterBrowserCapabilities();
        if (!ok) return (false, msg);

        try
        {
            // Deep-link directly into Windows 11 Default Apps page for Aether Browser
            Process.Start(new ProcessStartInfo($"ms-settings:defaultapps?registeredAppUser={AppId}")
            {
                UseShellExecute = true
            });
            return (true, "Opened Windows Default Apps — click 'Set default' next to Aether Browser.");
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true });
                return (true, "Opened Windows Default Apps — select Aether Browser.");
            }
            catch (Exception ex)
            {
                return (false, $"Could not open Windows Settings: {ex.Message}");
            }
        }
    }
}
