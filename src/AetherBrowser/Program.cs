using Velopack;

namespace AetherBrowser;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Velopack handles installer shortcuts, updates, and uninstall lifecycle hooks seamlessly.
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
