using OmniWin.Safety;

namespace OmniWin.Server.Engines;

/// <summary>
/// Which engines exist. Explicit entries in <c>config.json</c> win; otherwise the known engines found under the tools folder are
/// used with built-in arguments that keep them quiet (telemetry off), headless and inside the power-tools switch.
/// </summary>
internal static class EngineCatalog
{
    public const string WindowsMcp = "windows-mcp";
    public const string AgentDesktop = "agent-desktop";

    /// <summary>
    /// Tools of Windows-MCP that run commands, edit the registry, touch files or end processes. They are filtered out unless
    /// <c>power_tools.enabled</c> is true. (OmniWin applies this filter itself and also passes it to the engine.)
    /// </summary>
    public static readonly IReadOnlyList<string> WindowsMcpPowerTools = ["PowerShell", "Registry", "FileSystem", "Process"];

    /// <summary>
    /// Arguments of Windows-MCP tools that start any program (App: executable, args, cwd) or write a file (Screenshot and Snapshot:
    /// save_path). Refused unless power tools are on. The tools themselves stay: launching an app by name and reading the screen are
    /// what the engine is for.
    /// </summary>
    public static readonly IReadOnlyList<string> WindowsMcpPowerArguments =
        ["App.executable", "App.args", "App.cwd", "Screenshot.save_path", "Snapshot.save_path"];

    /// <summary>
    /// agent-desktop tools that start any program with arguments and environment (launch), can run other commands inside one call
    /// (batch, so it could carry a launch past this filter), end processes by force (close_app), write files (trace_export), or are
    /// macOS-only virtual machines (sandbox_*). Filtered unless power tools are on.
    /// </summary>
    public static readonly IReadOnlyList<string> AgentDesktopPowerTools =
    [
        "desktop_launch", "desktop_batch", "desktop_close_app", "desktop_trace_export",
        "desktop_sandbox_install", "desktop_sandbox_create", "desktop_sandbox_start", "desktop_sandbox_stop",
        "desktop_sandbox_status", "desktop_sandbox_list", "desktop_sandbox_rm", "desktop_sandbox_exec",
    ];

    /// <summary>agent-desktop does not mark its reading tools read-only; these are the ones that send no input.</summary>
    public static readonly IReadOnlyList<string> AgentDesktopReadOnly =
    [
        "desktop_snapshot", "desktop_find", "desktop_screenshot", "desktop_get", "desktop_is", "desktop_list_windows",
        "desktop_list_displays", "desktop_list_apps", "desktop_list_surfaces", "desktop_list_notifications", "desktop_clipboard_get",
        "desktop_wait", "desktop_status", "desktop_permissions", "desktop_version", "desktop_skills_list", "desktop_skills_get",
        "desktop_skills_path", "desktop_session_list", "desktop_trace_show",
    ];

    /// <summary>The folder with the engines: config, then OMNIWIN_TOOLS_DIR, then a "tools" folder in this or a parent directory.</summary>
    public static string? ResolveToolsDir(OmniWinConfig config, string baseDir)
    {
        if (!string.IsNullOrWhiteSpace(config.Engines.ToolsDir))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(config.Engines.ToolsDir));
        }

        if (Environment.GetEnvironmentVariable("OMNIWIN_TOOLS_DIR") is { Length: > 0 } fromEnv)
        {
            return Path.GetFullPath(fromEnv);
        }

        for (var dir = new DirectoryInfo(baseDir); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tools");
            if (Directory.Exists(Path.Combine(candidate, WindowsMcp)) || Directory.Exists(Path.Combine(candidate, AgentDesktop)))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>True when engine_call should be registered: engines are on and at least one is enabled with its program present.</summary>
    public static bool AnyUsable(OmniWinConfig config) =>
        Build(config, AppContext.BaseDirectory).Any(e => e.Enabled && (!Path.IsPathRooted(e.Command) || File.Exists(e.Command)));

    public static IReadOnlyList<EngineConfig> Build(OmniWinConfig config, string baseDir)
    {
        if (!config.Engines.Enabled) return [];
        if (config.Engines.List.Count > 0) return config.Engines.List;

        var tools = ResolveToolsDir(config, baseDir);
        if (tools is null) return [];

        var engines = new List<EngineConfig>();
        var winMcp = Path.Combine(tools, WindowsMcp, ".venv", "Scripts", "windows-mcp.exe");
        if (File.Exists(winMcp))
        {
            var args = new List<string> { "serve", "--transport", "stdio", "--no-control" };
            var configToml = Path.Combine(tools, WindowsMcp, "config.toml");
            if (File.Exists(configToml)) args.AddRange(["--config", configToml]);
            if (!config.PowerTools.Enabled) args.AddRange(["--exclude-tools", string.Join(',', WindowsMcpPowerTools)]);

            engines.Add(new EngineConfig
            {
                Name = WindowsMcp,
                Command = winMcp,
                Args = args,
                WorkingDir = Path.Combine(tools, WindowsMcp),
                // Telemetry off from the first start; UTF-8 so non-English text survives the pipe.
                Env = new Dictionary<string, string>
                {
                    ["ANONYMIZED_TELEMETRY"] = "false",
                    ["PYTHONUTF8"] = "1",
                    ["PYTHONIOENCODING"] = "utf-8",
                },
                StartupTimeoutMs = 120000,
                CallTimeoutMs = 60000,
                ExcludeTools = config.PowerTools.Enabled ? [] : WindowsMcpPowerTools,
                ExcludeArguments = config.PowerTools.Enabled ? [] : WindowsMcpPowerArguments,
            });
        }

        var agentDesktop = Path.Combine(tools, AgentDesktop, "agent-desktop.exe");
        if (File.Exists(agentDesktop))
        {
            engines.Add(new EngineConfig
            {
                Name = AgentDesktop,
                Command = agentDesktop,
                // Headless by default: semantic actions only, no focus changes, no cursor movement. Add "--headed" here to allow
                // physical input (hover, drag, physical double-click).
                Args = ["mcp", "--session", "omniwin"],
                WorkingDir = Path.Combine(tools, AgentDesktop),
                StartupTimeoutMs = 30000,
                CallTimeoutMs = 30000,
                ExcludeTools = config.PowerTools.Enabled ? [] : AgentDesktopPowerTools,
                ReadOnlyTools = AgentDesktopReadOnly,
            });
        }

        return engines;
    }
}
