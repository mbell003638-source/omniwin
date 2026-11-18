using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using OmniWin.Core;

namespace OmniWin.Safety;

/// <summary>
/// Every tool call, one JSON line, in <c>&lt;data dir&gt;\logs\audit-YYYYMMDD.jsonl</c> (contract §13.4).
/// <see cref="Record"/> only enqueues; a background task writes, so tool calls never wait on the disk.
/// </summary>
public sealed class AuditLog : IAuditLog, IComponentStatus, IAsyncDisposable
{
    private readonly Channel<AuditEntry> _queue = Channel.CreateUnbounded<AuditEntry>(new UnboundedChannelOptions { SingleReader = true });
    private readonly string _dir;
    private readonly ILogger<AuditLog> _log;
    private readonly Task _writer;
    private volatile string _state = "on";

    public AuditLog(OmniWinPaths paths, ILogger<AuditLog> log)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _dir = paths.LogsDir;
        _writer = Task.Run(WriteLoopAsync);
    }

    public string Component => "audit_log";

    public string State => _state;

    public string? Detail => _dir;

    public void Record(AuditEntry e)
    {
        ArgumentNullException.ThrowIfNull(e);
        _queue.Writer.TryWrite(e);
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _writer.ConfigureAwait(false);
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            Directory.CreateDirectory(_dir);
            while (await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (_queue.Reader.TryRead(out var entry))
                {
                    var date = entry.At.LocalDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                    var file = Path.Combine(_dir, $"audit-{date}.jsonl");
                    await using var stream = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    await using var writer = new StreamWriter(stream);
                    await writer.WriteLineAsync(JsonSerializer.Serialize(entry, CoreJson.Options)).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _state = "failed";
            _queue.Writer.TryComplete(ex);
            _log.LogError(ex, "Audit log stopped: could not write to {Dir}", _dir);
        }
    }
}
