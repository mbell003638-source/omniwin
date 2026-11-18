using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OmniWin.FixtureApp.Scenarios;

/// <summary>
/// Command line of the fixture app. With no <c>--scenario</c> the app is the original single window (unchanged), so existing
/// callers that pass only <c>--title</c> keep working.
/// <code>
/// --scenario F01..F17 | F05-cover   which fixture to show
/// --title TEXT                       window title (default "OmniWin fixture Fnn")
/// --log PATH                         JSONL ground-truth log (default %TEMP%\OmniWin-test-fixture\Fnn-pid.jsonl)
/// --rect L,T,W,H                     F05-cover: where the cover window goes (screen pixels)
/// --stdin-commands                   read one command per line from stdin (see docs/status/WS4.md)
/// --steal-after-ms N                 F14: start taking the foreground after N ms
/// </code>
/// </summary>
internal sealed record ScenarioOptions(string? Scenario, string? Title, string? LogPath, string? Rect, bool StdinCommands, int StealAfterMs)
{
    internal static ScenarioOptions Parse(string[] args)
    {
        string? scenario = null, title = null, log = null, rect = null;
        var stdin = false;
        var steal = -1;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--scenario" when i + 1 < args.Length: scenario = args[++i].ToUpperInvariant(); break;
                case "--title" when i + 1 < args.Length: title = args[++i]; break;
                case "--log" when i + 1 < args.Length: log = args[++i]; break;
                case "--rect" when i + 1 < args.Length: rect = args[++i]; break;
                case "--steal-after-ms" when i + 1 < args.Length && int.TryParse(args[i + 1], out var ms): steal = ms; i++; break;
                case "--stdin-commands": stdin = true; break;
            }
        }

        return new ScenarioOptions(scenario, string.IsNullOrWhiteSpace(title) ? null : title, log, rect, stdin, steal);
    }
}

/// <summary>
/// Ground truth that does not come from OmniWin: one JSON object per line for every input the fixture actually receives.
/// Typed text is logged in full except in password scenarios, which log only its length and a short hash.
/// </summary>
internal sealed class ScenarioLog : IDisposable
{
    private static readonly JsonSerializerOptions Options = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly object _gate = new();
    private readonly StreamWriter _writer;
    private readonly string _scenario;

    private ScenarioLog(string scenario, string path)
    {
        _scenario = scenario;
        Path = path;
        _writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false))
        {
            AutoFlush = true,
        };
    }

    internal string Path { get; }

    internal static ScenarioLog Create(string scenario, string? path)
    {
        path ??= System.IO.Path.Combine(System.IO.Path.GetTempPath(), "OmniWin-test-fixture", $"{scenario}-{Environment.ProcessId}.jsonl");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        return new ScenarioLog(scenario, path);
    }

    /// <summary>Appends one event. <paramref name="control"/> is the control's Name / AutomationId (null for window-level events).</summary>
    internal void Write(string? control, string ev, string? value = null, object? detail = null)
    {
        var line = JsonSerializer.Serialize(new
        {
            t = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            scenario = _scenario,
            control,
            @event = ev,
            value,
            detail,
        }, Options);
        lock (_gate)
        {
            _writer.WriteLine(line);
        }
    }

    /// <summary>"len=18 sha=ab12cd34": enough to prove what reached a password box without writing the secret anywhere.</summary>
    internal static string Fingerprint(string secret)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
        return $"len={secret.Length} sha={hash[..8].ToLowerInvariant()}";
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer.Dispose();
        }
    }
}

/// <summary>Reads one command per line from stdin so a test can trigger a fixture behaviour without sending any UI input.</summary>
internal static class StdinCommands
{
    internal static void Start(Action<string> dispatch)
    {
        var thread = new Thread(() =>
        {
            string? line;
            while ((line = Console.In.ReadLine()) is not null)
            {
                var command = line.Trim();
                if (command.Length > 0) dispatch(command);
            }
        })
        { IsBackground = true, Name = "fixture-stdin-commands" };
        thread.Start();
    }
}
