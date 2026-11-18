using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace OmniWin.Integration.Harness;

/// <summary>One DOM event the page itself saw (ground truth that does not come from OmniWin).</summary>
public sealed record TraceEvent(string Page, string Type, string Id, string Tag, bool Trusted, string Value);

/// <summary>
/// Serves testapps/web-fixtures from 127.0.0.1 and records, independently of OmniWin, every click / input / change / key /
/// submit the page receives. A small script is injected into every HTML page; it posts each event to this server with a
/// beacon. A form page can also post its final form to /__form, which is kept as the "server's record of the form".
/// This file has no test-framework dependency so the benchmark runner can link it.
/// </summary>
public sealed class FixtureWebServer : IAsyncDisposable
{
    private const string TraceScript =
        "(function(){function s(t,e){var g=e.target,id='',tag='',v='';try{id=(g&&(g.id||(g.getAttribute&&g.getAttribute('name'))))||'';" +
        "tag=(g&&g.tagName)||'';v=(g&&g.type==='password')?'<pw>':((g&&g.value!==undefined)?String(g.value):'');}catch(x){}" +
        "navigator.sendBeacon('/__event',JSON.stringify({page:location.pathname,type:t,id:id,tag:tag,trusted:!!e.isTrusted,value:v.slice(0,60)}));}" +
        "['click','dblclick','input','change','keydown','submit'].forEach(function(t){document.addEventListener(t,function(e){s(t,e)},true);});})();";

    private readonly HttpListener _listener = new();
    private readonly string _fixturesDir;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentQueue<TraceEvent> _events = new();
    private readonly ConcurrentQueue<string> _forms = new();
    private Task? _loop;

    public FixtureWebServer(string fixturesDir)
    {
        _fixturesDir = fixturesDir;
        var port = FreePort();
        BaseUrl = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(BaseUrl);
    }

    public string BaseUrl { get; }

    public IReadOnlyList<TraceEvent> Events => _events.ToArray();

    /// <summary>The form bodies posted to /__form, oldest first.</summary>
    public IReadOnlyList<string> Forms => _forms.ToArray();

    public void Start()
    {
        _listener.Start();
        _loop = Task.Run(LoopAsync);
    }

    public string Url(string page) => BaseUrl + page;

    public void Clear()
    {
        while (_events.TryDequeue(out _)) { }
        while (_forms.TryDequeue(out _)) { }
    }

    /// <summary>Waits a short, fixed time so beacons already in flight arrive, then returns the events for a page.</summary>
    public async Task<IReadOnlyList<TraceEvent>> SettledAsync(string? page = null, int ms = 400)
    {
        await Task.Delay(ms).ConfigureAwait(false);
        return page is null ? Events : Events.Where(e => e.Page.EndsWith(page, StringComparison.Ordinal)).ToArray();
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested && _listener.IsListening)
        {
            try
            {
                var context = await _listener.GetContextAsync().ConfigureAwait(false);
                _ = Task.Run(() => HandleAsync(context));
            }
            catch (Exception) when (_cts.IsCancellationRequested || !_listener.IsListening)
            {
                break;
            }
            catch
            {
                // keep listening
            }
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";
            if (path == "/__event" && context.Request.HttpMethod == "POST")
            {
                var body = await new StreamReader(context.Request.InputStream, Encoding.UTF8).ReadToEndAsync().ConfigureAwait(false);
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    var r = doc.RootElement;
                    _events.Enqueue(new TraceEvent(
                        r.GetProperty("page").GetString() ?? "", r.GetProperty("type").GetString() ?? "",
                        r.GetProperty("id").GetString() ?? "", r.GetProperty("tag").GetString() ?? "",
                        r.GetProperty("trusted").GetBoolean(), r.GetProperty("value").GetString() ?? ""));
                }
                catch (JsonException)
                {
                    // ignore a malformed beacon
                }

                context.Response.StatusCode = 204;
                return;
            }

            if (path == "/__form" && context.Request.HttpMethod == "POST")
            {
                _forms.Enqueue(await new StreamReader(context.Request.InputStream, Encoding.UTF8).ReadToEndAsync().ConfigureAwait(false));
                context.Response.StatusCode = 204;
                return;
            }

            if (path == "/__trace.js")
            {
                await WriteAsync(context, "application/javascript", Encoding.UTF8.GetBytes(TraceScript)).ConfigureAwait(false);
                return;
            }

            var relative = path.TrimStart('/');
            if (relative.Length == 0) relative = "index.html";
            // Route pages such as /route/next are served by the page that pushed the state.
            var file = Path.GetFullPath(Path.Combine(_fixturesDir, relative));
            if (!file.StartsWith(Path.GetFullPath(_fixturesDir), StringComparison.OrdinalIgnoreCase) || !File.Exists(file))
            {
                context.Response.StatusCode = 404;
                return;
            }

            var bytes = await File.ReadAllBytesAsync(file).ConfigureAwait(false);
            if (file.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            {
                var html = Encoding.UTF8.GetString(bytes);
                var tag = "<script src=\"/__trace.js\"></script>";
                var at = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
                html = at >= 0 ? html.Insert(at, tag) : tag + html;
                await WriteAsync(context, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html)).ConfigureAwait(false);
            }
            else
            {
                await WriteAsync(context, file.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ? "text/plain" : "application/octet-stream", bytes).ConfigureAwait(false);
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

    private static async Task WriteAsync(HttpListenerContext context, string type, byte[] bytes)
    {
        context.Response.StatusCode = 200;
        context.Response.ContentType = type;
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { /* ignore */ }
        try { _listener.Close(); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }
}
