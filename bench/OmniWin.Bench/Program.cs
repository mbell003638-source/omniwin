using OmniWin.Bench;

// OmniWin.Bench: the WS4 latency runner. No real input is sent by the "e2e" and "micro" modes.
//   OmniWin.Bench e2e      server-side and client round-trip latency over MCP stdio, headless browser, accuracy gates (20-run browser set)
//   OmniWin.Bench micro    in-process timing of the formatters and expectation parsing
//   OmniWin.Bench tasks    the OmniWin side of the nine-task comparison (B01-B07, B09, B10). Real input: needs OMNIWIN_LIVE_SESSION=1
//   OmniWin.Bench clicks   click speed on the F01 fixture, per phase, for two cases (expectation met; nothing changes). Real input: needs OMNIWIN_LIVE_SESSION=1
//   OmniWin.Bench typing   one long type (--target f01|notepad --chars N) read back exactly. Real input: needs OMNIWIN_LIVE_SESSION=1
//   OmniWin.Bench compare  OmniWin's own tools against Windows-MCP and agent-desktop on the same F01 jobs, judged by the fixture's log. Real input
//   OmniWin.Bench all      e2e + micro, then the report
// Files go to artifacts/benchmarks/ and bench/reports/ under new names (never over an old file).

var mode = args.Length > 0 ? args[0] : "all";
var runs = args.Length > 2 && args[1] == "--runs" && int.TryParse(args[2], out var n) ? n : 20;
var stamp = DateTime.Now.ToString("yyyy-MM-dd");
var root = Paths.RepoRoot();

try
{
    switch (mode)
    {
        case "e2e":
            await Report.WriteAsync(root, stamp, await E2E.RunAsync(root), null);
            break;
        case "micro":
            await Report.WriteAsync(root, stamp, null, Micro.Run());
            break;
        case "tasks":
            if (Environment.GetEnvironmentVariable("OMNIWIN_LIVE_SESSION") != "1")
            {
                Console.Error.WriteLine("not run: OMNIWIN_LIVE_SESSION=1 is not set. tasks sends real mouse and keyboard input. Start it only in the live session with the owner present: set OMNIWIN_LIVE_SESSION=1.");
                return 2;
            }

            // Exit 3 = "not run: no connected desktop" (a report saying so is written; nothing is recorded as 0/N).
            return await Tasks.RunAsync(root, stamp, runs);
        case "clicks":
            if (Environment.GetEnvironmentVariable("OMNIWIN_LIVE_SESSION") != "1")
            {
                Console.Error.WriteLine("not run: OMNIWIN_LIVE_SESSION=1 is not set. clicks sends real mouse input. Start it only in the live session with the owner present: set OMNIWIN_LIVE_SESSION=1.");
                return 2;
            }

            // --runs N = clicks per case (default 40 here, because 20 is too few for a p95).
            return await Clicks.RunAsync(root, args.Length > 2 && args[1] == "--runs" ? runs : 40);
        case "typing":
            if (Environment.GetEnvironmentVariable("OMNIWIN_LIVE_SESSION") != "1")
            {
                Console.Error.WriteLine("not run: OMNIWIN_LIVE_SESSION=1 is not set. typing sends real keyboard input. Start it only in the live session with the owner present: set OMNIWIN_LIVE_SESSION=1.");
                return 2;
            }

            // typing --target f01|notepad --chars N
            string Arg(string name, string fallback) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
            return await Tasks.TypingAsync(root, Arg("--target", "f01"), int.TryParse(Arg("--chars", "1000"), out var c) ? c : 0);
        case "compare":
            if (Environment.GetEnvironmentVariable("OMNIWIN_LIVE_SESSION") != "1")
            {
                Console.Error.WriteLine("not run: OMNIWIN_LIVE_SESSION=1 is not set. compare sends real mouse and keyboard input. Start it only in the live session with the owner present: set OMNIWIN_LIVE_SESSION=1.");
                return 2;
            }

            // compare [--runs N] [--only omniwin|windows-mcp|agent-desktop]: clicks per job per way (default 10).
            string? Opt(string name) => Array.IndexOf(args, name) is var at and >= 0 && at + 1 < args.Length ? args[at + 1] : null;
            return await Compare.RunAsync(root, int.TryParse(Opt("--runs"), out var perJob) && perJob > 0 ? perJob : 10, Opt("--only"));
        case "all":
            await Report.WriteAsync(root, stamp, await E2E.RunAsync(root), Micro.Run());
            break;
        default:
            Console.Error.WriteLine("usage: OmniWin.Bench [e2e|micro|tasks [--runs N]|clicks [--runs N]|typing --target f01|notepad --chars N|compare [--runs N]|all]");
            return 2;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine("bench failed: " + ex);
    return 1;
}

return 0;
