using AetherBrowser.Services;

namespace AetherBrowser;

public static class Program
{
    public static SingleInstanceService? SingleInstance { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        // Enforce single CEF process BEFORE WPF or CEF ever touches RootCachePath.
        // Secondary launches (e.g. clicking external links when Aether is default browser)
        // forward the URL over NamedPipe IPC to the running Aether window and exit immediately.
        var instance = new SingleInstanceService();
        if (!instance.TryAcquirePrimary())
        {
            SingleInstanceService.ForwardArgsToPrimary(args);
            instance.Dispose();
            return;
        }

        SingleInstance = instance;
        try
        {
            var app = new App();
            app.InitializeComponent();
            app.Run();
        }
        finally
        {
            instance.Dispose();
            SingleInstance = null;
        }
    }
}
