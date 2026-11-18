using OmniWin.Safety;

namespace OmniWin.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // One copy per Windows session: a second start just ends.
        using var single = new Mutex(true, @"Local\OmniWin.App.Single", out var first);
        if (!first) return 0;

        string? dataDir = null;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--data-dir") dataDir = args[i + 1];
        }

        var paths = dataDir is null ? OmniWinPaths.Default() : OmniWinPaths.FromOverride(dataDir);
        ApplicationConfiguration.Initialize();
        using var context = new TrayContext(paths, dataDir);
        Application.Run(context);
        return 0;
    }
}
