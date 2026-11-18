using System.Diagnostics;
using System.Text.Json;

namespace OmniWin.Integration.Harness;

/// <summary>A tool call's answer: the text, and how long the whole round trip took on the client side.</summary>
public sealed record ToolResult(string Text, bool IsError, double RoundTripMs, bool HadCarriageReturns = false)
{
    public string FirstLine
    {
        get
        {
            var i = Text.IndexOf('\n');
            return i < 0 ? Text : Text[..i];
        }
    }

    /// <summary>The tokens of an act result's first line (status=…, input_sent=…, reason=…, channel=…, risk=…, step=…).</summary>
    public IReadOnlyDictionary<string, string> Tokens => ParseTokens(FirstLine);

    public string? Status => Tokens.GetValueOrDefault("status");
    public bool? InputSent => Tokens.TryGetValue("input_sent", out var v) ? v == "true" : null;
    public string? Reason => Tokens.GetValueOrDefault("reason");

    public static Dictionary<string, string> ParseTokens(string line)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq > 0 && !d.ContainsKey(part[..eq])) d[part[..eq]] = part[(eq + 1)..];
        }

        return d;
    }
}

/// <summary>
/// Starts OmniWin.Server over stdio the way an MCP client does. Every stdout line must be JSON-RPC (the stdout guard):
/// a line that is not JSON fails the run. No test-framework dependency, so the benchmark runner can link this file.
/// </summary>
public sealed class McpServer : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task<string> _stderr;
    private readonly List<string> _stdout = [];
    private readonly Stopwatch _clock = new();
    private int _id;

    private McpServer(Process process)
    {
        _process = process;
        _stderr = process.StandardError.ReadToEndAsync();
    }

    public IReadOnlyList<string> StdoutLines => _stdout;

    public string DataDir { get; private init; } = "";

    public static string FindServerExe(string baseDirectory, string? configuration = null)
    {
        var output = new DirectoryInfo(baseDirectory);
        var root = output;
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "OmniWin.sln"))) root = root.Parent;
        if (root is null) throw new InvalidOperationException("Could not find OmniWin.sln above " + baseDirectory);

        // bin\<Configuration>\<tfm>\ : mirror the configuration and target framework the test or runner itself uses.
        var tfm = output.Name;
        var config = configuration ?? output.Parent!.Name;
        return Path.Combine(root.FullName, "src", "OmniWin.Server", "bin", config, tfm, "OmniWin.Server.exe");
    }

    public static async Task<McpServer> StartAsync(string serverExe, string dataDir, string? configJson = null, IDictionary<string, string>? env = null)
    {
        Directory.CreateDirectory(dataDir);
        await File.WriteAllTextAsync(Path.Combine(dataDir, "config.json"),
            configJson ?? """{"allow_elevated":true,"user_activity_cooldown_ms":0}""").ConfigureAwait(false);

        var start = new ProcessStartInfo(serverExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("--data-dir");
        start.ArgumentList.Add(dataDir);
        start.ArgumentList.Add("--test-mode");
        start.ArgumentList.Add("--allow-elevated");
        start.Environment["OMNIWIN_BROWSER_HEADLESS"] = "1";   // no browser window during the suite
        foreach (var kv in env ?? new Dictionary<string, string>()) start.Environment[kv.Key] = kv.Value;

        var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start " + serverExe);
        var server = new McpServer(process) { DataDir = dataDir };
        await server.CallAsync("initialize", new
        {
            protocolVersion = "2025-06-18",
            capabilities = new { },
            clientInfo = new { name = "omniwin-e2e", version = "1" },
        }).ConfigureAwait(false);
        await process.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""").ConfigureAwait(false);
        await process.StandardInput.FlushAsync().ConfigureAwait(false);
        return server;
    }

    public async Task<JsonElement> CallAsync(string method, object parameters, int timeoutSeconds = 90)
    {
        var id = ++_id;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters })).ConfigureAwait(false);
        await _process.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
        while (true)
        {
            var line = await _process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The server closed stdout. stderr: " + await StderrSoFarAsync().ConfigureAwait(false));
            _stdout.Add(line);
            JsonDocument json;
            try
            {
                json = JsonDocument.Parse(line);   // the stdout guard: anything that is not JSON fails here
            }
            catch (JsonException)
            {
                throw new InvalidOperationException("Stdout guard: a non-JSON line was written to stdout: " + line);
            }

            using (json)
            {
                if (!json.RootElement.TryGetProperty("jsonrpc", out var v) || v.GetString() != "2.0")
                {
                    throw new InvalidOperationException("Stdout guard: not a JSON-RPC message: " + line);
                }

                if (json.RootElement.TryGetProperty("id", out var rid) && rid.ValueKind == JsonValueKind.Number && rid.GetInt32() == id)
                {
                    if (json.RootElement.TryGetProperty("error", out var error))
                    {
                        throw new InvalidOperationException("JSON-RPC error: " + error.GetRawText());
                    }

                    return json.RootElement.Clone();
                }
            }
        }
    }

    public async Task<IReadOnlyList<JsonElement>> ListToolsAsync()
    {
        var response = await CallAsync("tools/list", new { }).ConfigureAwait(false);
        return response.GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.Clone()).ToList();
    }

    public async Task<ToolResult> ToolAsync(string name, object arguments, int timeoutSeconds = 90)
    {
        _clock.Restart();
        var response = await CallAsync("tools/call", new { name, arguments }, timeoutSeconds).ConfigureAwait(false);
        var ms = _clock.Elapsed.TotalMilliseconds;
        var result = response.GetProperty("result");
        var text = result.GetProperty("content")[0].GetProperty("text").GetString() ?? "";
        var isError = result.TryGetProperty("isError", out var e) && e.ValueKind == JsonValueKind.True;
        // browser_observe separates lines with CRLF while act results use LF (docs/status/WS4.md finding 4). Normalize for parsing.
        return new ToolResult(text.Replace("\r\n", "\n"), isError, ms, text.Contains('\r'));
    }

    public async Task<string> StderrSoFarAsync()
    {
        if (_process.HasExited) return await _stderr.ConfigureAwait(false);
        return "(server still running)";
    }

    /// <summary>Closes stdin, waits for a clean exit and returns everything the server wrote to stderr.</summary>
    public async Task<string> StopAsync()
    {
        if (!_process.HasExited)
        {
            _process.StandardInput.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().ConfigureAwait(false);
                throw new InvalidOperationException("The server did not shut down when stdin closed.");
            }
        }

        return await _stderr.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // already reported by the test that needed the result
        }
        finally
        {
            // Whatever happened above, never leave the server this object started running. Only this process tree.
            try
            {
                if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // already gone
            }
        }

        _process.Dispose();
    }
}
