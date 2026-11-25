using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace AetherInstaller;

public partial class MainWindow : Window
{
    private readonly bool _isUninstall;
    private readonly bool _isSilent;
    private readonly bool _relaunch;
    private readonly int? _waitPid;
    private readonly string _selfExe;
    private readonly PayloadInfo? _payload;
    private string _installDir;
    private bool _isBusy;

    private static string InstallerVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? "2.1.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    public MainWindow(bool isUninstall, bool isSilent, bool relaunch, string? customDir, int? waitPid)
    {
        _isUninstall = isUninstall;
        _isSilent = isSilent;
        _relaunch = relaunch;
        _waitPid = waitPid;
        _selfExe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
        _payload = PayloadReader.TryReadFooter(_selfExe);

        InitializeComponent();

        VersionBadgeText.Text = $"v{InstallerVersion}";

        var existing = WindowsIntegration.DetectExistingInstall();
        _installDir = !string.IsNullOrWhiteSpace(customDir)
            ? customDir
            : (existing.InstallDir ?? WindowsIntegration.DefaultInstallDir);
        InstallDirBox.Text = _installDir;

        if (_isUninstall)
        {
            ShowStep("uninstall");
            if (_isSilent)
                Loaded += async (_, _) => await RunUninstallAsync();
            return;
        }

        if (existing.IsInstalled && !string.IsNullOrWhiteSpace(existing.Version))
        {
            WelcomeHeadline.Text = $"Update Aether Browser";
            WelcomeSubline.Text = $"Upgrade existing v{existing.Version} to v{InstallerVersion} while keeping all your data.";
            QuickInstallBtn.Content = $"Update to v{InstallerVersion}";
        }

        if (_payload is not null)
        {
            try
            {
                var bytes = PayloadReader.EstimateUncompressedBytes(_selfExe, _payload);
                DiskSpaceText.Text = $"Estimated disk space required: ~{Math.Max(1, bytes / (1024 * 1024))} MB";
            }
            catch { }
        }

        if (_isSilent)
        {
            ChkLaunchAfter.IsChecked = _relaunch;
            Loaded += async (_, _) => await RunInstallAsync();
        }
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void ShowStep(string step)
    {
        StepWelcome.Visibility = step == "welcome" ? Visibility.Visible : Visibility.Collapsed;
        StepCustomize.Visibility = step == "customize" ? Visibility.Visible : Visibility.Collapsed;
        StepInstalling.Visibility = step == "installing" ? Visibility.Visible : Visibility.Collapsed;
        StepComplete.Visibility = step == "complete" ? Visibility.Visible : Visibility.Collapsed;
        StepUninstall.Visibility = step == "uninstall" ? Visibility.Visible : Visibility.Collapsed;

        FooterWelcome.Visibility = step == "welcome" ? Visibility.Visible : Visibility.Collapsed;
        FooterCustomize.Visibility = step == "customize" ? Visibility.Visible : Visibility.Collapsed;
        FooterComplete.Visibility = step == "complete" ? Visibility.Visible : Visibility.Collapsed;
        FooterUninstall.Visibility = step == "uninstall" ? Visibility.Visible : Visibility.Collapsed;
        StepDots.Visibility = step == "uninstall" ? Visibility.Collapsed : Visibility.Visible;

        var active = (Brush)FindResource("Accent");
        var idle = (Brush)FindResource("Border");
        Dot1.Fill = step == "welcome" ? active : idle;
        Dot2.Fill = step == "customize" ? active : idle;
        Dot3.Fill = step == "installing" ? active : idle;
        Dot4.Fill = step == "complete" ? active : idle;
    }

    private void GoCustomize_Click(object sender, RoutedEventArgs e) => ShowStep("customize");
    private void BackToWelcome_Click(object sender, RoutedEventArgs e) => ShowStep("welcome");

    private void BrowseDir_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Choose Aether Browser Installation Folder",
            InitialDirectory = Directory.Exists(InstallDirBox.Text) ? InstallDirBox.Text : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        };
        if (dlg.ShowDialog(this) == true)
            InstallDirBox.Text = dlg.FolderName;
    }

    private async void StartInstall_Click(object sender, RoutedEventArgs e)
    {
        await RunInstallAsync();
    }

    private async Task RunInstallAsync()
    {
        if (_isBusy) return;
        if (_payload is null)
        {
            MessageBox.Show(this, "Installer payload is missing or corrupted. Please download a fresh AetherBrowser-Setup.exe.", "Aether Setup", MessageBoxButton.OK, MessageBoxImage.Error);
            if (_isSilent) Close();
            return;
        }

        _isBusy = true;
        CloseBtn.IsEnabled = false;
        _installDir = string.IsNullOrWhiteSpace(InstallDirBox.Text)
            ? WindowsIntegration.DefaultInstallDir
            : InstallDirBox.Text.Trim();

        var createDesktop = ChkDesktop.IsChecked == true;
        var createStartMenu = ChkStartMenu.IsChecked == true;
        var registerBrowser = ChkRegisterBrowser.IsChecked == true;
        var launchAfter = ChkLaunchAfter.IsChecked == true;

        ShowStep("installing");
        InstallTitleText.Text = _isSilent ? $"Updating Aether Browser to v{InstallerVersion}..." : $"Installing Aether Browser v{InstallerVersion}...";

        try
        {
            await Task.Run(() =>
            {
                ReportProgress(4, "Closing running Aether Browser instances...");
                WindowsIntegration.WaitForAndCloseRunningBrowser(_waitPid);

                var estimatedBytes = PayloadReader.EstimateUncompressedBytes(_selfExe, _payload);
                PayloadReader.ExtractPayload(_selfExe, _payload, _installDir, (pct, status) =>
                {
                    ReportProgress(5 + pct, status);
                });

                var mainExe = Path.Combine(_installDir, "AetherBrowser.exe");
                var uninstallerExe = Path.Combine(_installDir, "AetherUninstall.exe");

                ReportProgress(93, "Writing standalone uninstaller...");
                PayloadReader.WriteUninstallerStub(_selfExe, _payload, uninstallerExe);

                ReportProgress(96, "Creating Windows shortcuts...");
                if (createStartMenu)
                    WindowsIntegration.CreateShortcut(WindowsIntegration.StartMenuShortcutPath, mainExe, _installDir, "Aether Browser");
                if (createDesktop)
                    WindowsIntegration.CreateShortcut(WindowsIntegration.DesktopShortcutPath, mainExe, _installDir, "Aether Browser");

                ReportProgress(98, "Registering in Windows Installed Apps...");
                WindowsIntegration.RegisterUninstaller(_installDir, InstallerVersion, uninstallerExe, estimatedBytes);
                if (registerBrowser)
                    WindowsIntegration.RegisterDefaultBrowserCapabilities(mainExe);

                ReportProgress(100, "Installation complete!");
            });

            if (_isSilent)
            {
                if (launchAfter) LaunchBrowser();
                Close();
                return;
            }

            CompleteTitle.Text = $"Aether Browser v{InstallerVersion} is ready";
            CompleteDetail.Text = $"Installed cleanly to {_installDir}. You can manage or uninstall it anytime in Windows Settings → Installed Apps.";
            ShowStep("complete");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Installation failed:\n{ex.Message}", "Aether Setup", MessageBoxButton.OK, MessageBoxImage.Error);
            ShowStep("welcome");
        }
        finally
        {
            _isBusy = false;
            CloseBtn.IsEnabled = true;
        }
    }

    private async void ConfirmUninstall_Click(object sender, RoutedEventArgs e)
    {
        await RunUninstallAsync();
    }

    private async Task RunUninstallAsync()
    {
        if (_isBusy) return;
        _isBusy = true;
        CloseBtn.IsEnabled = false;
        var deleteData = ChkDeleteUserData.IsChecked == true;
        var existing = WindowsIntegration.DetectExistingInstall();
        var targetDir = existing.InstallDir ?? Path.GetDirectoryName(_selfExe) ?? WindowsIntegration.DefaultInstallDir;

        ShowStep("installing");
        InstallTitleText.Text = "Uninstalling Aether Browser...";

        try
        {
            await Task.Run(() =>
            {
                ReportProgress(20, "Closing Aether Browser...");
                WindowsIntegration.WaitForAndCloseRunningBrowser();

                ReportProgress(60, "Removing shortcuts and Windows registry entries...");
                WindowsIntegration.RemoveRegistrationAndShortcuts();

                ReportProgress(95, "Scheduling file cleanup...");
                WindowsIntegration.ScheduleDirectoryCleanup(targetDir, deleteData);
                ReportProgress(100, "Aether Browser has been uninstalled.");
            });

            if (!_isSilent)
                MessageBox.Show(this, "Aether Browser has been uninstalled.", "Aether Browser", MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Uninstall failed: {ex.Message}", "Aether Browser", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
        finally
        {
            _isBusy = false;
        }
    }

    private void ReportProgress(int percent, string status)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var clamped = Math.Clamp(percent, 0, 100);
            var trackWidth = Math.Max(100, StepInstalling.ActualWidth);
            ProgressFill.Width = trackWidth * (clamped / 100.0);
            ProgressPercentText.Text = $"{clamped}%";
            InstallStatusText.Text = status;
        });
    }

    private void LaunchBrowser()
    {
        var mainExe = Path.Combine(_installDir, "AetherBrowser.exe");
        if (File.Exists(mainExe))
        {
            Process.Start(new ProcessStartInfo(mainExe)
            {
                WorkingDirectory = _installDir,
                UseShellExecute = true
            });
        }
    }

    private void OpenDefaultApps_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:defaultapps?registeredAppUser=AetherBrowser") { UseShellExecute = true });
        }
        catch
        {
            Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true });
        }
    }

    private void LaunchAndClose_Click(object sender, RoutedEventArgs e)
    {
        LaunchBrowser();
        Close();
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!_isBusy) Close();
    }
}
