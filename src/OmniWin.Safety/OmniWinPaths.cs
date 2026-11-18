namespace OmniWin.Safety;

/// <summary>
/// Where OmniWin keeps its files. Default <c>%LOCALAPPDATA%\OmniWin</c>; the server's <c>--data-dir</c> argument
/// overrides it so tests never touch the real config, logs, workflows or browser profile (contract 2.1).
/// </summary>
public sealed class OmniWinPaths
{
    private OmniWinPaths(string dataDir, bool isOverride)
    {
        DataDir = dataDir;
        IsOverride = isOverride;
    }

    public string DataDir { get; }

    /// <summary>True when the data dir came from <c>--data-dir</c>.</summary>
    public bool IsOverride { get; }

    public string ConfigFile => Path.Combine(DataDir, "config.json");

    public string LogsDir => Path.Combine(DataDir, "logs");

    public string WorkflowsDir => Path.Combine(DataDir, "workflows");

    /// <summary>The owner's named rewrite instructions for polishing text (contract 2.6). Read by polish_prompts; edited by the app.</summary>
    public string PromptsFile => Path.Combine(DataDir, "prompts.json");

    /// <summary>Recipe cards and the append-only events file (contract 2.6).</summary>
    public string RecipesDir => Path.Combine(DataDir, "recipes");

    public string DefaultBrowserProfileDir => Path.Combine(DataDir, "browser-profile");

    public static OmniWinPaths Default() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OmniWin"), isOverride: false);

    public static OmniWinPaths FromOverride(string dataDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDir);
        return new(Path.GetFullPath(Environment.ExpandEnvironmentVariables(dataDir)), isOverride: true);
    }

    /// <summary>Config value if set (environment variables expanded), else <see cref="DefaultBrowserProfileDir"/>.</summary>
    public string ResolveBrowserProfileDir(OmniWinConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var configured = config.Browser.ProfileDir;
        return string.IsNullOrWhiteSpace(configured)
            ? DefaultBrowserProfileDir
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured));
    }
}
