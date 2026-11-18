using OmniWin.Core;

namespace OmniWin.Browser.Tests;

public sealed class TestKillSwitch : IKillSwitch
{
    private readonly CancellationTokenSource _cts = new();

    public bool IsTripped { get; set; }
    public Func<bool>? IsTrippedCustom { get; set; }

    bool IKillSwitch.IsTripped => IsTrippedCustom != null ? IsTrippedCustom() : IsTripped;

    public CancellationToken Token => _cts.Token;

    public void Trip()
    {
        IsTripped = true;
        _cts.Cancel();
    }
}

public sealed class TestUserActivity : IUserActivity
{
    public bool UserActiveRecently { get; set; }

    public DateTimeOffset LastRealInput { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class TestMetrics : IMetrics
{
    private readonly List<(string Op, double Ms)> _records = [];

    public IReadOnlyList<(string Op, double Ms)> Records => _records;

    public void Record(string op, double ms)
    {
        lock (_records)
        {
            _records.Add((op, ms));
        }
    }

    public MetricsSnapshot Snapshot()
    {
        var dict = new Dictionary<string, (double P50, double P95, double P99, long Count)>();
        lock (_records)
        {
            var groups = _records.GroupBy(r => r.Op);
            foreach (var g in groups)
            {
                var sorted = g.Select(x => x.Ms).OrderBy(x => x).ToList();
                var p50 = sorted[(int)(sorted.Count * 0.5)];
                var p95 = sorted[(int)(sorted.Count * 0.95)];
                var p99 = sorted[(int)(sorted.Count * 0.99)];
                dict[g.Key] = (p50, p95, p99, sorted.Count);
            }
        }
        return new MetricsSnapshot(dict);
    }
}

/// <summary>
/// Fail-closed guard for every test that drives a real (headless) browser. It is installed as the CDP
/// <c>CommandInterceptor</c> and throws before a <c>Page.navigate</c> or <c>Target.createTarget</c> command is sent
/// for any URL that is not local (http://127.0.0.1, http://localhost), about:blank or data:. Nothing in the engine refuses
/// a URL for its scheme (owner decision), so if a test ever tries one, the test fails instead of opening a file:,
/// javascript:, ms-settings: or search-ms: URL. Tests install it through <c>BrowserEngine.ConnectionCreated</c>, so every
/// connection, including one made by a reconnect, has it before its first command.
/// </summary>
public static class NavigationGuard
{
    private static readonly string[] AllowedPrefixes = ["http://127.0.0.1", "http://localhost"];

    public static bool IsAllowedUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return false;
        if (url.Equals("about:blank", StringComparison.OrdinalIgnoreCase)) return true;
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return true;

        foreach (var prefix in AllowedPrefixes)
        {
            if (url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                (url.Length == prefix.Length || url[prefix.Length] is ':' or '/' or '?' or '#'))
            {
                return true;
            }
        }

        return false;
    }

    public static Task Check(string method, object? parameters)
    {
        if (method is "Page.navigate" or "Target.createTarget")
        {
            using var doc = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(parameters));
            var url = doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                      doc.RootElement.TryGetProperty("url", out var u) && u.ValueKind == System.Text.Json.JsonValueKind.String
                ? u.GetString()
                : null;
            if (!IsAllowedUrl(url))
            {
                throw new InvalidOperationException($"NavigationGuard: refusing {method} to '{url}' in a test run");
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>A test interceptor that keeps the guard and then runs <paramref name="extra"/>.</summary>
    public static Func<string, object?, Task> With(Func<string, object?, Task> extra) =>
        async (method, parameters) =>
        {
            await Check(method, parameters).ConfigureAwait(false);
            await extra(method, parameters).ConfigureAwait(false);
        };
}
