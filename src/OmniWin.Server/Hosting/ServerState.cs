using System.Diagnostics;
using System.Reflection;
using OmniWin.Core;
using OmniWin.Safety;

namespace OmniWin.Server.Hosting;

internal sealed record PlatformInfo(bool Elevated, string DpiAwareness);

/// <summary>Process-wide facts reported by omniwin_status.</summary>
internal sealed class ServerState(
    ServerOptions options,
    OmniWinPaths paths,
    OmniWinConfig config,
    PlatformInfo platform,
    IEnumerable<IComponentStatus> components)
{
    public const string Milestone = "M5-release";

    /// <summary>Components omniwin_status always lists, in this order. Unregistered ones show as not_built.</summary>
    public static readonly IReadOnlyList<string> KnownComponents =
        ["desktop", "browser", "system_tools", "ocr", "safety_gate", "kill_switch", "user_activity", "audit_log", "workflows"];

    public static string Version { get; } = ReadVersion();

    public long StartedTimestamp { get; } = Stopwatch.GetTimestamp();

    public int ProcessId { get; } = Environment.ProcessId;

    public ServerOptions Options { get; } = options;

    public OmniWinPaths Paths { get; } = paths;

    public OmniWinConfig Config { get; } = config;

    public PlatformInfo Platform { get; } = platform;

    public IReadOnlyList<IComponentStatus> Components { get; } = [.. components];

    private static string ReadVersion()
    {
        var info = typeof(ServerState).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(info))
        {
            return "0.0.0";
        }

        var plus = info.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? info : info[..plus];
    }
}
