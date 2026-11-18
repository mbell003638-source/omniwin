using OmniWin.Core;

namespace OmniWin.SystemTools;

/// <summary>
/// How <c>app_launch</c> decides what it may start (contract §8 "Deliberately absent", 03 §8). Pure logic, no I/O.
/// <list type="bullet">
/// <item>A Start-menu name or AUMID that is in the app list may be started, terminals included (owner policy).</item>
/// <item>Anything that looks like a path, URL, command line or shell syntax is refused unless the exact path is in
/// <c>app_launch_allow_paths</c>.</item>
/// <item><c>args</c> are never given to a command processor or script host, because a command line there would
/// bypass the no-delete check that typed commands go through.</item>
/// </list>
/// </summary>
internal static class AppLaunchRules
{
    /// <summary>Command processors and script hosts that never receive <c>args</c>.</summary>
    public static readonly IReadOnlySet<string> CommandProcessors = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "cmd.exe", "powershell.exe", "pwsh.exe", "powershell_ise.exe", "wt.exe", "WindowsTerminal.exe",
        "wscript.exe", "cscript.exe", "mshta.exe",
        // F8: other interpreters and loaders an owner might allow-list; none of them may receive args either.
        "python.exe", "pythonw.exe", "py.exe", "node.exe", "bash.exe", "sh.exe", "wsl.exe", "perl.exe", "ruby.exe",
        "java.exe", "javaw.exe", "rundll32.exe", "regsvr32.exe", "msbuild.exe", "msiexec.exe",
    };

    private static readonly string[] ExecutableLikeSuffixes =
        [".exe", ".com", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".msi", ".lnk", ".scr", ".cpl", ".msc", ".hta", ".dll"];

    private const string ShellCharacters = "\\/:|&;<>`$\"'%^\n\r\t()";

    /// <summary>True for text that is a path, URL, command line or shell syntax rather than an app name.</summary>
    public static bool LooksLikePathOrCommand(string app)
    {
        if (string.IsNullOrWhiteSpace(app)) return true;
        var text = app.Trim();
        if (text.AsSpan().IndexOfAny(ShellCharacters) >= 0) return true;

        foreach (var suffix in ExecutableLikeSuffixes)
        {
            if (text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>Exact, case-insensitive path match against the allow list (config <c>app_launch_allow_paths</c>).</summary>
    public static bool IsAllowListed(string app, IReadOnlyList<string> allowPaths) => AllowListedPath(app, allowPaths) is not null;

    /// <summary>The allow-list entry that equals the path (as the owner wrote it), or null.</summary>
    public static string? AllowListedPath(string app, IReadOnlyList<string> allowPaths)
    {
        var wanted = Normalize(app);
        foreach (var path in allowPaths)
        {
            if (string.Equals(Normalize(path), wanted, StringComparison.OrdinalIgnoreCase)) return path.Trim().Trim('"');
        }

        return null;
    }

    /// <summary>Best guess at the process a Start-menu entry starts, so the gate can check it against the blocklist.</summary>
    public static string? ExecutableFor(InstalledApp app)
    {
        var id = app.LaunchId;
        var slash = Math.Max(id.LastIndexOf('\\'), id.LastIndexOf('/'));
        var file = slash >= 0 ? id[(slash + 1)..] : id;
        if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return file;

        var haystack = $"{id} {app.Name}";
        if (haystack.Contains("WindowsTerminal", StringComparison.OrdinalIgnoreCase) ||
            app.Name.Equals("Terminal", StringComparison.OrdinalIgnoreCase) ||
            app.Name.Equals("Windows Terminal", StringComparison.OrdinalIgnoreCase))
        {
            return "WindowsTerminal.exe";
        }

        if (haystack.Contains("powershell", StringComparison.OrdinalIgnoreCase)) return "powershell.exe";
        if (app.Name.Contains("Command Prompt", StringComparison.OrdinalIgnoreCase)) return "cmd.exe";
        return null;
    }

    public static bool IsCommandProcessor(string? executable) =>
        executable is not null && CommandProcessors.Contains(Path.GetFileName(executable));

    /// <summary>Exact name or id first; then a unique case-insensitive prefix of a name.</summary>
    public static AppMatch Match(string app, IReadOnlyList<InstalledApp> apps)
    {
        var wanted = app.Trim();
        var exact = apps.Where(a => a.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase) ||
                                    a.LaunchId.Equals(wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1) return AppMatch.One(exact[0]);
        if (exact.Count > 1) return AppMatch.Many(exact);

        var prefix = apps.Where(a => a.Name.StartsWith(wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        return prefix.Count switch
        {
            1 => AppMatch.One(prefix[0]),
            0 => AppMatch.None,
            _ => AppMatch.Many(prefix),
        };
    }

    private static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path.Trim().Trim('"'));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.Trim();
        }
    }
}

internal sealed record AppMatch(InstalledApp? Single, IReadOnlyList<InstalledApp> Candidates)
{
    public static readonly AppMatch None = new(null, []);

    public static AppMatch One(InstalledApp app) => new(app, [app]);

    public static AppMatch Many(IReadOnlyList<InstalledApp> apps) => new(null, apps);
}
