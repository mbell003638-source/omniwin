using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OmniWin.App;

/// <summary>Calls one OmniWin tool and returns its text. The app's only way to do anything on the computer.</summary>
internal interface IToolCaller
{
    Task<string> CallAsync(string tool, object? arguments, CancellationToken ct);
}

/// <summary>
/// The app's own OmniWin.Server.exe, started as a child and spoken to over MCP (JSON-RPC lines on stdin/stdout), exactly like an AI
/// client does. Every check, the safety gate and the stop key therefore apply to what the app asks for. One call at a time. The
/// child ends when this object is disposed (its stdin closes) and is started again on the next call.
/// </summary>
internal sealed class ServerLink(string serverExe, string? dataDir = null) : IToolCaller, IDisposable
{
    private readonly SemaphoreSlim _one = new(1, 1);
    private Process? _process;
    private int _nextId;

    public string ServerExe => serverExe;

    public bool Running => _process is { HasExited: false };

    public async Task<string> CallAsync(string tool, object? arguments, CancellationToken ct)
    {
        await _one.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var process = await EnsureStartedAsync(ct).ConfigureAwait(false);
            var reply = await RequestAsync(process, "tools/call", new { name = tool, arguments = arguments ?? new { } }, ct).ConfigureAwait(false);
            if (reply["error"] is { } error) throw new InvalidOperationException("OmniWin refused the call: " + (error["message"]?.GetValue<string>() ?? "unknown error"));
            var text = new StringBuilder();
            foreach (var block in reply["result"]?["content"]?.AsArray() ?? [])
            {
                if (block?["type"]?.GetValue<string>() == "text") text.Append(block["text"]?.GetValue<string>());
            }

            return text.ToString();
        }
        catch (Exception ex) when (ex is (IOException and not FileNotFoundException) or JsonException or ObjectDisposedException)
        {
            Stop();
            throw new InvalidOperationException("The OmniWin server stopped answering. It will be started again on the next request.", ex);
        }
        finally
        {
            _one.Release();
        }
    }

    private async Task<Process> EnsureStartedAsync(CancellationToken ct)
    {
        if (_process is { HasExited: false } running) return running;
        Stop();
        if (!File.Exists(serverExe)) throw new FileNotFoundException("OmniWin.Server.exe was not found at " + serverExe + ". Set its path in Settings.", serverExe);

        var info = new ProcessStartInfo(serverExe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        if (dataDir is not null)
        {
            info.ArgumentList.Add("--data-dir");
            info.ArgumentList.Add(dataDir);
        }

        var process = Process.Start(info) ?? throw new InvalidOperationException("OmniWin.Server.exe could not be started.");
        process.ErrorDataReceived += static (_, _) => { };   // drain stderr so the child never blocks on it
        process.BeginErrorReadLine();
        _process = process;
        await RequestAsync(process, "initialize", new
        {
            protocolVersion = "2024-11-05",
            capabilities = new { },
            clientInfo = new { name = "omniwin-app", version = "0.1.0" },
        }, ct).ConfigureAwait(false);
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "notifications/initialized" })).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
        return process;
    }

    private async Task<JsonNode> RequestAsync(Process process, string method, object parameters, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters })).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
        while (await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0) continue;
            var node = JsonNode.Parse(line);
            if (node?["id"] is JsonValue value && value.TryGetValue<int>(out var got) && got == id) return node;
        }

        throw new IOException("The OmniWin server closed its output.");
    }

    /// <summary>Ends the child (closing its stdin makes the server exit by itself; it is killed only if it does not).</summary>
    public void Stop()
    {
        var process = _process;
        _process = null;
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                process.StandardInput.Close();
                if (!process.WaitForExit(5000)) process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
        finally
        {
            process.Dispose();
        }
    }

    public void Dispose()
    {
        Stop();
        _one.Dispose();
    }
}
