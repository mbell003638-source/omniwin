using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OmniWin.Core;
using OmniWin.Safety;
using OmniWin.Server.Engines;

namespace OmniWin.Server.Router;

/// <summary>
/// Stage B of the router: Laya (a 421M decision model) as a separate local process (<c>tools\laya\laya_server.py</c>). It is started on
/// the first use, talks JSON lines over stdin/stdout, and is ended on a timeout, on the stop key and at shutdown, so a hung or
/// crashed model can never touch input handling. It runs offline (no network) and returns only an intent and a confidence.
/// </summary>
internal sealed class LayaClassifier(OmniWinConfig config, IKillSwitch kill) : IIntentClassifier, IAsyncDisposable
{
    private readonly SemaphoreSlim _one = new(1, 1);
    private Process? _process;
    private int _nextId;

    public static string? Directory(OmniWinConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.Router.Laya.Dir)) return Path.GetFullPath(Environment.ExpandEnvironmentVariables(config.Router.Laya.Dir));
        var tools = EngineCatalog.ResolveToolsDir(config, AppContext.BaseDirectory);
        return tools is null ? null : Path.Combine(tools, "laya");
    }

    /// <summary>Everything the model needs is on disk: its Python, the server script and the weights.</summary>
    public static bool Available(OmniWinConfig config)
    {
        if (!config.Router.Laya.Enabled || Directory(config) is not { } dir) return false;
        return File.Exists(Path.Combine(dir, ".venv", "Scripts", "python.exe")) &&
               File.Exists(Path.Combine(dir, "laya_server.py")) &&
               File.Exists(Path.Combine(dir, "typed-decisions", "model.safetensors"));
    }

    public async Task<IntentGuess?> ClassifyAsync(string text, CancellationToken ct)
    {
        if (!Available(config)) return null;
        await _one.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var watch = Stopwatch.StartNew();
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, kill.Token);
            var process = await EnsureStartedAsync(stop.Token).ConfigureAwait(false);
            var id = Interlocked.Increment(ref _nextId);
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { id, text })).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(stop.Token).ConfigureAwait(false);

            stop.CancelAfter(Math.Max(2000, config.Router.Laya.MaxLatencyMs * 8));
            try
            {
                while (await process.StandardOutput.ReadLineAsync(stop.Token).ConfigureAwait(false) is { } line)
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("id", out var replyId) || replyId.ValueKind != JsonValueKind.Number || replyId.GetInt32() != id) continue;
                    if (root.TryGetProperty("error", out _)) return null;
                    return new IntentGuess(root.GetProperty("intent").GetString() ?? "none", root.GetProperty("confidence").GetDouble(),
                        watch.Elapsed.TotalMilliseconds);
                }

                // The process ended without answering.
                Stop();
                return null;
            }
            catch (OperationCanceledException)
            {
                // Too slow, or the stop key: end the model; the next request starts it again.
                Stop();
                if (ct.IsCancellationRequested) throw;
                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            Stop();
            return null;
        }
        finally
        {
            _one.Release();
        }
    }

    private async Task<Process> EnsureStartedAsync(CancellationToken ct)
    {
        if (_process is { HasExited: false } alive) return alive;
        Stop();

        var dir = Directory(config)!;
        var info = new ProcessStartInfo(Path.Combine(dir, ".venv", "Scripts", "python.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            WorkingDirectory = dir,
        };
        info.ArgumentList.Add(Path.Combine(dir, "laya_server.py"));
        info.Environment["PYTHONUTF8"] = "1";
        info.Environment["HF_HUB_OFFLINE"] = "1";          // the model runs from disk only; it never reaches for the network
        info.Environment["TRANSFORMERS_OFFLINE"] = "1";
        info.Environment["TOKENIZERS_PARALLELISM"] = "false";

        var process = new Process { StartInfo = info };
        process.Start();
        _ = process.StandardError.ReadToEndAsync(CancellationToken.None);   // drain, never shown
        _process = process;

        using var startup = CancellationTokenSource.CreateLinkedTokenSource(ct);
        startup.CancelAfter(180000);
        try
        {
            var ready = await process.StandardOutput.ReadLineAsync(startup.Token).ConfigureAwait(false);
            if (ready is null || !ready.Contains("\"ready\"", StringComparison.Ordinal)) throw new InvalidOperationException("Laya did not report ready.");
        }
        catch
        {
            Stop();
            throw;
        }

        return process;
    }

    private void Stop()
    {
        var process = _process;
        _process = null;
        if (process is null) return;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
        finally
        {
            process.Dispose();
        }
    }

    public ValueTask DisposeAsync()
    {
        Stop();
        return ValueTask.CompletedTask;
    }
}
