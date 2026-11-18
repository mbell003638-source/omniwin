using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OmniWin.Core;

namespace OmniWin.Safety.Tests;

public sealed class AuditLogTests : IDisposable
{
    private readonly string _root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "omniwin-safety-tests"));
    private readonly string _dir;

    public AuditLogTests() => _dir = Path.Combine(_root, Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Shutdown_drains_entries_and_can_be_called_twice()
    {
        var paths = OmniWinPaths.FromOverride(_dir);
        var audit = new AuditLog(paths, NullLogger<AuditLog>.Instance);
        var at = DateTimeOffset.UtcNow;
        for (var i = 0; i < 20; i++)
        {
            audit.Record(new AuditEntry(at, "desktop_act", """{"value":"<12 chars>"}""", "denied", false, 1.5,
                ReasonCodes.PolicyForbidden, null));
        }

        await audit.DisposeAsync();
        await audit.DisposeAsync();
        var file = Directory.GetFiles(paths.LogsDir, "audit-*.jsonl").Single();
        var lines = await File.ReadAllLinesAsync(file, TestContext.Current.CancellationToken);
        Assert.Equal(20, lines.Length);
        foreach (var line in lines)
        {
            using var json = JsonDocument.Parse(line);
            Assert.Equal("desktop_act", json.RootElement.GetProperty("tool").GetString());
            Assert.False(json.RootElement.GetProperty("input_sent").GetBoolean());
            Assert.Equal("""{"value":"<12 chars>"}""", json.RootElement.GetProperty("args_redacted_json").GetString());
        }
    }

    [Fact]
    public async Task Entries_are_filed_by_their_action_date()
    {
        var paths = OmniWinPaths.FromOverride(_dir);
        var audit = new AuditLog(paths, NullLogger<AuditLog>.Instance);
        var first = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
        var second = first.AddDays(2);
        audit.Record(new AuditEntry(first, "windows_list", "{}", "ok", false, 1, null, null));
        audit.Record(new AuditEntry(second, "windows_list", "{}", "ok", false, 1, null, null));
        await audit.DisposeAsync();
        Assert.True(File.Exists(Path.Combine(paths.LogsDir, $"audit-{first.LocalDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.jsonl")));
        Assert.True(File.Exists(Path.Combine(paths.LogsDir, $"audit-{second.LocalDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.jsonl")));
    }

    [Fact]
    public async Task Disk_failure_is_reported_and_shutdown_still_completes()
    {
        var paths = OmniWinPaths.FromOverride(_dir);
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(paths.LogsDir, "occupied", TestContext.Current.CancellationToken);
        var audit = new AuditLog(paths, NullLogger<AuditLog>.Instance);
        audit.Record(new AuditEntry(DateTimeOffset.UtcNow, "windows_list", "{}", "ok", false, 1, null, null));
        await audit.DisposeAsync();
        Assert.Equal("failed", audit.State);
        audit.Record(new AuditEntry(DateTimeOffset.UtcNow, "windows_list", "{}", "ok", false, 1, null, null));
    }

    public void Dispose()
    {
        var path = Path.GetFullPath(_dir);
        Assert.StartsWith(_root + Path.DirectorySeparatorChar, path, StringComparison.OrdinalIgnoreCase);
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    [Fact]
    public async Task Risk_label_is_written_when_set_and_left_out_when_null()
    {
        var paths = OmniWinPaths.FromOverride(_dir);
        var audit = new AuditLog(paths, NullLogger<AuditLog>.Instance);
        var at = DateTimeOffset.UtcNow;
        audit.Record(new AuditEntry(at, "browser_act", "{}", "unverified", true, 1, null, null, "risky"));
        audit.Record(new AuditEntry(at, "desktop_act", "{}", "confirmed", true, 1, null, null));

        await audit.DisposeAsync();
        var file = Directory.GetFiles(paths.LogsDir, "audit-*.jsonl").Single();
        var lines = await File.ReadAllLinesAsync(file, TestContext.Current.CancellationToken);

        using var risky = JsonDocument.Parse(lines[0]);
        using var plain = JsonDocument.Parse(lines[1]);
        Assert.Equal("risky", risky.RootElement.GetProperty("risk").GetString());
        Assert.False(plain.RootElement.TryGetProperty("risk", out _));
    }
}
