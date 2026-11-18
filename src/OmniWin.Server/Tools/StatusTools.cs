using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using ModelContextProtocol.Server;
using OmniWin.Core;
using OmniWin.Server.Hosting;

namespace OmniWin.Server.Tools;

[McpServerToolType]
internal static class StatusTools
{
    public const string StatusOp = "omniwin_status";

    /// <summary>Components whose Detail text is shown under the components line.</summary>
    private static readonly string[] DetailComponents = ["browser", "system_tools", "ocr", "workflows", "engines", "router", "learning", "decision"];

    [McpServerTool(Name = "omniwin_status", Title = "OmniWin status", ReadOnly = true, Idempotent = true,
                   Destructive = false, OpenWorld = false)]
    [Description("OmniWin server health: version, contract version, which engines and safety components are running " +
                 "(not_built = not implemented yet), whether a browser is attached, and server-side p50/p95/p99 " +
                 "timings per operation. Read-only; sends no input.")]
    public static string OmniwinStatus(ServerState state, IMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(metrics);

        var start = Stopwatch.GetTimestamp();
        var text = Render(state, metrics.Snapshot());
        metrics.Record(StatusOp, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        return text;
    }

    internal static string Render(ServerState state, MetricsSnapshot snapshot)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(512);

        sb.Append("omniwin ").Append(ServerState.Version)
          .Append(" contract=").Append(OmniWinContract.ContractVersion)
          .Append(" milestone=").Append(ServerState.Milestone)
          .Append(" pid=").Append(state.ProcessId.ToString(inv))
          .Append(" uptime=").Append(Stopwatch.GetElapsedTime(state.StartedTimestamp).TotalSeconds.ToString("0.0", inv)).Append('s')
          .Append('\n');

        // Our own path, not app content: Windows paths can't contain quotes or line breaks, so no escaping.
        sb.Append("data_dir=\"").Append(state.Paths.DataDir).Append('"')
          .Append(state.Paths.IsOverride ? " (override)" : string.Empty)
          .Append(" test_mode=").Append(state.Options.TestMode ? "true" : "false")
          .Append(" elevated=").Append(state.Platform.Elevated ? "true" : "false")
          .Append(" dpi=").Append(state.Platform.DpiAwareness)
          .Append(" confirm_mode=").Append(state.Config.ConfirmMode)
          .Append('\n');

        var byName = new Dictionary<string, IComponentStatus>(StringComparer.Ordinal);
        foreach (var c in state.Components)
        {
            byName[c.Component] = c;
        }

        sb.Append("components:");
        foreach (var name in ServerState.KnownComponents)
        {
            sb.Append(' ').Append(name).Append('=').Append(byName.TryGetValue(name, out var c) ? c.State : "not_built");
        }

        foreach (var c in state.Components)
        {
            if (!ServerState.KnownComponents.Contains(c.Component))
            {
                sb.Append(' ').Append(c.Component).Append('=').Append(c.State);
            }
        }

        sb.Append('\n');

        // One line of detail for the engines built after M1, when they have something to say (counts, what is attached).
        foreach (var name in DetailComponents)
        {
            if (byName.TryGetValue(name, out var detailed) && detailed.Detail is { Length: > 0 } detail)
            {
                sb.Append(name).Append(": ").Append(ResultFormatter.OneLine(detail)).Append('\n');
            }
        }

        var browserAttached = byName.TryGetValue("browser", out var browser) && browser.State == "attached";
        sb.Append("browser_attached=").Append(browserAttached ? "true" : "false").Append('\n');

        if (!byName.ContainsKey("safety_gate"))
        {
            sb.Append("note: the safety layer is not built yet, so no tool that sends input is registered.\n");
        }

        if (snapshot.Ops.Count == 0)
        {
            sb.Append("metrics: none yet");
        }
        else
        {
            sb.Append("metrics (server-side ms):");
            foreach (var op in snapshot.Ops.Keys.Order(StringComparer.Ordinal))
            {
                var m = snapshot.Ops[op];
                sb.Append('\n').Append("  ").Append(op)
                  .Append(" p50=").Append(ResultFormatter.FormatMs(m.P50))
                  .Append(" p95=").Append(ResultFormatter.FormatMs(m.P95))
                  .Append(" p99=").Append(ResultFormatter.FormatMs(m.P99))
                  .Append(" n=").Append(m.Count.ToString(inv));
            }
        }

        return sb.ToString();
    }
}
