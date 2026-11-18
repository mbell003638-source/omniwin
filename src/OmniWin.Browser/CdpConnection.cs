using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using OmniWin.Core;

namespace OmniWin.Browser;

public sealed record CdpEvent(string Method, JsonElement Params, string? SessionId);

public sealed class CdpConnection : IAsyncDisposable
{
    private static readonly JsonSerializerOptions CdpJsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ClientWebSocket _ws = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private int _nextId;
    private Task? _readLoop;
    private bool _disposed;

    public event Func<CdpEvent, Task>? EventReceived;
    public event Action? Disconnected;

    public bool IsConnected => _ws.State == WebSocketState.Open && !_disposed;

    /// <summary>Testing hook to intercept or fail commands.</summary>
    internal Func<string, object?, Task>? CommandInterceptor { get; set; }

    public async Task ConnectAsync(Uri webSocketUrl, CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        await _ws.ConnectAsync(webSocketUrl, linked.Token).ConfigureAwait(false);
        _readLoop = Task.Run(ReadLoopAsync);
    }

    public async Task<JsonElement> SendAsync(
        string method,
        object? parameters = null,
        string? sessionId = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (CommandInterceptor is not null)
        {
            await CommandInterceptor(method, parameters).ConfigureAwait(false);
        }

        if (_ws.State != WebSocketState.Open)
        {
            throw new InvalidOperationException($"WebSocket is not open (State: {_ws.State})");
        }

        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var requestObj = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["method"] = method,
        };

        if (parameters is not null)
        {
            requestObj["params"] = parameters;
        }

        if (!string.IsNullOrEmpty(sessionId))
        {
            requestObj["sessionId"] = sessionId;
        }

        var json = JsonSerializer.Serialize(requestObj, CdpJsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);

        using var registration = ct.Register(() =>
        {
            if (_pending.TryRemove(id, out var pendingTcs))
            {
                pendingTcs.TrySetCanceled(ct);
            }
        });

        await _ws.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
        return await tcs.Task.ConfigureAwait(false);
    }

    private async Task ReadLoopAsync()
    {
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        using var ms = new MemoryStream();

        try
        {
            while (!_cts.Token.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                ms.SetLength(0);
                ValueWebSocketReceiveResult result;

                do
                {
                    result = await _ws.ReceiveAsync(buffer.AsMemory(), _cts.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }
                    ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                if (ms.Length == 0) continue;

                ms.Position = 0;
                using var doc = JsonDocument.Parse(ms);
                var root = doc.RootElement.Clone();

                if (root.TryGetProperty("id", out var idProp) && idProp.TryGetInt32(out var id))
                {
                    if (_pending.TryRemove(id, out var tcs))
                    {
                        if (root.TryGetProperty("error", out var errorProp))
                        {
                            var errMsg = errorProp.TryGetProperty("message", out var msgProp) ? msgProp.GetString() : "CDP error";
                            tcs.TrySetException(new InvalidOperationException($"CDP error ({id}): {errMsg}"));
                        }
                        else if (root.TryGetProperty("result", out var resProp))
                        {
                            tcs.TrySetResult(resProp.Clone());
                        }
                        else
                        {
                            tcs.TrySetResult(root);
                        }
                    }
                }
                else if (root.TryGetProperty("method", out var methodProp))
                {
                    var method = methodProp.GetString() ?? string.Empty;
                    var @params = root.TryGetProperty("params", out var pProp) ? pProp.Clone() : default;
                    var sessionId = root.TryGetProperty("sessionId", out var sProp) ? sProp.GetString() : null;

                    if (EventReceived is { } handler)
                    {
                        _ = handler(new CdpEvent(method, @params, sessionId));
                    }
                }
            }
        }
        catch (Exception) when (_cts.IsCancellationRequested || _disposed)
        {
            // Expected during shutdown
        }
        catch (Exception ex)
        {
            FailPending(ex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            FailPending(new InvalidOperationException("WebSocket connection closed"));
            Disconnected?.Invoke();
        }
    }

    private void FailPending(Exception ex)
    {
        foreach (var kvp in _pending)
        {
            if (_pending.TryRemove(kvp.Key, out var tcs))
            {
                tcs.TrySetException(ex);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();

        if (_ws.State == WebSocketState.Open)
        {
            try
            {
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Ignore errors closing WebSocket
            }
        }

        _ws.Dispose();
        _cts.Dispose();

        if (_readLoop is not null)
        {
            try { await _readLoop.ConfigureAwait(false); } catch { /* ignore */ }
        }
    }
}
