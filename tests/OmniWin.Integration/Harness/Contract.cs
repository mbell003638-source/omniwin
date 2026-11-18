namespace OmniWin.Integration.Harness;

/// <summary>Contract §8: the tool surface, written out by hand so the tests compare the server against the document, not against itself.</summary>
internal static class Contract
{
    public static readonly IReadOnlyDictionary<string, string[]> Tools = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["omniwin_status"] = [],
        ["windows_list"] = [],
        ["desktop_observe"] = ["window", "scope", "max_elements", "since_gen", "include_text"],
        ["desktop_act"] = ["ref", "gen", "action", "value", "expect"],
        ["desktop_keys"] = ["keys", "window", "expect"],
        ["desktop_scroll"] = ["direction", "amount", "ref", "window"],
        ["wait_for"] = ["condition", "timeout_ms"],
        ["browser_tabs"] = [],
        ["browser_nav"] = ["action", "url", "tab", "expect"],
        ["browser_observe"] = ["tab", "max_elements", "since_gen", "include_text", "viewport_only"],
        ["browser_act"] = ["ref", "gen", "action", "value", "expect"],
        ["apps_list"] = ["filter"],
        ["app_launch"] = ["app", "args", "expect"],
        ["window_manage"] = ["window", "action", "x", "y", "width", "height"],
        ["audio"] = ["volume", "mute"],
        ["media"] = ["action"],
        ["system_info"] = ["kind"],
        ["clipboard_get"] = [],
        ["clipboard_set"] = ["text"],
        ["screen_ocr"] = ["window", "region"],
        ["run_steps"] = ["steps", "stop_on_failure"],
        ["workflow_list"] = [],
        ["workflow_save"] = ["name", "description", "from_step", "to_step", "params"],
        ["workflow_run"] = ["name", "params"],
        // Contract 2.5: always registered.
        ["omniwin_guide"] = ["topic"],
        ["omniwin_do"] = ["text"],
        // Contract 2.6: learning and selected-text tools, always registered.
        ["task_done"] = ["success", "goal", "template", "params"],
        ["workflow_manage"] = ["name", "action"],
        ["selection_get"] = [],
        ["selection_replace"] = ["sel", "text"],
        ["polish_prompts"] = [],
        ["text_insert"] = ["text"],
        ["engines_list"] = ["engine", "tools"],
    };

    /// <summary>
    /// Contract 2.5: tools that exist only when switched on or when an engine is installed: engine_call (engines found under the tools
    /// folder) and the power tools (power_tools.enabled, off by default). A default server may or may not have engine_call.
    /// </summary>
    public static readonly IReadOnlySet<string> Optional =
        new HashSet<string>(["engine_call", "shell", "files", "registry", "process"], StringComparer.Ordinal);

    /// <summary>Look-only tools the owner added on 2026-11-08 (add-on 16). They may exist; if they do they can take no click, key or coordinate.</summary>
    public static readonly IReadOnlySet<string> LookOnly = new HashSet<string>(["screen_view", "browser_view"], StringComparer.Ordinal);

    /// <summary>Tools whose descriptions must carry the §10.3 retry wording.</summary>
    public static readonly string[] ActTools = ["desktop_act", "desktop_keys", "desktop_scroll", "browser_nav", "browser_act"];

    /// <summary>Opt-in switch for everything that sends real input, shows a window or changes the foreground (Phase 5, owner present).</summary>
    public static bool LiveSession => Environment.GetEnvironmentVariable("OMNIWIN_LIVE_SESSION") == "1" && DesktopAvailability.IsAvailable();

    public const string LiveSkip =
        "Real input / windows / foreground: runs only in the Phase 5 live session with the owner present (set OMNIWIN_LIVE_SESSION=1, hold Local\\OmniWin.UiInputTestLock).";
}
