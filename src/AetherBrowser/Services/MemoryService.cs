using System.Diagnostics;
using System.Runtime.InteropServices;
using AetherBrowser.Models;
using CefSharp;
using AetherSettings = AetherBrowser.Models.BrowserSettings;

namespace AetherBrowser.Services;

public sealed class MemoryService
{
    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr proc, nint min, nint max);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    /// <summary>
    /// Captures live memory usage strictly across THIS AetherBrowser process and its own
    /// descendant CefSharp.BrowserSubprocess processes (excluding unrelated apps),
    /// plus tab Active / Sleeping / Discarded counts.
    /// </summary>
    public MemorySnapshot CaptureSnapshot(IEnumerable<BrowserTab> tabs)
    {
        var tabList = tabs.ToList();
        var snap = new MemorySnapshot
        {
            TotalTabs = tabList.Count,
            ActiveTabs = tabList.Count(t => t.MemoryState == TabMemoryState.Active),
            SleepingTabs = tabList.Count(t => t.MemoryState == TabMemoryState.Sleeping),
            DiscardedTabs = tabList.Count(t => t.MemoryState == TabMemoryState.Discarded)
        };

        try
        {
            using var current = Process.GetCurrentProcess();
            snap.AppWorkingSetMb = Math.Round(current.WorkingSet64 / (1024.0 * 1024.0), 1);
        }
        catch { }

        double cefBytes = 0;
        int cefCount = 0;

        foreach (var childPid in GetOwnCefSubprocessPids())
        {
            try
            {
                using var proc = Process.GetProcessById(childPid);
                cefBytes += proc.WorkingSet64;
                cefCount++;
            }
            catch { }
        }

        snap.WebViewProcessesMb = Math.Round(cefBytes / (1024.0 * 1024.0), 1);
        snap.WebViewProcessCount = cefCount;
        return snap;
    }

    /// <summary>
    /// Puts an inactive background tab to sleep: hides the Chromium host (discards compositor tiles,
    /// throttles JS timers & animation frames) and trims working sets for minimal idle RAM.
    /// </summary>
    public Task<bool> TrySleepTabAsync(BrowserTab tab)
    {
        if (tab.IsSelected || tab.MemoryState != TabMemoryState.Active)
            return Task.FromResult(false);
        if (tab.Browser is null || tab.Browser.IsDisposed || !tab.IsBrowserInitialized)
            return Task.FromResult(false);

        try
        {
            var host = tab.Browser.GetBrowserHost();
            if (host is not null)
            {
                host.WasHidden(true);
                try
                {
                    if (host.WindowRenderingDisabled)
                        host.WindowlessFrameRate = 1;
                }
                catch { }
            }

            tab.MemoryState = TabMemoryState.Sleeping;
            _ = Task.Run(TrimSubprocessWorkingSets);
            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// Wakes a sleeping tab when the user switches back to it.
    /// </summary>
    public void WakeSleepingTab(BrowserTab tab, AetherSettings settings)
    {
        if (tab.MemoryState == TabMemoryState.Sleeping && tab.Browser is not null && !tab.Browser.IsDisposed)
        {
            try
            {
                var host = tab.Browser.GetBrowserHost();
                if (host is not null)
                {
                    host.WasHidden(false);
                    try
                    {
                        if (host.WindowRenderingDisabled)
                            host.WindowlessFrameRate = 60;
                    }
                    catch { }
                }
                tab.MemoryState = TabMemoryState.Active;
            }
            catch { }
        }
        tab.LastActiveUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// Completely hibernates (discards) a background tab by disposing its ChromiumWebBrowser -> 0 MB RAM.
    /// </summary>
    public bool DiscardTab(BrowserTab tab, Action<BrowserTab> detachFromVisualTree)
    {
        if (tab.IsSelected || tab.MemoryState == TabMemoryState.Discarded)
            return false;

        try
        {
            detachFromVisualTree(tab);
            if (tab.Browser is not null)
            {
                if (!tab.Browser.IsDisposed)
                    tab.Browser.Dispose();
                tab.Browser = null;
            }
            tab.IsBrowserInitialized = false;
            tab.IsLoading = false;
            tab.LoadingProgress = 0;
            tab.MemoryState = TabMemoryState.Discarded;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Runs optional aggressive .NET GC and trims Windows working sets strictly for this AetherBrowser
    /// process and its own descendant CefSharp.BrowserSubprocess renderer/GPU helpers.
    /// </summary>
    public void TrimWorkingSet(bool aggressiveGc = true)
    {
        try
        {
            if (aggressiveGc)
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: false, compacting: true);
                GC.WaitForPendingFinalizers();
            }

            using var proc = Process.GetCurrentProcess();
            SetProcessWorkingSetSize(proc.Handle, (nint)(-1), (nint)(-1));
            TrimSubprocessWorkingSets();
        }
        catch { }
    }

    private static void TrimSubprocessWorkingSets()
    {
        foreach (var childPid in GetOwnCefSubprocessPids())
        {
            try
            {
                using var sub = Process.GetProcessById(childPid);
                SetProcessWorkingSetSize(sub.Handle, (nint)(-1), (nint)(-1));
            }
            catch { }
        }
    }

    /// <summary>
    /// Enumerates only descendant CefSharp.BrowserSubprocess.exe PIDs whose ancestor is Environment.ProcessId
    /// using a single lightweight Win32 Toolhelp32 process snapshot.
    /// </summary>
    private static List<int> GetOwnCefSubprocessPids()
    {
        var result = new List<int>();
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero || snapshot == InvalidHandleValue)
            return result;

        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32First(snapshot, ref entry))
                return result;

            var parentByPid = new Dictionary<uint, uint>(256);
            var cefCandidates = new List<uint>();

            do
            {
                parentByPid[entry.th32ProcessID] = entry.th32ParentProcessID;
                if (entry.szExeFile.Equals("CefSharp.BrowserSubprocess.exe", StringComparison.OrdinalIgnoreCase))
                    cefCandidates.Add(entry.th32ProcessID);
            }
            while (Process32Next(snapshot, ref entry));

            var selfPid = (uint)Environment.ProcessId;
            foreach (var pid in cefCandidates)
            {
                var cur = pid;
                for (int depth = 0; depth < 8; depth++)
                {
                    if (!parentByPid.TryGetValue(cur, out var parent) || parent == 0 || parent == cur)
                        break;
                    if (parent == selfPid)
                    {
                        result.Add((int)pid);
                        break;
                    }
                    cur = parent;
                }
            }
        }
        catch { }
        finally
        {
            CloseHandle(snapshot);
        }

        return result;
    }
}
