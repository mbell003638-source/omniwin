using System.Text;
using System.Text.Json;

namespace OmniWin.Bench;

internal static class Report
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private static string FreshPath(string dir, string baseName, string ext)
    {
        Directory.CreateDirectory(dir);
        for (var v = 1; ; v++)
        {
            var path = Path.Combine(dir, $"{baseName}-v{v}{ext}");
            if (!File.Exists(path)) return path;   // never overwrite an earlier artifact
        }
    }

    public static async Task WriteAsync(string root, string stamp, E2EResult? e2e, MicroResult? micro)
    {
        var artifacts = Path.Combine(root, "artifacts", "benchmarks");
        string? e2ePath = null, microPath = null;
        if (e2e is not null)
        {
            e2ePath = FreshPath(artifacts, "ws4-e2e", ".json");
            await File.WriteAllTextAsync(e2ePath, JsonSerializer.Serialize(ToDoc(e2e), Json));
        }

        if (micro is not null)
        {
            microPath = FreshPath(artifacts, "ws4-micro", ".json");
            await File.WriteAllTextAsync(microPath, JsonSerializer.Serialize(micro, Json));
        }

        var md = FreshPath(Path.Combine(root, "bench", "reports"), stamp + "-phase2", ".md");
        await File.WriteAllTextAsync(md, Markdown(stamp, e2e, micro, e2ePath, microPath, root));
        Console.WriteLine("wrote " + (e2ePath ?? "(no e2e)") + "\n      " + (microPath ?? "(no micro)") + "\n      " + md);
    }

    private static object ToDoc(E2EResult r) => new
    {
        r.Started,
        r.BrowserVersion,
        r.Configuration,
        r.Failure,
        WarmupCalls = r.Warmup,
        MeasuredCalls = r.Samples,
        Accuracy = r.Accuracy,
        r.NotMeasured,
        Rows = r.Rows.Select(row => new
        {
            row.Name,
            Section15Row = row.Section15Row,
            BudgetP95Ms = row.BudgetP95Ms,
            ComparedBasis = row.Basis,
            Samples = row.Samples.Count,
            StatusCounts = row.StatusCounts(),
            InputSentTrue = row.Samples.Count(s => s.InputSent),
            ClientRoundTrip = Pcts(row.Series(s => s.RoundTripMs)),
            ServerTotal = Pcts(row.Series(s => s.ServerTotalMs)),
            ServerWithoutGate = Pcts(row.Series(s => s.ServerWithoutGateMs)),
            Phases = row.Samples.Where(s => s.Phases is not null).SelectMany(s => s.Phases!).GroupBy(kv => kv.Key)
                .ToDictionary(g => g.Key, g => Pcts(g.Select(kv => kv.Value).ToArray())),
            ServerMetrics = row.ServerMetrics.ToDictionary(kv => kv.Key, kv => new { kv.Value.P50, kv.Value.P95, kv.Value.P99, N = kv.Value.Count }),
            Raw = new
            {
                Status = row.Samples.Select(s => s.Status),
                InputSent = row.Samples.Select(s => s.InputSent),
                Reason = row.Samples.Select(s => s.Reason),
                ClientRoundTripMs = row.Samples.Select(s => Math.Round(s.RoundTripMs, 3)),
                ServerTotalMs = row.Samples.Select(s => s.ServerTotalMs),
                GateMs = row.Samples.Select(s => s.GateMs),
            },
        }),
    };

    private static object? Pcts(double[] v) => v.Length == 0 ? null : new { N = v.Length, P50 = Stats.Percentile(v, 0.5), P95 = Stats.Percentile(v, 0.95), P99 = Stats.Percentile(v, 0.99) };

    private static string Fmt(double? v) => v is null ? "n/a" : v.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    private static string Markdown(string stamp, E2EResult? e, MicroResult? m, string? e2ePath, string? microPath, string root)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# OmniWin benchmark report - {stamp} - Phase 2 (no real input)");
        sb.AppendLine();
        sb.AppendLine($"contract=see `OmniWinContract.ContractVersion` configuration=Release browser={e?.BrowserVersion ?? "n/a"} samples=warm-up {e?.Warmup} + {e?.Samples} per operation");
        sb.AppendLine($"artifacts: `{Rel(root, e2ePath)}` (key `rows[].client_round_trip|server_total|server_without_gate|phases|server_metrics`), `{Rel(root, microPath)}`");
        if (e?.Failure is not null) sb.AppendLine().AppendLine("**RUN FAILURE:** `" + e.Failure.Split('\n')[0] + "`");
        sb.AppendLine();

        if (e is not null)
        {
            sb.AppendLine("## A. Server-side, against contract section 15");
            sb.AppendLine("| Operation | Compared number | p50 ms | p95 ms | p95 budget | Pass |");
            sb.AppendLine("|---|---|---|---|---|---|");
            foreach (var row in e.Rows)
            {
                var p50 = row.Pick(row.Basis, 0.5);
                var p95 = row.Pick(row.Basis, 0.95);
                var pass = row.BudgetP95Ms is null ? "report only" : p95 is null ? "**not measured**" : (p95 <= row.BudgetP95Ms ? "pass" : "**over budget**");
                sb.AppendLine($"| {row.Section15Row} (`{row.Name}`) | {row.Basis} | {Fmt(p50)} | {Fmt(p95)} | {(row.BudgetP95Ms is null ? "-" : "<= " + row.BudgetP95Ms)} | {pass} |");
            }

            sb.AppendLine();
            sb.AppendLine("## B. Client round trip per tool (stdio, includes transport)");
            sb.AppendLine("| Row | p50 ms | p95 ms | n | statuses | input_sent=true |");
            sb.AppendLine("|---|---|---|---|---|---|");
            foreach (var row in e.Rows)
            {
                sb.AppendLine($"| `{row.Name}` | {Fmt(row.Pick("client_round_trip", 0.5))} | {Fmt(row.Pick("client_round_trip", 0.95))} | {row.Samples.Count} | {string.Join(", ", row.StatusCounts().Select(kv => kv.Key + "=" + kv.Value))} | {row.Samples.Count(s => s.InputSent)} |");
            }

            sb.AppendLine();
            sb.AppendLine("## C. Baseline vs OmniWin, per task");
            sb.AppendLine("Not run: Windows-MCP needs Python and `uv` downloads the owner has not approved. The OmniWin side is `OmniWin.Bench tasks` (Phase 5, owner present).");
            sb.AppendLine();
            sb.AppendLine("## D. Baseline vs OmniWin, per operation type");
            sb.AppendLine("Not run for the same reason.");
            sb.AppendLine();
            sb.AppendLine("## E. Accuracy gates (browser task B09, headless, 20 runs; desktop gates run in Phase 5)");
            var a = e.Accuracy;
            sb.AppendLine("| Gate | Result | Pass |");
            sb.AppendLine("|---|---|---|");
            sb.AppendLine($"| Wrong-target actions | {a.WrongTarget} of {a.Steps} steps | {(a.WrongTarget == 0 ? "pass" : "**FAIL**")} |");
            sb.AppendLine($"| input_sent accuracy | {a.InputSentMismatch} mismatches of {a.Steps} steps | {(a.InputSentMismatch == 0 ? "pass" : "**FAIL**")} |");
            sb.AppendLine($"| False confirmed | {a.FalseConfirmed} | {(a.FalseConfirmed == 0 ? "pass" : "**FAIL**")} |");
            sb.AppendLine($"| Scripted suite success | {a.RunsFullyOk} of {a.Runs} runs fully as expected ({a.StepsAsExpected} of {a.Steps} steps) | {(a.Runs > 0 && a.RunsFullyOk * 100.0 / a.Runs >= 98 ? "pass" : "**below 98 %**")} |");
            sb.AppendLine("| Adversarial cases | see the end-to-end suite (`AdversarialTests`), 100 % required | see docs/status/WS4.md |");
            sb.AppendLine();
            sb.AppendLine("## F. Not measured here");
            foreach (var line in e.NotMeasured) sb.AppendLine("- " + line);
        }

        if (m is not null)
        {
            sb.AppendLine();
            sb.AppendLine("## G. Micro benchmarks (in-process, microseconds per operation)");
            sb.AppendLine("| Row | What | p50 us | p95 us | p99 us |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var r in m.Rows) sb.AppendLine($"| `{r.Name}` | {r.What} | {Fmt(r.P50Us)} | {Fmt(r.P95Us)} | {Fmt(r.P99Us)} |");
            foreach (var line in m.NotMeasured) sb.AppendLine("- " + line);
        }

        return sb.ToString();
    }

    private static string Rel(string root, string? path) => path is null ? "n/a" : Path.GetRelativePath(root, path).Replace('\\', '/');
}
