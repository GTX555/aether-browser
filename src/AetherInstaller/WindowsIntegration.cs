using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Microsoft.Win32;

namespace AetherInstaller;

public static class WindowsIntegration
{
    private const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\AetherBrowser";
    private const string AppId = "AetherBrowser";
    private const string ProgId = "AetherHTML";

    public static string DefaultInstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AetherBrowser", "App");

    public static string UserDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AetherBrowser");

    public static (bool IsInstalled, string? InstallDir, string? Version) DetectExistingInstall()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKeyPath);
            if (key is null) return (false, null, null);
            var loc = key.GetValue("InstallLocation") as string;
            var ver = key.GetValue("DisplayVersion") as string;
            if (!string.IsNullOrWhiteSpace(loc) && Directory.Exists(loc))
                return (true, loc, ver);
        }
        catch { }
        return (false, null, null);
    }

    public static void WaitForAndCloseRunningBrowser(int? waitPid = null)
    {
        if (waitPid.HasValue)
        {
            try
            {
                using var p = Process.GetProcessById(waitPid.Value);
                if (!p.WaitForExit(4000))
                    p.Kill(entireProcessTree: true);
            }
            catch { }
        }

        foreach (var proc in Process.GetProcessesByName("AetherBrowser"))
        {
            try
            {
                proc.CloseMainWindow();
                if (!proc.WaitForExit(2500))
                    proc.Kill(entireProcessTree: true);
            }
            catch { }
            finally { proc.Dispose(); }
        }

        Thread.Sleep(250);
    }

    public static void CreateShortcut(string shortcutPath, string targetExe, string workingDir, string description)
    {
        var dir = Path.GetDirectoryName(shortcutPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var link = (IShellLinkW)new ShellLink();
        link.SetPath(targetExe);
        link.SetWorkingDirectory(workingDir);
        link.SetDescription(description);
        link.SetIconLocation(targetExe, 0);

        var file = (IPersistFile)link;
        file.Save(shortcutPath, false);
    }

    public static string DesktopShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        "Aether Browser.lnk");

    public static string StartMenuShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
        "Programs", "Aether Browser.lnk");

    public static void RegisterUninstaller(string installDir, string version, string uninstallerExe, long estimatedBytes)
    {
        var mainExe = Path.Combine(installDir, "AetherBrowser.exe");
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKeyPath);
        key.SetValue("DisplayName", "Aether Browser");
        key.SetValue("DisplayVersion", version);
        key.SetValue("Publisher", "Aether");
        key.SetValue("InstallLocation", installDir);
        key.SetValue("DisplayIcon", $"\"{mainExe}\",0");
        key.SetValue("UninstallString", $"\"{uninstallerExe}\" --uninstall");
        key.SetValue("QuietUninstallString", $"\"{uninstallerExe}\" --uninstall --silent");
        key.SetValue("EstimatedSize", (int)Math.Max(1, estimatedBytes / 1024), RegistryValueKind.DWord);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
    }

    public static void RegisterDefaultBrowserCapabilities(string mainExe)
    {
        var iconRef = $"\"{mainExe}\",0";
        var openCmd = $"\"{mainExe}\" \"%1\"";

        using (var progKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
        {
            progKey.SetValue("", "Aether Browser HTML Document");
            progKey.SetValue("URL Protocol", "");
            using var iconKey = progKey.CreateSubKey("DefaultIcon");
            iconKey.SetValue("", iconRef);
            using var cmdKey = progKey.CreateSubKey(@"shell\open\command");
            cmdKey.SetValue("", openCmd);
        }

        var clientRoot = $@"Software\Clients\StartMenuInternet\{AppId}";
        using (var clientKey = Registry.CurrentUser.CreateSubKey(clientRoot))
        {
            clientKey.SetValue("", "Aether Browser");
            using var iconKey = clientKey.CreateSubKey("DefaultIcon");
            iconKey.SetValue("", iconRef);
            using var cmdKey = clientKey.CreateSubKey(@"shell\open\command");
            cmdKey.SetValue("", $"\"{mainExe}\"");

            using var capKey = clientKey.CreateSubKey("Capabilities");
            capKey.SetValue("ApplicationName", "Aether Browser");
            capKey.SetValue("ApplicationDescription", "Ultra-lightweight, privacy-first, open-source web browser.");
            capKey.SetValue("ApplicationIcon", iconRef);

            using var urlAssoc = capKey.CreateSubKey("URLAssociations");
            urlAssoc.SetValue("http", ProgId);
            urlAssoc.SetValue("https", ProgId);

            using var fileAssoc = capKey.CreateSubKey("FileAssociations");
            fileAssoc.SetValue(".htm", ProgId);
            fileAssoc.SetValue(".html", ProgId);
            fileAssoc.SetValue(".xhtml", ProgId);
            fileAssoc.SetValue(".webp", ProgId);
            fileAssoc.SetValue(".svg", ProgId);
        }

        using var regApps = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications");
        regApps.SetValue(AppId, $@"{clientRoot}\Capabilities");
    }

    public static void RemoveRegistrationAndShortcuts()
    {
        try { if (File.Exists(DesktopShortcutPath)) File.Delete(DesktopShortcutPath); } catch { }
        try { if (File.Exists(StartMenuShortcutPath)) File.Delete(StartMenuShortcutPath); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, throwOnMissingSubKey: false); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ProgId}", throwOnMissingSubKey: false); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree($@"Software\Clients\StartMenuInternet\{AppId}", throwOnMissingSubKey: false); } catch { }
        try
        {
            using var regApps = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", writable: true);
            regApps?.DeleteValue(AppId, throwOnMissingValue: false);
        }
        catch { }
    }

    public static void ScheduleDirectoryCleanup(string installDir, bool deleteUserData)
    {
        var cmds = new StringBuilder();
        cmds.Append("/C ping 127.0.0.1 -n 3 > nul & ");
        cmds.Append($"rmdir /S /Q \"{installDir}\"");
        if (deleteUserData && Directory.Exists(UserDataDir))
            cmds.Append($" & rmdir /S /Q \"{UserDataDir}\"");

        Process.Start(new ProcessStartInfo("cmd.exe", cmds.ToString())
        {
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            UseShellExecute = false
        });
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, int fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(IntPtr hwnd, int fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}
