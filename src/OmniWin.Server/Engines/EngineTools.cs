using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OmniWin.Core;
using OmniWin.Safety;

namespace OmniWin.Server.Engines;

/// <summary><c>engines_list</c>: always registered, read-only. Shows the configured engines and, on request, their tools.</summary>
[McpServerToolType]
internal sealed class EnginesListTool(EngineManager engines, OmniWinConfig config)
{
    [McpServerTool(Name = "engines_list", Title = "List bridged engines", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("List the other computer-use engines OmniWin can forward to (Windows-MCP, agent-desktop): state, and which tools are " +
        "filtered out. With engine=<name> and tools=true it starts that engine (if needed) and lists its tools with their argument " +
        "names. Forward a call with engine_call. Engine results are never confirmed. Read-only; sends no input.")]
    public async Task<string> EnginesList(CancellationToken ct, string? engine = null, bool tools = false)
    {
        var sb = new StringBuilder();
        sb.Append("engines enabled=").Append(config.Engines.Enabled ? "true" : "false")
          .Append(" power_tools=").Append(config.PowerTools.Enabled ? "on" : "off").Append(" engine_call=")
          .Append(config.Engines.Enabled && engines.AnyUsable ? "available" : "not_registered");
        foreach (var host in engines.Hosts)
        {
            sb.Append('\n').Append(host.Name).Append(" state=").Append(host.State);
            if (host.Config.ExcludeTools.Count > 0) sb.Append(" filtered=").Append(string.Join(',', host.Config.ExcludeTools));
            if (host.Config.ExcludeArguments.Count > 0) sb.Append(" filtered_args=").Append(string.Join(',', host.Config.ExcludeArguments));
            if (host.Config.AllowTools is { } allow) sb.Append(" allow_only=").Append(string.Join(',', allow));
            if (host.LastError is { } error) sb.Append(" last_error=").Append(ResultFormatter.Quote(error, 120));
        }

        if (engines.Hosts.Count == 0) sb.Append("\n(no engine found: nothing installed under the tools folder, or engines are off)");

        if (tools && !string.IsNullOrWhiteSpace(engine))
        {
            if (engines.Find(engine) is not { } host)
            {
                return sb.Append("\nno engine named ").Append(ResultFormatter.Quote(engine, 60)).ToString();
            }

            try
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                limit.CancelAfter(host.Config.StartupTimeoutMs);
                var list = await host.ListToolsAsync(limit.Token).ConfigureAwait(false);
                sb.Append('\n').Append(ResultFormatter.UntrustedHeader);
                foreach (var tool in list.OrderBy(t => t.Name, StringComparer.Ordinal))
                {
                    if (!host.IsAllowed(tool.Name)) continue;
                    var readOnly = tool.ProtocolTool.Annotations?.ReadOnlyHint == true ||
                                   host.Config.ReadOnlyTools.Contains(tool.Name, StringComparer.OrdinalIgnoreCase) ? " [read-only]" : string.Empty;
                    var first = (tool.Description ?? string.Empty).Split('\n', 2)[0];
                    if (first.Length > 200) first = first[..199] + "…";
                    sb.Append("\n- ").Append(tool.Name).Append('(').Append(EngineHost.Arguments(tool)).Append(')').Append(readOnly)
                      .Append(": ").Append(ResultFormatter.OneLine(first));
                }
            }
            catch (Exception ex)
            {
                sb.Append("\ncould not list tools: ").Append(host.LastError ?? ex.GetType().Name);
            }
        }

        return sb.ToString();
    }
}

/// <summary>
/// <c>engine_call</c>: registered only when engines are on and at least one is usable. It forwards one call to a bridged engine.
/// </summary>
internal sealed class EngineCallTool(EngineManager engines)
{
    [McpServerTool(Name = "engine_call", Title = "Call a bridged engine's tool", ReadOnly = false, Idempotent = false, Destructive = true, OpenWorld = true)]
    [Description("Forward one call to another computer-use engine that OmniWin runs (see engines_list): engine is its name, tool the " +
        "engine's own tool name, args an object with the engine's own argument names. Use OmniWin's own tools first; reach for an engine " +
        "when OmniWin has no tool for it. OmniWin cannot check an engine's work: the result is always unverified (a read: retry=safe; " +
        "anything that may have acted: input_sent=true and retry=check_state, so look at the screen before repeating). One engine call " +
        "runs at a time and holds the keyboard and mouse. Tools that run commands, edit the registry or touch files are switched off " +
        "unless power tools are on. " + ToolDocs.RetryRules)]
    public async Task<CallToolResult> EngineCall(string engine, string tool, CancellationToken ct, JsonElement? args = null)
    {
        try
        {
            var outcome = await engines.CallAsync(engine, tool, args, ct).ConfigureAwait(false);
            return EngineManager.ToToolResult(engine, tool, outcome);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var failed = new EngineCallOutcome(ActResult.Failed(true, ReasonCodes.EngineTimeout,
                $"The engine call failed ({ex.GetType().Name}). It may have acted: check the screen.", Timing.Total(0)), []);
            return EngineManager.ToToolResult(engine, tool, failed);
        }
    }
}
