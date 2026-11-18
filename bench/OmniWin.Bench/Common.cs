using System.Text.RegularExpressions;
using OmniWin.Integration.Harness;

namespace OmniWin.Bench;

internal static class Paths
{
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OmniWin.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("OmniWin.sln not found above " + AppContext.BaseDirectory);
    }

    public static string ServerExe() => McpServer.FindServerExe(AppContext.BaseDirectory);

    public static string Fixtures(string root) => Path.Combine(root, "testapps", "web-fixtures");

    public static string NewDataDir() => Path.Combine(Path.GetTempPath(), "omniwin-bench", Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Removes one server data folder this run created itself, by its exact path, and only when it is a direct child of
    /// %TEMP%\omniwin-bench (as <see cref="NewDataDir"/> makes them). Edge may still release files for a moment, so it retries a few
    /// times. Returns null when the folder is gone, otherwise the reason it was left (the caller lists it; nothing else is touched).
    /// </summary>
    public static async Task<string?> RemoveOwnDataDirAsync(string dir)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "omniwin-bench")));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
        if (!string.Equals(Path.GetDirectoryName(full), root, StringComparison.OrdinalIgnoreCase))
            return "not a data folder made by this run: " + full;
        string? last = null;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                if (!Directory.Exists(full)) return null;
                Directory.Delete(full, recursive: true);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                last = ex.Message;
                await Task.Delay(300);
            }
        }

        return last;
    }

    /// <summary>
    /// Removes one file this run created itself in %TEMP% whose name starts with <paramref name="prefix"/>. The fixture that wrote it
    /// may release it a moment after it exits, so it retries a few times. Null when it is gone.
    /// </summary>
    public static async Task<string?> RemoveOwnTempFileAsync(string path, string prefix)
    {
        var full = Path.GetFullPath(path);
        var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (!string.Equals(Path.GetDirectoryName(full), temp, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(full).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return "not a file made by this run: " + full;
        string? last = null;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (File.Exists(full)) File.Delete(full);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                last = ex.Message;
                await Task.Delay(300);
            }
        }

        return last;
    }
}

/// <summary>One call: what the tool said and how long it took, measured on both sides.</summary>
internal sealed record Sample(string Status, bool InputSent, string? Reason, double RoundTripMs, double? ServerTotalMs, double? GateMs, IReadOnlyDictionary<string, double>? Phases)
{
    /// <summary>Server time without the safety gate, which contract §15 reports separately.</summary>
    public double? ServerWithoutGateMs => ServerTotalMs is null ? null : ServerTotalMs - (GateMs ?? 0);
}

internal sealed class Row(string name, string section15Row, double? budgetP95Ms, string basis)
{
    public string Name { get; } = name;
    public string Section15Row { get; } = section15Row;
    public double? BudgetP95Ms { get; } = budgetP95Ms;

    /// <summary>Which number is compared with the budget (so the report is explicit about it).</summary>
    public string Basis { get; } = basis;

    public List<Sample> Samples { get; } = [];

    public Dictionary<string, (double P50, double P95, double P99, long Count)> ServerMetrics { get; } = [];

    public double[] Series(Func<Sample, double?> pick) => Samples.Select(pick).Where(v => v.HasValue).Select(v => v!.Value).ToArray();

    public double? Pick(string basis, double p)
    {
        // "server_metric:<op>" compares the server's own rolling metric for that operation (read from omniwin_status).
        if (basis.StartsWith("server_metric:", StringComparison.Ordinal))
        {
            return ServerMetrics.TryGetValue(basis["server_metric:".Length..], out var m) ? (p >= 0.95 ? m.P95 : m.P50) : null;
        }

        var values = basis switch
        {
            "client_round_trip" => Series(s => s.RoundTripMs),
            "server_total_without_gate" => Series(s => s.ServerWithoutGateMs),
            "server_total" => Series(s => s.ServerTotalMs),
            _ => [],
        };
        return values.Length == 0 ? null : Stats.Percentile(values, p);
    }

    public Dictionary<string, int> StatusCounts() => Samples.GroupBy(s => s.Status).ToDictionary(g => g.Key, g => g.Count());
}

internal static partial class Stats
{
    public static double Percentile(double[] values, double p)
    {
        var sorted = values.Order().ToArray();
        var index = Math.Clamp((int)Math.Floor(sorted.Length * p), 0, sorted.Length - 1);
        return Math.Round(sorted[index], 3);
    }

    [GeneratedRegex(@"total=([0-9.]+)ms(?: \((.*?)\))?")]
    private static partial Regex TotalRegex();

    [GeneratedRegex(@"\sp50=([0-9.]+) p95=([0-9.]+) p99=([0-9.]+) n=([0-9]+)")]
    private static partial Regex MetricRegex();

    public static Sample ToSample(ToolResult r)
    {
        var tokens = r.Tokens;
        var status = tokens.GetValueOrDefault("status") ?? "ok";
        var sent = r.InputSent ?? false;
        double? total = null;
        double? gate = null;
        Dictionary<string, double>? phases = null;
        var m = TotalRegex().Match(r.FirstLine);
        if (m.Success)
        {
            total = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (m.Groups[2].Success)
            {
                phases = [];
                foreach (var part in m.Groups[2].Value.Split(',', StringSplitOptions.TrimEntries))
                {
                    var bits = part.Split(' ');
                    if (bits.Length == 2 && double.TryParse(bits[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
                    {
                        phases[bits[0]] = v;
                        if (bits[0] == "gate") gate = v;
                    }
                }
            }
        }

        return new Sample(status, sent, tokens.GetValueOrDefault("reason"), r.RoundTripMs, total, gate, phases);
    }

    /// <summary>The "metrics (server-side ms)" block of omniwin_status: op → p50/p95/p99/n.</summary>
    public static Dictionary<string, (double P50, double P95, double P99, long Count)> ParseMetrics(string statusText)
    {
        var result = new Dictionary<string, (double, double, double, long)>(StringComparer.Ordinal);
        foreach (var line in statusText.Split('\n'))
        {
            var m = MetricRegex().Match(line);
            if (!m.Success) continue;
            var op = line.Trim().Split(' ')[0];
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            result[op] = (double.Parse(m.Groups[1].Value, inv), double.Parse(m.Groups[2].Value, inv), double.Parse(m.Groups[3].Value, inv), long.Parse(m.Groups[4].Value, inv));
        }

        return result;
    }
}
