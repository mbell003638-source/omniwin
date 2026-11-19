namespace OmniWin.Safety;

// config.json (contract §14). Property names map to snake_case through CoreJson.

public sealed record OmniWinConfig
{
    /// <summary>Owner's choice (2026-11-06): no confirmation prompts; only deleting is blocked (see DeletePolicy).</summary>
    public const string ConfirmModeOff = "off";
    public const string ConfirmModeRisky = "risky";
    public const string ConfirmModeTestAutoAllow = "test_auto_allow";

    public bool AllowElevated { get; init; }
    public string KillHotkey { get; init; } = "ctrl+alt+shift+k";
    /// <summary>
    /// Owner decision 2026-11-08: real mouse or keyboard use never pauses OmniWin, so the default is 0 (pause off).
    /// Set a positive number of milliseconds to make OmniWin wait that long after the last real input.
    /// </summary>
    public int UserActivityCooldownMs { get; init; } = 0;
    /// <summary>
    /// Accepted so existing config files keep loading, but IGNORED: nothing enforces it. The owner's policy is that nothing
    /// is blocked except deleting, so OmniWin never returns <c>denied rate_limit</c> and does not throttle calls (2.4).
    /// </summary>
    public RateLimitConfig RateLimit { get; init; } = new();
    public IReadOnlyList<string> RiskLexiconExtra { get; init; } = [];

    /// <summary>Extra words that mark a button or menu item as a delete action, e.g. "purge".</summary>
    public IReadOnlyList<string> BlockedWordsExtra { get; init; } = [];
    public IReadOnlyList<string> ProcessBlocklistExtra { get; init; } = [];
    public IReadOnlyList<string> ConfirmTypingProcesses { get; init; } =
        ["Code.exe", "idea64.exe", "devenv.exe", "pycharm64.exe", "rider64.exe"];

    /// <summary>Empty by default: nothing is pre-approved until the user adds it by hand.</summary>
    public IReadOnlyList<PreapprovedAction> Preapproved { get; init; } = [];
    public IReadOnlyList<string> AppLaunchAllowPaths { get; init; } = [];
    public BrowserConfig Browser { get; init; } = new();
    public TimeoutsConfig Timeouts { get; init; } = new();
    public ObserveConfig Observe { get; init; } = new();
    public bool LogTypedText { get; init; }
    public string ConfirmMode { get; init; } = ConfirmModeOff;

    /// <summary>Contract 2.5. shell, registry, files and process tools. OFF by default (owner decision 2026-11-13).</summary>
    public PowerToolsConfig PowerTools { get; init; } = new();

    /// <summary>
    /// Owner decision 2026-11-19: <c>safety.allow_delete</c> lifts the no-delete rule for desktop, browser and typing actions.
    /// Off by default.
    /// </summary>
    public SafetyConfig Safety { get; init; } = new();

    /// <summary>Contract 2.5. Other computer-use servers OmniWin can start and forward to (Windows-MCP, agent-desktop).</summary>
    public EnginesConfig Engines { get; init; } = new();

    /// <summary>Contract 2.5. The fast local router behind omniwin_do.</summary>
    public RouterConfig Router { get; init; } = new();

    /// <summary>Contract 2.6. Learning: a task the AI did once is saved as a recipe and replayed without thinking between steps.</summary>
    public LearningConfig Learning { get; init; } = new();

    /// <summary>Contract 2.6. An optional decision model that picks among learned recipes and quick commands. Off unless the owner wires one.</summary>
    public DecisionConfig Decision { get; init; } = new();

    /// <summary>Problems that must stop the server from starting. Empty when the config is usable.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (ConfirmMode is ConfirmModeRisky)
        {
            errors.Add($"confirm_mode \"{ConfirmModeRisky}\" needs the confirm dialog, which isn't built; use \"{ConfirmModeOff}\"");
        }
        else if (ConfirmMode is not (ConfirmModeOff or ConfirmModeTestAutoAllow))
        {
            errors.Add($"confirm_mode must be \"{ConfirmModeOff}\" or \"{ConfirmModeTestAutoAllow}\"");
        }

        if (string.IsNullOrWhiteSpace(KillHotkey) || !KillSwitch.TryParseHotkey(KillHotkey, out _, out _))
        {
            errors.Add("kill_hotkey must contain modifiers and one valid key without duplicate modifiers");
        }

        if (UserActivityCooldownMs < 0)
        {
            errors.Add("user_activity_cooldown_ms must be >= 0");
        }

        if (RateLimit.MaxActions < 1 || RateLimit.PerSeconds < 1)
        {
            errors.Add("rate_limit.max_actions and rate_limit.per_seconds must be >= 1");
        }

        if (Timeouts.UiaTransactionMs < 1 || Timeouts.UiaConnectionMs < 1 || Timeouts.CdpCommandMs < 1 ||
            Timeouts.DefaultExpectMs is < 1 or > 30000)
        {
            errors.Add("timeouts must be >= 1 ms, and default_expect_ms at most 30000");
        }

        if (Observe.DefaultMaxElements is < 1 or > 1000 || Observe.MaxDepth < 1)
        {
            errors.Add("observe.default_max_elements must be 1..1000 and observe.max_depth >= 1");
        }

        if (Browser.Mode != "dedicated")
        {
            errors.Add("browser.mode must be \"dedicated\"");
        }

        if (PowerTools.ShellTimeoutMs is < 1000 or > 600000 || PowerTools.MaxOutputChars is < 100 or > 1000000)
        {
            errors.Add("power_tools.shell_timeout_ms must be 1000..600000 and power_tools.max_output_chars 100..1000000");
        }

        if (PowerTools.AllowDelete && !PowerTools.Enabled)
        {
            errors.Add("power_tools.allow_delete needs power_tools.enabled");
        }

        foreach (var engine in Engines.List)
        {
            if (string.IsNullOrWhiteSpace(engine.Name) || string.IsNullOrWhiteSpace(engine.Command) ||
                engine.StartupTimeoutMs < 1000 || engine.CallTimeoutMs < 1000)
            {
                errors.Add($"engines.list entry \"{engine.Name}\" needs a name, a command and timeouts of at least 1000 ms");
            }
        }

        if (Router.Laya.Threshold is <= 0 or > 1 || Router.Laya.MaxLatencyMs < 1)
        {
            errors.Add("router.laya.threshold must be above 0 and at most 1, and max_latency_ms at least 1");
        }

        if (Learning.PromoteAfter < 1 || Learning.MaxRecipes < 1 || Learning.EpisodeMaxSteps is < 2 or > 200 || Learning.EpisodeIdleMinutes < 1)
        {
            errors.Add("learning.promote_after and max_recipes must be >= 1, episode_max_steps 2..200 and episode_idle_minutes >= 1");
        }

        if (Decision.Mode is not (DecisionConfig.ModeOff or DecisionConfig.ModeShadow or DecisionConfig.ModeAct))
        {
            errors.Add("decision.mode must be \"off\", \"shadow\" or \"act\"");
        }

        if (Decision.Threshold is <= 0 or > 1 || Decision.Margin is < 0 or > 1 || Decision.MaxLatencyMs < 1 || Decision.TimeoutMs < 100 ||
            Decision.MaxWords is < 1 or > 60)
        {
            errors.Add("decision.threshold must be above 0 and at most 1, margin 0..1, max_latency_ms >= 1, timeout_ms >= 100 and max_words 1..60");
        }

        if (Decision.Mode != DecisionConfig.ModeOff && Decision.Provider == DecisionConfig.ProviderNone)
        {
            errors.Add("decision.mode needs decision.provider (it is \"none\")");
        }

        return errors;
    }
}

/// <summary>Contract 2.6: learning recipes from tasks the AI performed.</summary>
public sealed record LearningConfig
{
    /// <summary>task_done may save a recipe. Off: nothing is learned and nothing learned is run by omniwin_do.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>omniwin_do may run a trusted recipe by itself when the request matches its wording exactly.</summary>
    public bool AutoRun { get; init; } = true;

    /// <summary>Clean replays in a row before a recipe is trusted.</summary>
    public int PromoteAfter { get; init; } = 3;

    public int MaxRecipes { get; init; } = 200;

    public int EpisodeMaxSteps { get; init; } = 60;

    public int EpisodeIdleMinutes { get; init; } = 10;
}

/// <summary>Contract 2.6: the optional decision model. Nothing is constructed, started or connected while provider is "none".</summary>
public sealed record DecisionConfig
{
    public const string ProviderNone = "none";
    public const string ModeOff = "off";
    public const string ModeShadow = "shadow";
    public const string ModeAct = "act";

    /// <summary>"none", or the name of an adapter the server knows.</summary>
    public string Provider { get; init; } = ProviderNone;

    /// <summary>off: never asked. shadow: asked and scored, never acted on. act: may run a pick that passes every rule.</summary>
    public string Mode { get; init; } = ModeOff;

    public double Threshold { get; init; } = 0.95;

    /// <summary>The top pick must lead the second by at least this much.</summary>
    public double Margin { get; init; } = 0.30;

    public int MaxLatencyMs { get; init; } = 400;

    public int TimeoutMs { get; init; } = 1500;

    public int MaxWords { get; init; } = 20;

    public string? Endpoint { get; init; }

    /// <summary>Name of the environment variable that holds the key. The key itself is never in a file.</summary>
    public string? ApiKeyEnv { get; init; }

    /// <summary>A model that is not on this PC receives the request wording and the option names. Blocked unless the owner sets this.</summary>
    public bool AllowRemote { get; init; }
}

/// <summary>
/// Power tools: PowerShell, registry, files, processes. <see cref="Enabled"/> is false by default and is also what removes the same
/// kind of tools from bridged engines. <see cref="AllowDelete"/> lets those tools delete (it is false unless the owner says so).
/// </summary>
public sealed record PowerToolsConfig
{
    public bool Enabled { get; init; }
    public bool AllowDelete { get; init; }
    public int ShellTimeoutMs { get; init; } = 30000;
    public int MaxOutputChars { get; init; } = 20000;
}

/// <summary>
/// Engines OmniWin can start as child MCP servers. When <see cref="List"/> is empty, the known engines found under the tools folder
/// (windows-mcp, agent-desktop) are used with built-in, safe arguments. <see cref="Enabled"/> false hides engine_call entirely.
/// </summary>
public sealed record EnginesConfig
{
    public bool Enabled { get; init; } = true;

    /// <summary>Optional explicit engines. Empty means "discover the known ones".</summary>
    public IReadOnlyList<EngineConfig> List { get; init; } = [];

    /// <summary>Folder that holds the engines (default: the "tools" folder beside the server's folder).</summary>
    public string? ToolsDir { get; init; }
}

public sealed record EngineConfig
{
    public string Name { get; init; } = string.Empty;
    public string Command { get; init; } = string.Empty;
    public IReadOnlyList<string> Args { get; init; } = [];
    public IReadOnlyDictionary<string, string> Env { get; init; } = new Dictionary<string, string>();
    public string? WorkingDir { get; init; }
    public bool Enabled { get; init; } = true;
    public int StartupTimeoutMs { get; init; } = 60000;
    public int CallTimeoutMs { get; init; } = 30000;

    /// <summary>Tool names that are never forwarded (applied by OmniWin whatever the engine itself does).</summary>
    public IReadOnlyList<string> ExcludeTools { get; init; } = [];

    /// <summary>When set, only these tool names are forwarded.</summary>
    public IReadOnlyList<string>? AllowTools { get; init; }

    /// <summary>
    /// Arguments that are refused for a tool, written "Tool.argument" (for example "App.executable": a call that carries that argument is
    /// denied before the engine is asked). For engines whose tool also starts programs or writes files through an argument.
    /// </summary>
    public IReadOnlyList<string> ExcludeArguments { get; init; } = [];

    /// <summary>
    /// Tools that only read (no input is sent), for engines that do not say so themselves. A tool the engine marks read-only is also
    /// read-only. A read comes back unverified with input_sent=false and retry=safe; anything else with input_sent=true.
    /// </summary>
    public IReadOnlyList<string> ReadOnlyTools { get; init; } = [];
}

public sealed record RouterConfig
{
    /// <summary>omniwin_do: the local phrase router (stage A). On by default; it only runs commands it matches exactly.</summary>
    public bool Enabled { get; init; } = true;

    public LayaConfig Laya { get; init; } = new();
}

/// <summary>Stage B of the router: the Laya decision model as a separate local process. Off until it passes its offline gate.</summary>
public sealed record LayaConfig
{
    public bool Enabled { get; init; }

    /// <summary>Folder with the Laya files and its Python environment (default: tools\laya beside the server).</summary>
    public string? Dir { get; init; }

    /// <summary>A command is run from Laya's answer only at or above this confidence (calibrated, not a default to trust).</summary>
    public double Threshold { get; init; } = 0.9;

    public int MaxLatencyMs { get; init; } = 250;
}

/// <summary>
/// <c>safety</c> in config.json. With <see cref="AllowDelete"/> on, the gate allows what it would deny for deleting (Delete
/// buttons, delete keys in file views, drops on the Recycle Bin, delete commands typed or pasted into a terminal, the terminal
/// Enter check) and labels it <c>risk=risky</c> in the result and the audit log; <c>omniwin_status</c> then shows
/// <c>safety_gate=off</c>. Blocked processes (secure desktop, sign-in, OmniWin itself, process_blocklist_extra), password
/// fields and the stop key are not affected. The power tools keep their own <c>power_tools.allow_delete</c>.
/// </summary>
public sealed record SafetyConfig
{
    public bool AllowDelete { get; init; }
}

public sealed record RateLimitConfig
{
    public int MaxActions { get; init; } = 30;
    public int PerSeconds { get; init; } = 10;
}

public sealed record PreapprovedAction
{
    public required string Process { get; init; }
    public required string TargetName { get; init; }
    public required string Action { get; init; }
}

public sealed record BrowserConfig
{
    public string Mode { get; init; } = "dedicated";
    public string Channel { get; init; } = "msedge";

    /// <summary>Null = <c>&lt;data dir&gt;\browser-profile</c>. See <see cref="OmniWinPaths.ResolveBrowserProfileDir"/>.</summary>
    public string? ProfileDir { get; init; }
    public string Downloads { get; init; } = "deny";
}

public sealed record TimeoutsConfig
{
    public int UiaTransactionMs { get; init; } = 1000;
    public int UiaConnectionMs { get; init; } = 2000;
    public int CdpCommandMs { get; init; } = 5000;
    public int DefaultExpectMs { get; init; } = 2000;
}

public sealed record ObserveConfig
{
    public int DefaultMaxElements { get; init; } = 150;
    public int MaxDepth { get; init; } = 25;
}
