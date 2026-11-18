using System.Windows.Forms;

namespace OmniWin.FixtureApp.Scenarios;

/// <summary>Starts one fixture scenario (F01..F17). Prints OMNIWIN_FIXTURE_READY when the window is shown, then the log path.</summary>
internal static class ScenarioRunner
{
    internal static readonly string[] Known =
    [
        "F01", "F02", "F03", "F04", "F05", "F05-COVER", "F06", "F07", "F08", "F09", "F10", "F11", "F12", "F13", "F14", "F15", "F16", "F17",
    ];

    internal static int Run(ScenarioOptions options)
    {
        var scenario = options.Scenario!;
        if (!Known.Contains(scenario, StringComparer.Ordinal))
        {
            Console.Error.WriteLine($"OmniWin fixture: unknown scenario '{scenario}'. Known: {string.Join(' ', Known)}");
            return 2;
        }

        using var log = ScenarioLog.Create(scenario, options.LogPath);

        if (WpfScenarios.Create(options, log) is { } wpf)
        {
            if (options.StdinCommands) StdinCommands.Start(command => wpf.Dispatcher.BeginInvoke(() => wpf.Command(command)));
            new System.Windows.Application().Run(wpf);
            return 0;
        }

        if (WinFormsScenarios.Create(options, log) is not { } form)
        {
            Console.Error.WriteLine($"OmniWin fixture: scenario '{scenario}' is not available.");
            return 2;
        }

        if (options.StdinCommands) StdinCommands.Start(command => form.BeginInvoke((Action)(() => form.Command(command))));
        Application.Run(form);
        return 0;
    }
}
