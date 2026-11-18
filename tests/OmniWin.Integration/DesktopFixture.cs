using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OmniWin.Integration.Harness;

namespace OmniWin.Integration;

/// <summary>
/// One running scenario of testapps/OmniWin.FixtureApp (F01..F17). Starting it shows a real window, so only live-session tests use it;
/// they hold the shared UI lock (<c>Local\OmniWin.UiInputTestLock</c>) for as long as the window exists. The scenario's own JSONL
/// log is the ground truth for what input really arrived: it never comes from OmniWin's output.
/// </summary>
public sealed class DesktopFixture : IAsyncDisposable
{
    public const string UiLockName = @"Local\OmniWin.UiInputTestLock";

    private readonly Process _process;
    private readonly UiLockHolder? _uiLock;
    private readonly List<string> _stdout = [];

    private DesktopFixture(Process process, UiLockHolder? uiLock, string title, string logPath, string scenario)
    {
        _process = process;
        _uiLock = uiLock;
        Title = title;
        LogPath = logPath;
        Scenario = scenario;
    }

    public string Scenario { get; }

    /// <summary>Unique window title (a scenario name plus a random suffix), so desktop_observe can name exactly this window.</summary>
    public string Title { get; }

    public string LogPath { get; }

    public IReadOnlyList<string> Stdout => _stdout;

    public static string FindFixtureExe(string baseDirectory)
    {
        var output = new DirectoryInfo(baseDirectory);
        var root = output;
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "OmniWin.sln"))) root = root.Parent;
        if (root is null) throw new InvalidOperationException("Could not find OmniWin.sln above " + baseDirectory);
        return Path.Combine(root.FullName, "testapps", "OmniWin.FixtureApp", "bin", output.Parent!.Name, output.Name, "OmniWin.FixtureApp.exe");
    }

    /// <summary>Starts a scenario and waits until its window is shown (OMNIWIN_FIXTURE_READY).</summary>
    public static async Task<DesktopFixture> StartAsync(string scenario, string? extraArgs = null, bool takeUiLock = true)
    {
        // Defence in depth: a test that slipped past its Skip attribute must still never hang on a session with no desktop.
        if (Live.WhyNot is { } whyNot) Xunit.Assert.Skip(whyNot);

        var exe = FindFixtureExe(AppContext.BaseDirectory);
        if (!File.Exists(exe)) throw new FileNotFoundException("Build testapps/OmniWin.FixtureApp first (same configuration).", exe);

        UiLockHolder? uiLock = null;
        if (takeUiLock)
        {
            uiLock = await UiLockHolder.AcquireAsync(UiLockName, TimeSpan.FromMinutes(2));
        }

        var title = $"OmniWin fixture {scenario} {Guid.NewGuid().ToString("N")[..6]}";
        var logPath = Path.Combine(Path.GetTempPath(), "OmniWin-test-fixture", $"{scenario}-{Guid.NewGuid().ToString("N")[..8]}.jsonl");
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "--scenario", scenario, "--title", title, "--log", logPath, "--stdin-commands" }) info.ArgumentList.Add(a);
        if (extraArgs is not null)
        {
            foreach (var a in extraArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries)) info.ArgumentList.Add(a);
        }

        var process = Process.Start(info) ?? throw new InvalidOperationException("The fixture app did not start.");
        var fixture = new DesktopFixture(process, uiLock, title, logPath, scenario);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(timeout.Token) ?? throw new InvalidOperationException("The fixture app exited before it was ready.");
            fixture._stdout.Add(line);
            if (line == "OMNIWIN_FIXTURE_READY") break;
        }

        // The log line follows READY; later output (rects) is read on demand by ReadStdoutLineAsync.
        fixture._stdout.Add(await process.StandardOutput.ReadLineAsync(timeout.Token) ?? "");
        return fixture;
    }

    /// <summary>Sends one command line (see docs/status/WS4.md, "Fixture commands").</summary>
    public async Task CommandAsync(string command)
    {
        await _process.StandardInput.WriteLineAsync(command);
        await _process.StandardInput.FlushAsync();
        await Task.Delay(150);   // the command runs on the UI thread; its effect is visible in the log right after
    }

    /// <summary>The next stdout line, for example the OMNIWIN_FIXTURE_RECT line of F05.</summary>
    public async Task<string> ReadStdoutLineAsync(string prefix)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var line = await _process.StandardOutput.ReadLineAsync(timeout.Token) ?? throw new InvalidOperationException("The fixture app closed its output.");
            _stdout.Add(line);
            if (line.StartsWith(prefix, StringComparison.Ordinal)) return line;
        }
    }

    /// <summary>Every event the scenario logged so far.</summary>
    public IReadOnlyList<JsonElement> Events()
    {
        var list = new List<JsonElement>();
        if (!File.Exists(LogPath)) return list;
        using var stream = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            using var doc = JsonDocument.Parse(line);
            list.Add(doc.RootElement.Clone());
        }

        return list;
    }

    /// <summary>How many times <paramref name="ev"/> was logged for <paramref name="control"/> (null control = any).</summary>
    public int Count(string? control, string ev) => Events().Count(e =>
        e.GetProperty("event").GetString() == ev &&
        (control is null || (e.TryGetProperty("control", out var c) && c.GetString() == control)));

    /// <summary>Count of real input events of any control (click, typed text, selection, check, scroll).</summary>
    public int InputEventCount() => Events().Count(e => e.GetProperty("event").GetString() is
        "click" or "text_changed" or "typed" or "selected" or "checked" or "value" or "expanded" or "collapsed");

    public string? LastValue(string control, string ev) => Events()
        .LastOrDefault(e => e.GetProperty("event").GetString() == ev && e.TryGetProperty("control", out var c) && c.GetString() == control)
        .ValueKindOrNull("value");

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                // Only the process this object started.
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }
        }
        finally
        {
            _process.Dispose();
            _uiLock?.Dispose();
        }
    }
}

// UiLockHolder (the shared UI test lock held on one dedicated thread) lives in tests/Shared/UiLockHolder.cs, linked here.

internal static class JsonElementExtensions
{
    internal static string? ValueKindOrNull(this JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>desktop_observe / desktop_act helpers over the MCP session (element refs are eN, OCR words oN).</summary>
public static class DesktopSteps
{
    public static async Task<string> ObserveWindow(this E2ESession s, DesktopFixture fixture, object? extra = null)
    {
        var args = new Dictionary<string, object?> { ["window"] = fixture.Title };
        if (extra is not null)
        {
            foreach (var p in extra.GetType().GetProperties()) args[p.Name] = p.GetValue(extra);
        }

        return (await s.Tool("desktop_observe", args)).Text;
    }

    public static string DesktopRef(string observation, string contains)
    {
        var line = observation.Split('\n').FirstOrDefault(l => l.Contains(contains, StringComparison.Ordinal) && l.TrimStart().Length > 1 && l.TrimStart()[0] is 'e' or 'o' && char.IsDigit(l.TrimStart()[1]))
            ?? throw new InvalidOperationException($"No element line containing '{contains}' in:\n{observation}");
        return line.Trim().Split(' ')[0];
    }

    public static Task<ToolResult> DesktopAct(this E2ESession s, string observation, string element, string action, string? value = null, object? expect = null)
    {
        var args = new Dictionary<string, object?>
        {
            ["ref"] = DesktopRef(observation, element),
            ["gen"] = E2ESession.GenOf(observation),
            ["action"] = action,
        };
        if (value is not null) args["value"] = value;
        if (expect is not null) args["expect"] = expect;
        return s.Tool("desktop_act", args);
    }
}
