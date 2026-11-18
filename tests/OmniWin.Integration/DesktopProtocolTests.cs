using System.Diagnostics;
using System.Text.Json;
using OmniWin.Core;

namespace OmniWin.Integration;

public sealed class DesktopProtocolTests
{
    [Fact]
    public async Task Desktop_tools_work_over_stdio_and_audit_redacts_request_text()
    {
        var data = Path.Combine(Path.GetTempPath(), "omniwin-protocol-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(data);
        await File.WriteAllTextAsync(Path.Combine(data, "config.json"),
            """{"allow_elevated":true,"user_activity_cooldown_ms":0}""", TestContext.Current.CancellationToken);
        try
        {
            await using (var server = new Server(data))
            {
                var initialize = await server.CallAsync("initialize", new
                {
                    protocolVersion = "2025-06-18",
                    capabilities = new { },
                    clientInfo = new { name = "omniwin-protocol-tests", version = "1" },
                });
                Assert.Equal("omniwin", initialize.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
                await server.NotifyInitializedAsync();

                var listing = await server.CallAsync("tools/list", new { });
                var tools = listing.GetProperty("result").GetProperty("tools").EnumerateArray()
                    .ToDictionary(tool => tool.GetProperty("name").GetString()!, StringComparer.Ordinal);
                // The whole surface is checked against contract §8 in SurfaceTests; here the desktop tools must be present.
                Assert.All(new[] { "desktop_act", "desktop_keys", "desktop_observe", "desktop_scroll", "omniwin_status", "windows_list" },
                    name => Assert.Contains(name, tools.Keys));
                CheckParameters(tools["desktop_act"], ["ref", "gen", "action", "value", "expect"]);
                CheckParameters(tools["desktop_observe"], ["window", "scope", "max_elements", "since_gen", "include_text"]);
                CheckParameters(tools["desktop_keys"], ["keys", "window", "expect"]);
                CheckParameters(tools["desktop_scroll"], ["direction", "amount", "ref", "window"]);
                Assert.Contains("value_equals", tools["desktop_act"].GetRawText(), StringComparison.Ordinal);

                var status = await server.ToolAsync("omniwin_status", new { });
                Assert.Contains("safety_gate=no_delete", Text(status), StringComparison.Ordinal);
                Assert.Contains("desktop=", Text(status), StringComparison.Ordinal);

                var invalidKeys = await server.ToolAsync("desktop_keys", new { keys = "ctrl++a" });
                Assert.StartsWith("status=blocked input_sent=false reason=unsupported_action", Text(invalidKeys));
                var invalidScroll = await server.ToolAsync("desktop_scroll", new { direction = "down", amount = 0 });
                Assert.StartsWith("status=blocked input_sent=false reason=unsupported_action", Text(invalidScroll));
                var invalidScope = await server.ToolAsync("desktop_observe", new { scope = "invalid" });
                Assert.StartsWith("status=blocked input_sent=false reason=unsupported_action", Text(invalidScope));

                var absent = await server.ToolAsync("desktop_act", new
                {
                    @ref = "e999999999",
                    gen = 1,
                    action = "set_value",
                    value = "OMNIWIN_TYPED_SECRET_MARKER",
                    expect = new
                    {
                        condition = new { kind = "value_equals", @ref = "e999999999", value = "OMNIWIN_EXPECT_SECRET_MARKER" },
                    },
                });
                Assert.Contains("input_sent=false", Text(absent), StringComparison.Ordinal);
                Assert.DoesNotContain("OMNIWIN_TYPED_SECRET_MARKER", Text(absent), StringComparison.Ordinal);

                var windows = await server.ToolAsync("windows_list", new { });
                Assert.True(Text(windows).StartsWith(ResultFormatter.UntrustedHeader, StringComparison.Ordinal) ||
                    Text(windows).StartsWith("status=error input_sent=false", StringComparison.Ordinal));
            }

            var entries = new List<JsonElement>();
            foreach (var path in Directory.GetFiles(Path.Combine(data, "logs"), "audit-*.jsonl"))
            {
                var contents = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
                Assert.DoesNotContain("OMNIWIN_TYPED_SECRET_MARKER", contents, StringComparison.Ordinal);
                Assert.DoesNotContain("OMNIWIN_EXPECT_SECRET_MARKER", contents, StringComparison.Ordinal);
                foreach (var line in contents.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    using var json = JsonDocument.Parse(line);
                    entries.Add(json.RootElement.Clone());
                }
            }

            Assert.Equal(6, entries.Count);
            Assert.All(entries, entry => Assert.False(entry.GetProperty("input_sent").GetBoolean()));
            Assert.Contains(entries, entry => entry.GetProperty("tool").GetString() == "desktop_act");
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    private static void CheckParameters(JsonElement tool, string[] parameters) =>
        Assert.Equal(parameters.Order(StringComparer.Ordinal),
            tool.GetProperty("inputSchema").GetProperty("properties").EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));

    private static string Text(JsonElement response) =>
        response.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;

    private sealed class Server : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly Task<string> _stderr;
        private int _id;

        internal Server(string data)
        {
            var output = new DirectoryInfo(AppContext.BaseDirectory);
            var root = output;
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "OmniWin.sln"))) root = root.Parent;
            Assert.NotNull(root);
            var server = Path.Combine(root.FullName, "src", "OmniWin.Server", "bin", output.Parent!.Name, output.Name, "OmniWin.Server.exe");
            var start = new ProcessStartInfo(server)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add("--data-dir");
            start.ArgumentList.Add(data);
            start.ArgumentList.Add("--test-mode");
            start.ArgumentList.Add("--allow-elevated");
            _process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the test server.");
            _stderr = _process.StandardError.ReadToEndAsync();
        }

        internal Task<JsonElement> ToolAsync(string name, object arguments) => CallAsync("tools/call", new { name, arguments });

        internal async Task<JsonElement> CallAsync(string method, object parameters)
        {
            var id = ++_id;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            // MCP envelope names are camelCase, while tool arguments above use explicit snake_case.
            await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }));
            await _process.StandardInput.FlushAsync(timeout.Token);
            while (true)
            {
                var line = await _process.StandardOutput.ReadLineAsync(timeout.Token);
                Assert.NotNull(line);
                using var json = JsonDocument.Parse(line);
                if (json.RootElement.TryGetProperty("id", out var responseId) && responseId.GetInt32() == id)
                {
                    if (json.RootElement.TryGetProperty("error", out var error))
                    {
                        Assert.Fail(error.GetRawText());
                    }

                    return json.RootElement.Clone();
                }
            }
        }

        internal async Task NotifyInitializedAsync()
        {
            await _process.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
            await _process.StandardInput.FlushAsync(TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            _process.StandardInput.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await _process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync(CancellationToken.None);
                Assert.Fail("The MCP test server did not shut down when stdin closed.");
            }

            var errors = await _stderr;
            Assert.True(_process.ExitCode == 0, errors);
            _process.Dispose();
        }
    }
}
