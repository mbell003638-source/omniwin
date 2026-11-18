using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using OmniWin.Integration.Harness;

namespace OmniWin.Bench;

/// <summary>
/// The long-typing check (handoff 22 A3, 23 C15): one <c>desktop_act type</c> of N characters through the published server, into
/// either the F01 fixture's Name box (read back from the fixture's own log) or a new Notepad window (read back through Win32).
/// Replaces the scratch script: the Notepad window is found by comparing the window list before and after this run's own launch
/// (the same rules as the nine tasks), only that window is typed into, emptied and closed, and every existing Notepad window is
/// checked for changes. Real input: needs OMNIWIN_LIVE_SESSION=1. Writes artifacts/benchmarks/typing-vN.json.
/// </summary>
internal static partial class Tasks
{
    private const string TypingWords = "the quick brown fox jumps over the lazy dog 0123456789 ";

    /// <summary>Plain ASCII words, no newline or emoji (Notepad refuses both in <c>type</c>), exactly <paramref name="chars"/> long.</summary>
    internal static string TypingText(int chars)
    {
        var sb = new System.Text.StringBuilder(chars + TypingWords.Length);
        while (sb.Length < chars) sb.Append(TypingWords);
        return sb.ToString(0, chars);
    }

    public static async Task<int> TypingAsync(string root, string target, int chars)
    {
        var started = DateTimeOffset.Now;
        if (target is not ("f01" or "notepad") || chars is < 1 or > 4000)
        {
            Console.Error.WriteLine("usage: OmniWin.Bench typing --target f01|notepad --chars N (1 to 4000)");
            return 2;
        }

        if (!DesktopAvailability.IsAvailable(out var why))
        {
            Console.Error.WriteLine("not run: no connected desktop: " + why);
            return 3;
        }

        var text = TypingText(chars);
        var result = target == "f01" ? await TypeIntoF01Async(root, text) : await TypeIntoNotepadAsync(root, text);

        var dir = Path.Combine(root, "artifacts", "benchmarks");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "typing-v1.json");
        for (var v = 2; File.Exists(path); v++) path = Path.Combine(dir, $"typing-v{v}.json");
        var doc = new Dictionary<string, object?>
        {
            ["started"] = started,
            ["finished"] = DateTimeOffset.Now,
            ["what"] = "one desktop_act type through the published server (real input)",
            ["target"] = target,
            ["chars"] = chars,
        };
        foreach (var (k, v) in result) doc[k] = v;
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("wrote " + path);
        return result.TryGetValue("readback_exact", out var exact) && exact is true ? 0 : 1;
    }

    private static Dictionary<string, object?> TypeResult(ToolResult r, double wallMs, int chars, bool? exact, int? readChars) => new()
    {
        ["status"] = r.Status,
        ["input_sent"] = r.InputSent,
        ["first_line"] = r.FirstLine,
        ["wall_ms"] = Math.Round(wallMs, 1),
        ["ms_per_char"] = Math.Round(wallMs / chars, 2),
        ["readback_exact"] = exact,
        ["readback_chars"] = readChars,
    };

    private static async Task<Dictionary<string, object?>> TypeIntoNotepadAsync(string root, string text)
    {
        var dataDir = Paths.NewDataDir();
        var temp = Path.Combine(Path.GetTempPath(), "OmniWin-test-typing-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);
        var outcome = new Dictionary<string, object?>();
        await using var web = new FixtureWebServer(Paths.Fixtures(root));   // not started: this check uses no browser
        var server = await McpServer.StartAsync(Paths.ServerExe(), dataDir, null, new Dictionary<string, string>());
        var ctx = new Ctx(server, web, temp);
        try
        {
            await ctx.SnapshotAsync();
            if (!ctx.BaselineKnown) return new() { ["not_run"] = "the starting window list could not be read", ["readback_exact"] = null };
            try
            {
                await LaunchNotepad(ctx);   // a new, empty window of this run only, or not run
                var (doc, gen) = await NotepadEditor(ctx);
                var sw = Stopwatch.StartNew();
                var r = await ctx.Tool("desktop_act", new { @ref = doc, gen, action = "type", value = text });
                var wall = sw.Elapsed.TotalMilliseconds;
                await Task.Delay(500);
                var read = ReadNotepadText(ctx.NotepadHandle)?.Replace("\r", "", StringComparison.Ordinal);
                outcome = TypeResult(r, wall, text.Length, read == text, read?.Length);
            }
            catch (NotRunException ex)
            {
                outcome = new() { ["not_run"] = ex.Message, ["readback_exact"] = null };
            }
            finally
            {
                await ctx.CheckBaselineAsync();
                await ctx.CleanupAsync();
            }
        }
        finally
        {
            await server.DisposeAsync();
            if (await Paths.RemoveOwnDataDirAsync(dataDir) is { } dataLeft) ctx.CleanupErrors.Add("data folder not removed: " + dataLeft);
            try
            {
                if (Path.GetFileName(temp).StartsWith("OmniWin-test-typing-", StringComparison.Ordinal)) Directory.Delete(temp, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ctx.CleanupErrors.Add("temp folder not removed: " + ex.Message);
            }
        }

        outcome["wrong_target_evidence"] = ctx.BaselineChanges;
        outcome["input_sent_mismatch_calls"] = ctx.InputSentMismatches;
        outcome["leftover_windows"] = ctx.LeftoverWindows;
        outcome["other_new_windows_not_touched"] = ctx.OtherNewWindows;
        outcome["cleanup_errors"] = ctx.CleanupErrors;
        return outcome;
    }

    private static async Task<Dictionary<string, object?>> TypeIntoF01Async(string root, string text)
    {
        var output = new DirectoryInfo(AppContext.BaseDirectory);
        var fixtureExe = Path.Combine(root, "testapps", "OmniWin.FixtureApp", "bin", output.Parent!.Name, output.Name, "OmniWin.FixtureApp.exe");
        if (!File.Exists(fixtureExe)) return new() { ["not_run"] = "build testapps/OmniWin.FixtureApp first (same configuration): " + fixtureExe, ["readback_exact"] = null };

        var title = "TYPING-" + Guid.NewGuid().ToString("N")[..6];
        var log = Path.Combine(Path.GetTempPath(), "omniwin-test-typing-" + Guid.NewGuid().ToString("N")[..8] + ".log");
        var dataDir = Paths.NewDataDir();
        var outcome = new Dictionary<string, object?>();
        var errors = new List<string>();
        using var fixture = new Process
        {
            StartInfo = new ProcessStartInfo(fixtureExe) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true },
        };
        foreach (var a in new[] { "--scenario", "F01", "--title", title, "--log", log, "--stdin-commands" }) fixture.StartInfo.ArgumentList.Add(a);
        fixture.Start();
        try
        {
            var ready = false;
            var deadline = Stopwatch.StartNew();
            while (!ready && deadline.Elapsed < TimeSpan.FromSeconds(20))
            {
                var lineTask = fixture.StandardOutput.ReadLineAsync();
                if (await Task.WhenAny(lineTask, Task.Delay(TimeSpan.FromSeconds(20))) != lineTask) break;
                if (await lineTask is not { } line) break;
                ready = line.Contains("OMNIWIN_FIXTURE_READY", StringComparison.Ordinal);
            }

            if (!ready) return new() { ["not_run"] = "the F01 fixture window did not appear", ["readback_exact"] = null };

            var server = await McpServer.StartAsync(Paths.ServerExe(), dataDir, null, new Dictionary<string, string>());
            try
            {
                var obs = (await server.ToolAsync("desktop_observe", new { window = title })).Text;
                var m = Regex.Match(obs, @"^\s*(e[0-9]+) [a-z]+ ""Name""", RegexOptions.Multiline);
                if (!m.Success) return new() { ["not_run"] = "the F01 Name box is not in the observation", ["readback_exact"] = null };
                var sw = Stopwatch.StartNew();
                var r = await server.ToolAsync("desktop_act", new { @ref = m.Groups[1].Value, gen = Gen(obs), action = "type", value = text }, 180);
                var wall = sw.Elapsed.TotalMilliseconds;
                await Task.Delay(500);

                // The fixture's own record: the last text_changed of the Name box (the log is still open, so read with sharing).
                string? last = null;
                using (var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream))
                {
                    while (await reader.ReadLineAsync() is { } line)
                    {
                        using var entry = JsonDocument.Parse(line);
                        var e = entry.RootElement;
                        if (e.TryGetProperty("control", out var control) && control.GetString() == "Name" &&
                            e.TryGetProperty("event", out var ev) && ev.GetString() == "text_changed")
                            last = e.TryGetProperty("value", out var value) ? value.GetString() : null;
                    }
                }

                outcome = TypeResult(r, wall, text.Length, last == text, last?.Length);
            }
            finally
            {
                await server.DisposeAsync();
            }
        }
        finally
        {
            try
            {
                fixture.StandardInput.Close();
                if (!fixture.WaitForExit(2000)) fixture.Kill();   // this run's own fixture process
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
            {
                // Already gone.
            }

            if (await Paths.RemoveOwnTempFileAsync(log, "omniwin-test-typing-") is { } logLeft) errors.Add("log not removed: " + logLeft);
            if (await Paths.RemoveOwnDataDirAsync(dataDir) is { } dataLeft) errors.Add("data folder not removed: " + dataLeft);
        }

        outcome["cleanup_errors"] = errors;
        return outcome;
    }
}
