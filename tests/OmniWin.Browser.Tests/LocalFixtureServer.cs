using System.Net;
using System.Text;

namespace OmniWin.Browser.Tests;

public sealed class LocalFixtureServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _fixturesDir;
    private readonly CancellationTokenSource _cts = new();
    private Task? _listenTask;

    public LocalFixtureServer(string fixturesDir)
    {
        _fixturesDir = fixturesDir;
        var port = FindFreePort();
        BaseUrl = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(BaseUrl);
    }

    public string BaseUrl { get; }

    public void Start()
    {
        _listener.Start();
        _listenTask = Task.Run(ListenLoopAsync);
    }

    private async Task ListenLoopAsync()
    {
        while (!_cts.IsCancellationRequested && _listener.IsListening)
        {
            try
            {
                var context = await _listener.GetContextAsync().ConfigureAwait(false);
                _ = ProcessRequestAsync(context);
            }
            catch (Exception) when (_cts.IsCancellationRequested || !_listener.IsListening)
            {
                break;
            }
            catch
            {
                // Continue listening
            }
        }
    }

    private async Task ProcessRequestAsync(HttpListenerContext context)
    {
        try
        {
            var rawPath = context.Request.Url?.AbsolutePath.TrimStart('/') ?? string.Empty;
            if (string.IsNullOrEmpty(rawPath))
            {
                rawPath = "index.html";
            }

            var filePath = Path.Combine(_fixturesDir, rawPath);
            if (File.Exists(filePath))
            {
                var bytes = await File.ReadAllBytesAsync(filePath).ConfigureAwait(false);
                context.Response.StatusCode = 200;
                context.Response.ContentType = GetContentType(filePath);
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            }
            else
            {
                context.Response.StatusCode = 404;
                var notFound = Encoding.UTF8.GetBytes("Not Found");
                await context.Response.OutputStream.WriteAsync(notFound).ConfigureAwait(false);
            }
        }
        catch
        {
            try { context.Response.StatusCode = 500; } catch { /* ignore */ }
        }
        finally
        {
            try { context.Response.Close(); } catch { /* ignore */ }
        }
    }

    private static string GetContentType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js" => "application/javascript",
            ".css" => "text/css",
            ".json" => "application/json",
            _ => "application/octet-stream"
        };
    }

    private static int FindFreePort()
    {
        using var socket = new System.Net.Sockets.Socket(
            System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Stream,
            System.Net.Sockets.ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { /* ignore */ }
        try { _listener.Close(); } catch { /* ignore */ }
        if (_listenTask is not null)
        {
            try { await _listenTask.ConfigureAwait(false); } catch { /* ignore */ }
        }
        _cts.Dispose();
    }
}
