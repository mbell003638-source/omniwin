using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OmniWin.Core;

namespace OmniWin.Server.Hosting;

internal static class ToolCallAudit
{
    internal static McpRequestFilter<CallToolRequestParams, CallToolResult> Filter => next => async (request, ct) =>
    {
        var start = Stopwatch.GetTimestamp();
        CallToolResult? result = null;
        try
        {
            result = await next(request, ct).ConfigureAwait(false);
            return result;
        }
        finally
        {
            var text = result?.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text;
            var firstLine = text?.Split('\n', 2)[0];
            (request.Services ?? request.Server.Services)?.GetService<IAuditLog>()?.Record(BuildEntry(DateTimeOffset.UtcNow,
                request.Params?.Name ?? "unknown", Redact(request.Params?.Arguments), firstLine,
                result is null || result.IsError == true, Stopwatch.GetElapsedTime(start).TotalMilliseconds));
        }
    };

    /// <summary>
    /// Builds the audit line from a tool result's first line. The Q6 Risky label is read from the <c>risk=</c> token and
    /// recorded as "risky"; any other value, or no token, leaves <see cref="AuditEntry.Risk"/> null. It is a label only.
    /// </summary>
    internal static AuditEntry BuildEntry(
        DateTimeOffset at, string tool, string argsRedactedJson, string? firstLine, bool isError, double totalMs)
    {
        var status = Token(firstLine, "status=") ?? (isError ? "error" : "ok");
        var risk = Token(firstLine, "risk=") == "risky" ? "risky" : null;
        return new AuditEntry(at, tool, argsRedactedJson, status,
            Token(firstLine, "input_sent=") == "true", totalMs,
            Token(firstLine, "reason="), Token(firstLine, "step="), risk);
    }

    internal static string Redact(IDictionary<string, JsonElement>? args)
    {
        var redacted = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (args is not null)
        {
            foreach (var (key, value) in args)
            {
                if (key is "gen" or "since_gen" or "max_elements" or "include_text" or "viewport_only" or "full_page" or "max_side" or "amount" or
                    "volume" or "mute" or "x" or "y" or "width" or "height")
                {
                    redacted[key] = value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null
                        ? (object)value : "<invalid>";
                }
                else if (key is "ref" or "window" or "tab" && value.ValueKind == JsonValueKind.String &&
                    value.GetString() is { Length: > 1 } reference && reference[0] is 'w' or 'e' or 'o' or 'b' or 't' &&
                    reference.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0)
                {
                    redacted[key] = reference;
                }
                else if (key == "url" && value.ValueKind == JsonValueKind.String)
                {
                    // Browser URLs can carry tokens in the path, query or fragment: log the scheme and host only.
                    redacted[key] = SchemeAndHost(value.GetString()!);
                }
                else if (key == "app" && value.ValueKind == JsonValueKind.String)
                {
                    // F7: which app was launched is plain metadata for the owner (a Start-menu name or id); typed text is not.
                    redacted[key] = OneLineCapped(value.GetString()!, 100);
                }
                else if (value.ValueKind == JsonValueKind.String && SafeMetadata(key, value.GetString()!))
                {
                    redacted[key] = value.GetString();
                }
                else if (key is "ref" or "window" or "tab" or "action" or "scope" or "direction" or "kind" or "value" or "keys")
                {
                    redacted[key] = value.ValueKind == JsonValueKind.String ? $"<{value.GetString()!.Length} chars>" : "<redacted>";
                }
                else if (key == "expect")
                {
                    redacted[key] = "<expectation>";
                }
            }
        }

        return JsonSerializer.Serialize(redacted, CoreJson.Options);
    }

    /// <summary>"https://example.com/a?token=1#x" becomes "https://example.com"; anything that is not an absolute URL becomes "&lt;invalid url&gt;".</summary>
    internal static string SchemeAndHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? $"{uri.Scheme}://{uri.Host}"
            : Uri.TryCreate(url, UriKind.Absolute, out var other) ? $"{other.Scheme}:" : "<invalid url>";

    private static string OneLineCapped(string text, int max)
    {
        var chars = text.Length > max ? text[..max] : text;
        return new string(chars.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
    }

    private static bool SafeMetadata(string key, string value) => key switch
    {
        "action" => value is "click" or "double_click" or "right_click" or "invoke" or "toggle" or "select" or
            "expand" or "collapse" or "set_value" or "type" or "clear" or "focus" or "scroll_into_view" or
            "hover" or "drag" or "check" or "uncheck" or "press" or
            "navigate" or "back" or "forward" or "reload" or "new_tab" or "close_tab" or "switch_tab" or
            "dialog_dismiss" or "dialog_accept" or
            "minimize" or "maximize" or "restore" or "move" or "resize" or "snap_left" or "snap_right" or "close" or
            "play_pause" or "play" or "pause" or "next" or "previous" or "stop" or
            "disable" or "enable" or "right" or "wrong_pick",
        "scope" => value is "window" or "focused" or "dialog",
        // engine_call: which engine and which of its tools (the engine's own names; arguments are never logged).
        "engine" => value is "windows-mcp" or "agent-desktop",
        "tool" => value.Length is >= 1 and <= 40 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'),
        "topic" => value is "start" or "desktop" or "browser" or "system" or "workflows" or "recovery" or "engines" or "router" or "recipes" or "selection" or "power" or "tools",
        // screen_view: "screen" names the whole screen; it is metadata, not content.
        "window" => value == "screen",
        "direction" => value is "up" or "down" or "left" or "right",
        "kind" => value is "network" or "battery" or "displays" or "time",
        _ => false,
    };

    private static string? Token(string? line, string prefix)
    {
        foreach (var token in line?.Split(' ') ?? [])
        {
            if (token.StartsWith(prefix, StringComparison.Ordinal)) return token[prefix.Length..];
        }

        return null;
    }
}
