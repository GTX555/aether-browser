using System.IO;
using System.Windows;

namespace AetherInstaller;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var args = e.Args;
        if (args.Contains("--verify-payload", StringComparer.OrdinalIgnoreCase))
        {
            var exe = Environment.ProcessPath ?? "";
            var info = PayloadReader.TryReadFooter(exe);
            Shutdown(info is not null ? 0 : 2);
            return;
        }

        var isUninstall = args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase);
        var isSilent = args.Contains("--silent", StringComparer.OrdinalIgnoreCase);
        var relaunch = args.Contains("--relaunch", StringComparer.OrdinalIgnoreCase);

        string? customDir = null;
        int? waitPid = null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--dir", StringComparison.OrdinalIgnoreCase))
                customDir = args[i + 1];
            else if (args[i].Equals("--wait-pid", StringComparison.OrdinalIgnoreCase) && int.TryParse(args[i + 1], out var p))
                waitPid = p;
        }

        var win = new MainWindow(isUninstall, isSilent, relaunch, customDir, waitPid);
        MainWindow = win;
        win.Show();
    }
}
