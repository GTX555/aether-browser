using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace AetherBrowser.Services;

/// <summary>
/// Ensures only one Aether Browser CEF process ever locks RootCachePath.
/// Secondary launches (e.g. clicking an external http/https link in another app or clicking the shortcut again)
/// forward their arguments over a local named pipe to the running instance and exit immediately.
/// </summary>
public sealed class SingleInstanceService : IDisposable
{
    private const string MutexName = "Local\\AetherBrowser_SingleInstance_v2";
    private const string PipeName = "AetherBrowser_IPC_v2";

    private Mutex? _mutex;
    private CancellationTokenSource? _cts;
    private bool _isPrimary;

    public bool IsPrimary => _isPrimary;

    public bool TryAcquirePrimary()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        _isPrimary = createdNew;
        return createdNew;
    }

    public static bool ForwardArgsToPrimary(string[] args)
    {
        var payload = JsonSerializer.Serialize(args ?? Array.Empty<string>());
        var bytes = Encoding.UTF8.GetBytes(payload);

        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.None);
                client.Connect(1200);
                client.Write(bytes, 0, bytes.Length);
                client.Flush();
                return true;
            }
            catch
            {
                Thread.Sleep(150);
            }
        }
        return false;
    }

    public void StartListening(Action<string[]> onArgsReceived)
    {
        if (!_isPrimary || _cts is not null) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.In,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(token);

                    using var ms = new MemoryStream();
                    var buf = new byte[4096];
                    int read;
                    while ((read = await server.ReadAsync(buf, 0, buf.Length, token)) > 0)
                        ms.Write(buf, 0, read);

                    var json = Encoding.UTF8.GetString(ms.ToArray());
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        var args = JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>();
                        onArgsReceived(args);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    try { await Task.Delay(100, token); } catch { break; }
                }
            }
        }, token);
    }

    public static string? ExtractLaunchUrl(string[]? args)
    {
        if (args is null || args.Length == 0) return null;

        foreach (var raw in args)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var a = raw.Trim().Trim('"');
            if (a.StartsWith("--") || a.StartsWith("/")) continue;

            if (a.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                a.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                a.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ||
                a.StartsWith("aether://", StringComparison.OrdinalIgnoreCase))
            {
                return a;
            }

            // Local HTML / SVG / WebP file passed by Windows Explorer
            try
            {
                if (File.Exists(a))
                    return new Uri(Path.GetFullPath(a)).AbsoluteUri;
            }
            catch { }
        }

        return null;
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _cts?.Dispose(); } catch { }
        _cts = null;

        if (_mutex is not null)
        {
            try { if (_isPrimary) _mutex.ReleaseMutex(); } catch { }
            try { _mutex.Dispose(); } catch { }
            _mutex = null;
        }
    }
}
