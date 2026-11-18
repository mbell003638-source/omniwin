using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OmniWin.Safety;

namespace OmniWin.Server.Engines;

/// <summary>
/// One engine as a child MCP server over stdio. Started on the first use, restarted if it died, never more than one call at a
/// time. Disposing it ends the child and its process tree, which is how the stop key and a timeout take effect.
/// </summary>
internal sealed class EngineHost(EngineConfig config) : IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _call = new(1, 1);
    private McpClient? _client;
    private IList<McpClientTool>? _tools;
    private string? _lastError;

    public EngineConfig Config => config;

    public string Name => config.Name;

    /// <summary>stopped (not started yet), ready, disabled, unavailable (the program is not there).</summary>
    public string State => !config.Enabled ? "disabled" : !CommandExists() ? "unavailable" : _client is { Completion.IsCompleted: false } ? "ready" : "stopped";

    public string? LastError => _lastError;

    public bool CommandExists() => !Path.IsPathRooted(config.Command) || File.Exists(config.Command);

    /// <summary>The first refused argument ("Tool.argument") that this call carries, or null. Checked before the engine is asked.</summary>
    public string? RefusedArgument(string tool, JsonElement? arguments)
    {
        if (config.ExcludeArguments.Count == 0 || arguments is not { ValueKind: JsonValueKind.Object } obj) return null;
        foreach (var property in obj.EnumerateObject())
        {
            var key = tool + "." + property.Name;
            // Report the entry as it is written in the configuration, whatever case the caller used.
            var entry = config.ExcludeArguments.FirstOrDefault(e => string.Equals(e, key, StringComparison.OrdinalIgnoreCase));
            if (entry is not null && property.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)) return entry;
        }

        return null;
    }

    /// <summary>Does OmniWin forward this tool? The exclude list always wins; an allow list, when given, restricts to its names.</summary>
    public bool IsAllowed(string tool)
    {
        if (config.ExcludeTools.Contains(tool, StringComparer.OrdinalIgnoreCase)) return false;
        return config.AllowTools is null || config.AllowTools.Contains(tool, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyList<McpClientTool>> ListToolsAsync(CancellationToken ct)
    {
        var client = await EnsureStartedAsync(ct).ConfigureAwait(false);
        return [.. (_tools ??= await client.ListToolsAsync(cancellationToken: ct).ConfigureAwait(false))];
    }

    /// <summary>Calls one tool and returns its result. The caller has already applied the filter and the lane.</summary>
    public async Task<CallToolResult> CallAsync(string tool, JsonElement? arguments, CancellationToken ct)
    {
        await _call.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var client = await EnsureStartedAsync(ct).ConfigureAwait(false);
            var args = ToArguments(arguments);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(config.CallTimeoutMs);
            try
            {
                return await client.CallToolAsync(tool, args, cancellationToken: limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The engine may be in the middle of input: end it rather than let it keep acting. The next call starts it afresh.
                await StopAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _call.Release();
        }
    }

    /// <summary>Ends the child process and its tree (stop key, timeout, shutdown).</summary>
    public async Task StopAsync()
    {
        McpClient? client;
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            client = _client;
            _client = null;
            _tools = null;
        }
        finally
        {
            _lifecycle.Release();
        }

        if (client is not null)
        {
            try
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The process is already gone or refuses to close; the transport has killed its tree.
            }
        }
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private async Task<McpClient> EnsureStartedAsync(CancellationToken ct)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_client is { Completion.IsCompleted: false } alive) return alive;
            if (_client is not null)
            {
                _client = null;
                _tools = null;
            }

            if (!config.Enabled) throw new InvalidOperationException($"engine {config.Name} is disabled");
            if (!CommandExists()) throw new FileNotFoundException($"engine {config.Name}: {config.Command} was not found");

            var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in config.Env) env[key] = value;
            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = config.Name,
                Command = config.Command,
                Arguments = [.. config.Args],
                WorkingDirectory = config.WorkingDir,
                EnvironmentVariables = env,
                ShutdownTimeout = TimeSpan.FromSeconds(3),
            });

            using var startup = CancellationTokenSource.CreateLinkedTokenSource(ct);
            startup.CancelAfter(config.StartupTimeoutMs);
            try
            {
                _client = await McpClient.CreateAsync(transport, new McpClientOptions
                {
                    ClientInfo = new Implementation { Name = "omniwin-bridge", Version = "1" },
                    // A server on the newest protocol revision (Windows-MCP) answers only server/discover, and a cold start of a
                    // Python engine takes longer than the default 5 s probe: give the probe the engine's whole start-up time,
                    // otherwise the client falls back to the old handshake, which such a server refuses.
                    DiscoverProbeTimeout = TimeSpan.FromMilliseconds(config.StartupTimeoutMs),
                    InitializationTimeout = TimeSpan.FromMilliseconds(config.StartupTimeoutMs),
                }, loggerFactory: null, cancellationToken: startup.Token).ConfigureAwait(false);
                _lastError = null;
                return _client;
            }
            catch (Exception ex)
            {
                _lastError = ex is OperationCanceledException ? "did not start in time" : ex.Message.Split('\n', 2)[0];
                throw;
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private static Dictionary<string, object?> ToArguments(JsonElement? arguments)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (arguments is { ValueKind: JsonValueKind.Object } obj)
        {
            foreach (var property in obj.EnumerateObject()) result[property.Name] = property.Value.Clone();
        }

        return result;
    }

    /// <summary>The names of a tool's arguments, for engines_list.</summary>
    public static string Arguments(McpClientTool tool)
    {
        try
        {
            if (tool.JsonSchema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
            {
                var required = tool.JsonSchema.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array
                    ? req.EnumerateArray().Select(e => e.GetString()).ToHashSet()
                    : [];
                return string.Join(", ", properties.EnumerateObject().Select(p => required.Contains(p.Name) ? p.Name : p.Name + "?"));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
        {
            // An engine with an unusual schema: list no arguments rather than fail the listing.
        }

        return string.Empty;
    }
}
