using System.Diagnostics.CodeAnalysis;

namespace OmniWin.Server.Hosting;

/// <summary>Command-line options. Unknown arguments are an error, never ignored.</summary>
internal sealed record ServerOptions(string? DataDir, bool AllowElevated, bool TestMode)
{
    public const string Usage =
        "usage: OmniWin.Server [--data-dir <path>] [--test-mode] [--allow-elevated]\n" +
        "       OmniWin.Server --validate-config <file>   check a config file, print {ok, errors} as JSON, and exit\n" +
        "  --data-dir <path>  keep config, logs, workflows and the browser profile here instead of %LOCALAPPDATA%\\OmniWin\n" +
        "  --test-mode        started by the integration suite (required for confirm_mode \"test_auto_allow\")\n" +
        "  --allow-elevated   allow running as administrator; also needs \"allow_elevated\": true in config.json";

    public static bool TryParse(IReadOnlyList<string> args,
                                [NotNullWhen(true)] out ServerOptions? options,
                                [NotNullWhen(false)] out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? dataDir = null;
        bool allowElevated = false, testMode = false;
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--data-dir" when i + 1 < args.Count && !string.IsNullOrWhiteSpace(args[i + 1]):
                    dataDir = args[++i];
                    break;
                case "--data-dir":
                    return Fail("--data-dir needs a path", out options, out error);
                case "--allow-elevated":
                    allowElevated = true;
                    break;
                case "--test-mode":
                    testMode = true;
                    break;
                default:
                    return Fail($"unknown argument \"{args[i]}\"", out options, out error);
            }
        }

        options = new ServerOptions(dataDir, allowElevated, testMode);
        error = null;
        return true;
    }

    private static bool Fail(string message, out ServerOptions? options, out string? error)
    {
        options = null;
        error = message;
        return false;
    }
}
