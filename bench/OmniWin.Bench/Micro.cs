using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OmniWin.Core;

namespace OmniWin.Bench;

internal sealed record MicroRow(string Name, string What, int OpsPerBatch, int Batches, double P50Us, double P95Us, double P99Us);

internal sealed record MicroResult(string Started, IReadOnlyList<MicroRow> Rows, IReadOnlyList<string> NotMeasured);

/// <summary>
/// In-process timing of the shared formatting and parsing code, with Stopwatch (no BenchmarkDotNet package: no new packages are allowed).
/// Each row is 200 batches of N operations; the numbers are microseconds per operation.
/// </summary>
internal static class Micro
{
    public static MicroResult Run()
    {
        var rows = new List<MicroRow>();
        var names150 = Enumerable.Range(1, 150).Select(i => $"Button number {i} with a typical accessible name").ToArray();
        var names500 = Enumerable.Range(1, 500).Select(i => $"Button number {i} with a typical accessible name").ToArray();

        rows.Add(Time("quote_150_element_names", "ResultFormatter.Quote x150 + line assembly (the formatting cost of a 150-element observation)", 20, () => BuildLines(names150)));
        rows.Add(Time("quote_500_element_names", "ResultFormatter.Quote x500 + line assembly", 5, () => BuildLines(names500)));

        var after = new Observation("t1", 5, "# diff\n+ b1 button \"x\"\n", 1, 0, true, 4, Timing.Total(1));
        var result = new ActResult(ActStatus.Confirmed, true, null, "ok", after, Channels.CdpMouse, new Timing(12.3, new Dictionary<string, double> { ["resolve"] = 1, ["gate"] = 0.1, ["input"] = 5, ["verify"] = 6 }), "s1");
        rows.Add(Time("format_act_result", "ResultFormatter.Format(ActResult) with a diff", 500, () => ResultFormatter.Format(result)));

        var tabs = Enumerable.Range(1, 10).Select(i => new TabInfo("t" + i, "https://example.com/page/" + i, "Page " + i, i == 1)).ToList();
        rows.Add(Time("format_10_tabs", "ResultFormatter.FormatTabs x10 tabs", 500, () => ResultFormatter.FormatTabs(tabs)));

        const string json = """{"kind":"all_of","items":[{"kind":"url_contains","value":"x"},{"kind":"element_appears","role":"button","name_contains":"Save"},{"kind":"window_opened","title_contains":"Save As"}]}""";
        rows.Add(Time("parse_expectation_json", "JsonSerializer.Deserialize<Expectation> of a 3-condition all_of", 500, () => JsonSerializer.Deserialize<Expectation>(json, CoreJson.Options)));

        return new MicroResult(DateTimeOffset.Now.ToString("o"), rows,
        [
            "Diff rendering and the browser/desktop element formatters are internal to the engines; their cost is part of the observe rows in the e2e report.",
            "Desktop observe on F01/F03, cached and cold: needs a visible fixture window: Phase 5.",
        ]);
    }

    private static object BuildLines(string[] names)
    {
        var sb = new StringBuilder(names.Length * 80);
        for (var i = 0; i < names.Length; i++)
        {
            sb.Append("  b").Append(i + 1).Append(" button ").Append(ResultFormatter.Quote(names[i], ResultFormatter.MaxNameChars)).Append(" [click]").Append('\n');
        }

        return sb.ToString();
    }

    private static MicroRow Time(string name, string what, int opsPerBatch, Func<object?> op)
    {
        for (var i = 0; i < 20 * opsPerBatch; i++) op();   // warm-up
        var perOpUs = new double[200];
        for (var b = 0; b < perOpUs.Length; b++)
        {
            var start = Stopwatch.GetTimestamp();
            for (var i = 0; i < opsPerBatch; i++) op();
            perOpUs[b] = Stopwatch.GetElapsedTime(start).TotalMilliseconds * 1000.0 / opsPerBatch;
        }

        return new MicroRow(name, what, opsPerBatch, perOpUs.Length, Stats.Percentile(perOpUs, 0.50), Stats.Percentile(perOpUs, 0.95), Stats.Percentile(perOpUs, 0.99));
    }
}
