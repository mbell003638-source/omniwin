using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OmniWin.Integration.Harness;

/// <summary>
/// One server process, one fixture web server and one throwaway data directory (config, audit logs, workflows and the
/// browser profile all live under it, never under the user's %LOCALAPPDATA%\OmniWin). Shared by the tests of one class.
/// </summary>
public partial class E2ESession : IAsyncLifetime
{
    private string _stderr = "";

    public McpServer Server { get; private set; } = null!;

    public FixtureWebServer Web { get; private set; } = null!;

    public string DataDir { get; private set; } = "";

    /// <summary>Everything the server wrote to stderr; filled once the session is stopped.</summary>
    public string Stderr => _stderr;

    public virtual string? ConfigJson => null;

    public async ValueTask InitializeAsync()
    {
        DataDir = Path.Combine(Path.GetTempPath(), "omniwin-e2e", Guid.NewGuid().ToString("N"));
        Web = new FixtureWebServer(FindFixturesDir());
        Web.Start();
        Server = await McpServer.StartAsync(McpServer.FindServerExe(AppContext.BaseDirectory), DataDir, ConfigJson);
    }

    public async ValueTask DisposeAsync()
    {
        Exception? leak = null;
        try
        {
            _stderr = await Server.StopAsync();
        }
        finally
        {
            await Server.DisposeAsync();
            await Web.DisposeAsync();

            // The leak check. Edge processes whose command line holds THIS session's data folder are the only ones counted or
            // stopped; the owner's own Edge is never touched. A leftover fails the class, after it has been stopped.
            try
            {
                EdgeProcesses.AssertNoneLeft(DataDir, "after the server stopped");
            }
            catch (InvalidOperationException ex)
            {
                leak = ex;
            }

            try
            {
                // Only the folder this session created.
                if (Directory.Exists(DataDir)) Directory.Delete(DataDir, recursive: true);
            }
            catch (IOException)
            {
                // the browser may still be releasing its profile; the temp folder is harmless
            }
        }

        if (leak is not null) throw leak;
    }

    /// <summary>How many Edge processes this session's browser currently has (0 before the first browser call).</summary>
    public int OwnedEdgeCount() => EdgeProcesses.CountOwned(DataDir);

    /// <summary>Runs a test body against its own fresh server, for tests that count audit lines or read files after the server has stopped.</summary>
    public static async Task UsingAsync(Func<E2ESession, Task> body)
    {
        var session = new E2ESession();
        await session.InitializeAsync();
        try
        {
            await body(session);
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    public Task<ToolResult> Tool(string name, object arguments) => Server.ToolAsync(name, arguments);

    /// <summary>Navigates the browser to a fixture page (always a local http://127.0.0.1 URL) and returns the result.</summary>
    public Task<ToolResult> Nav(string page, string query = "") =>
        Tool("browser_nav", new { action = "navigate", url = Web.Url(page) + query });

    public async Task<string> Observe(object? args = null)
    {
        var r = await Tool("browser_observe", args ?? new { });
        return r.Text;
    }

    /// <summary>Navigates to a page and observes it: returns the observation text.</summary>
    public async Task<string> Open(string page, string query = "")
    {
        Web.Clear();
        var nav = await Nav(page, query);
        Xunit.Assert.True(nav.InputSent == true, "navigate: " + nav.FirstLine);
        return await Observe();
    }

    public static string RefOf(string observation, string contains)
    {
        var line = observation.Split('\n').FirstOrDefault(l => l.Contains(contains, StringComparison.Ordinal) && l.TrimStart().StartsWith('b'))
            ?? throw new InvalidOperationException($"No element line containing '{contains}' in:\n{observation}");
        return line.Trim().Split(' ')[0];
    }

    public static long GenOf(string observation)
    {
        var m = GenRegex().Match(observation);
        return m.Success ? long.Parse(m.Groups[1].Value) : throw new InvalidOperationException("No gen= in the observation header:\n" + observation);
    }

    public Task<ToolResult> Act(string observation, string element, string action, string? value = null, object? expect = null)
    {
        var args = new Dictionary<string, object?>
        {
            ["ref"] = RefOf(observation, element),
            ["gen"] = GenOf(observation),
            ["action"] = action,
        };
        if (value is not null) args["value"] = value;
        if (expect is not null) args["expect"] = expect;
        return Tool("browser_act", args);
    }

    /// <summary>The audit entries written so far (waits briefly for the background writer).</summary>
    public async Task<IReadOnlyList<JsonElement>> AuditAsync(int atLeast = 0)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var entries = ReadAudit();
            if (entries.Count >= atLeast) return entries;
            await Task.Delay(100);
        }

        return ReadAudit();
    }

    public List<JsonElement> ReadAudit()
    {
        var list = new List<JsonElement>();
        var logs = Path.Combine(DataDir, "logs");
        if (!Directory.Exists(logs)) return list;
        foreach (var path in Directory.GetFiles(logs, "audit-*.jsonl").Order(StringComparer.Ordinal))
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (line.Length == 0) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    list.Add(doc.RootElement.Clone());
                }
                catch (JsonException)
                {
                    // a line the writer is still flushing
                }
            }
        }

        return list;
    }

    /// <summary>All text OmniWin wrote under its data directory (logs, audit, workflows, config), except the browser's own profile.</summary>
    public string AllWrittenText()
    {
        var sb = new StringBuilder();
        if (!Directory.Exists(DataDir)) return "";
        foreach (var file in Directory.EnumerateFiles(DataDir, "*", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "browser-profile" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                sb.AppendLine(reader.ReadToEnd());
            }
            catch (IOException)
            {
                // being written
            }
        }

        return sb.ToString();
    }

    public static string FindFixturesDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "testapps", "web-fixtures")))
        {
            dir = Directory.GetParent(dir)?.FullName;
        }

        return dir is not null ? Path.Combine(dir, "testapps", "web-fixtures") : throw new DirectoryNotFoundException("testapps/web-fixtures not found");
    }

    [GeneratedRegex(@"\bgen=(\d+)")]
    private static partial Regex GenRegex();
}
