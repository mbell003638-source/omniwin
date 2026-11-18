using System.Text.Json;
using System.Text.RegularExpressions;
using OmniWin.Core;

namespace OmniWin.Workflows;

/// <summary>
/// One concrete tool call, as the model sends it in <c>run_steps</c> (JSON, snake_case, parameter names exactly as the
/// individual tool): <c>{"tool":"desktop_act","ref":"e3","gen":5,"action":"click"}</c>. Only <see cref="Tool"/> is required.
/// </summary>
public sealed record StepCall(
    string Tool,
    string? Ref = null,
    long? Gen = null,
    string? Action = null,
    string? Value = null,          // desktop_act / browser_act value
    ExpectSpec? Expect = null,
    string? Keys = null,           // desktop_keys
    string? Window = null,         // desktop_keys / desktop_scroll / window_manage
    string? Direction = null,      // desktop_scroll
    int? Amount = null,            // desktop_scroll
    string? Url = null,            // browser_nav
    string? Tab = null,            // browser_nav
    string? App = null,            // app_launch
    string? Args = null,           // app_launch
    int? X = null, int? Y = null, int? Width = null, int? Height = null,   // window_manage
    int? Volume = null, bool? Mute = null,                                // audio
    string? Text = null);          // clipboard_set

/// <summary>The tools a step may use, and how each maps onto an engine. Everything else is refused: steps are declarative data, never code.</summary>
public static partial class StepTools
{
    public const string Desktop = "desktop";
    public const string Browser = "browser";
    public const string SystemEngine = "system";

    private static readonly Dictionary<string, string> EngineByTool = new(StringComparer.Ordinal)
    {
        ["desktop_act"] = Desktop,
        ["desktop_keys"] = Desktop,
        ["desktop_scroll"] = Desktop,
        ["browser_act"] = Browser,
        ["browser_nav"] = Browser,
        ["app_launch"] = SystemEngine,
        ["window_manage"] = SystemEngine,
        ["audio"] = SystemEngine,
        ["media"] = SystemEngine,
        ["clipboard_set"] = SystemEngine,
    };

    public static IReadOnlyCollection<string> Names => EngineByTool.Keys;

    public static bool IsKnown(string? tool) => tool is not null && EngineByTool.ContainsKey(tool);

    public static string? EngineOf(string? tool) => tool is not null && EngineByTool.TryGetValue(tool, out var e) ? e : null;

    /// <summary>Tools whose target is a ref from an observation (the selector comes from that element's descriptor).</summary>
    public static bool IsRefBased(string tool) => tool is "desktop_act" or "browser_act";

    /// <summary>Tools that may take an optional ref (scroll).</summary>
    public static bool MayHaveRef(string tool) => IsRefBased(tool) || tool == "desktop_scroll";

    /// <summary>A "wN" window id. These only exist for one server session, so a run step keeps the window's process name instead.</summary>
    public static bool IsWindowId(string? window) => window is not null && WindowIdPattern().IsMatch(window);

    [GeneratedRegex("^w[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex WindowIdPattern();

    /// <summary>
    /// Settings for step and workflow files. Same as <see cref="CoreJson.Options"/> but dictionary keys keep their spelling
    /// (a parameter or argument name is never rewritten) and null values are written.
    /// </summary>
    public static JsonSerializerOptions JsonOptions { get; } = new(CoreJson.Indented)
    {
        DictionaryKeyPolicy = null,
        // Write nulls too: Core's records have required constructor parameters (SelectorSegment), so a file that left them out would not load.
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    /// <summary>The tool's literal, non-text arguments, as stored in the run log and in saved workflows.</summary>
    public static (string Action, Dictionary<string, string> Args, bool HasValue) Describe(StepCall call, string? windowProcess)
    {
        ArgumentNullException.ThrowIfNull(call);
        var args = new Dictionary<string, string>(StringComparer.Ordinal);
        var inv = System.Globalization.CultureInfo.InvariantCulture;

        void Put(string key, string? value)
        {
            if (!string.IsNullOrEmpty(value)) args[key] = value;
        }

        switch (call.Tool)
        {
            case "desktop_act":
            case "browser_act":
                return (call.Action ?? string.Empty, args, !string.IsNullOrEmpty(call.Value));

            case "desktop_keys":
                Put("keys", call.Keys);
                Put("window", windowProcess);
                return ("keys", args, false);

            case "desktop_scroll":
                Put("direction", call.Direction);
                if (call.Amount is { } amount) args["amount"] = amount.ToString(inv);
                Put("window", windowProcess);
                return ("scroll", args, false);

            case "browser_nav":
                Put("url", call.Url);
                return (call.Action ?? string.Empty, args, false);

            case "app_launch":
                Put("app", call.App);
                return ("launch", args, !string.IsNullOrEmpty(call.Args));

            case "window_manage":
                Put("window", windowProcess);
                if (call.X is { } x) args["x"] = x.ToString(inv);
                if (call.Y is { } y) args["y"] = y.ToString(inv);
                if (call.Width is { } w) args["width"] = w.ToString(inv);
                if (call.Height is { } h) args["height"] = h.ToString(inv);
                return (call.Action ?? string.Empty, args, false);

            case "audio":
                if (call.Volume is { } v) args["volume"] = v.ToString(inv);
                if (call.Mute is { } m) args["mute"] = m ? "true" : "false";
                return ("set", args, false);

            case "media":
                return (call.Action ?? string.Empty, args, false);

            case "clipboard_set":
                return ("set", args, !string.IsNullOrEmpty(call.Text));

            default:
                return (call.Action ?? string.Empty, args, false);
        }
    }

    /// <summary>The inverse of <see cref="Describe"/>: a concrete call from stored literals. <paramref name="value"/> is the text for the step's placeholder.</summary>
    public static StepCall ToCall(string tool, string action, IReadOnlyDictionary<string, string> args, string? value)
    {
        ArgumentNullException.ThrowIfNull(args);
        var inv = System.Globalization.CultureInfo.InvariantCulture;

        string? S(string key) => args.TryGetValue(key, out var v) ? v : null;
        int? I(string key) => args.TryGetValue(key, out var v) && int.TryParse(v, System.Globalization.NumberStyles.Integer, inv, out var n) ? n : null;

        return tool switch
        {
            "desktop_act" or "browser_act" => new StepCall(tool, Action: action, Value: value),
            "desktop_keys" => new StepCall(tool, Keys: S("keys"), Window: S("window")),
            "desktop_scroll" => new StepCall(tool, Direction: S("direction"), Amount: I("amount"), Window: S("window")),
            "browser_nav" => new StepCall(tool, Action: action, Url: S("url")),
            "app_launch" => new StepCall(tool, App: S("app"), Args: value),
            "window_manage" => new StepCall(tool, Action: action, Window: S("window"), X: I("x"), Y: I("y"), Width: I("width"), Height: I("height")),
            "audio" => new StepCall(tool, Volume: I("volume"), Mute: S("mute") is { } m ? m == "true" : null),
            "media" => new StepCall(tool, Action: action),
            "clipboard_set" => new StepCall(tool, Text: value),
            _ => new StepCall(tool, Action: action),
        };
    }
}
