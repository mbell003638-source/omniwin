using System.ComponentModel;
using System.Reflection;
using System.Text;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OmniWin.Core;
using OmniWin.Server.Hosting;

namespace OmniWin.Server.Tools;

/// <summary>
/// <c>omniwin_guide</c>: version-matched usage guides for the model (borrowed from agent-desktop's compiled-in "skills").
/// Every topic starts with the server and contract version it describes, and the <c>tools</c> topic is generated from the
/// tool definitions that are really registered, so the guide cannot describe a different build than the one running.
/// </summary>
[McpServerToolType]
internal static class GuideTools
{
    public const string DefaultTopic = "start";

    public static readonly IReadOnlyList<string> Topics =
        ["start", "desktop", "browser", "system", "workflows", "recovery", "engines", "router", "recipes", "selection", "power", "tools"];

    [McpServerTool(Name = "omniwin_guide", Title = "OmniWin usage guide", ReadOnly = true, Idempotent = true,
                   Destructive = false, OpenWorld = false)]
    [Description("Read a short usage guide for this exact OmniWin version. topic is start (read this first), desktop, browser, " +
                 "system, workflows, recovery (every status and the retry= word), engines, router, recipes (learning: task_done), selection, power, or tools " +
                 "(the registered tools). Read-only; sends no input.")]
    public static string OmniwinGuide(McpServer? server = null, string? topic = null)
    {
        var name = string.IsNullOrWhiteSpace(topic) ? DefaultTopic : topic.Trim().ToLowerInvariant();
        return Render(name, server?.ServerOptions.ToolCollection?.Select(t => t.ProtocolTool).ToList());
    }

    /// <param name="registered">The tools the running server really has (optional ones included). Null: the attributed tool classes.</param>
    internal static string Render(string topic, IReadOnlyList<Tool>? registered = null)
    {
        var header = $"omniwin {ServerState.Version} contract={OmniWinContract.ContractVersion} guide={topic}\n";
        if (topic == "tools") return header + (registered is null ? ToolsListing() : ToolsListing(registered));
        return GuideTopics.Text.TryGetValue(topic, out var text)
            ? header + text
            : header + $"unknown topic. Topics: {string.Join(", ", Topics)}";
    }

    /// <summary>One line per tool the running server has registered: name, arguments, read-only mark, first sentence of its description.</summary>
    internal static string ToolsListing(IReadOnlyList<Tool> registered)
    {
        var sb = new StringBuilder();
        foreach (var tool in registered.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            var required = tool.InputSchema.TryGetProperty("required", out var req) && req.ValueKind == System.Text.Json.JsonValueKind.Array
                ? req.EnumerateArray().Select(e => e.GetString()).ToHashSet()
                : [];
            var parameters = tool.InputSchema.TryGetProperty("properties", out var props) && props.ValueKind == System.Text.Json.JsonValueKind.Object
                ? string.Join(", ", props.EnumerateObject().Select(p => required.Contains(p.Name) ? p.Name : p.Name + "?"))
                : string.Empty;
            var readOnly = tool.Annotations?.ReadOnlyHint == true ? " [read-only]" : string.Empty;
            sb.Append("- ").Append(tool.Name).Append('(').Append(parameters).Append(')').Append(readOnly).Append(": ")
              .Append(FirstSentence(tool.Description ?? string.Empty)).Append('\n');
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>Fallback without a running server (tests): the tool classes that carry the tool-type attribute.</summary>
    internal static string ToolsListing()
    {
        var rows = new List<(string Name, string Line)>();
        foreach (var type in typeof(GuideTools).Assembly.GetTypes())
        {
            if (type.GetCustomAttribute<McpServerToolTypeAttribute>() is null) continue;
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (method.GetCustomAttribute<McpServerToolAttribute>() is not { } tool) continue;
                var description = method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty;
                var first = FirstSentence(description);
                var parameters = string.Join(", ", method.GetParameters()
                    .Where(p => p.ParameterType != typeof(CancellationToken))
                    .Select(p => (p.HasDefaultValue ? p.Name + "?" : p.Name)));
                var name = tool.Name ?? method.Name;
                rows.Add((name, $"- {name}({parameters}){(tool.ReadOnly ? " [read-only]" : string.Empty)}: {first}"));
            }
        }

        var sb = new StringBuilder();
        foreach (var (_, line) in rows.OrderBy(r => r.Name, StringComparer.Ordinal)) sb.Append(line).Append('\n');
        return sb.ToString().TrimEnd();
    }

    private static string FirstSentence(string text)
    {
        var end = text.IndexOf(". ", StringComparison.Ordinal);
        var sentence = end < 0 ? text : text[..(end + 1)];
        return sentence.Length > 220 ? sentence[..219] + "…" : sentence;
    }
}
