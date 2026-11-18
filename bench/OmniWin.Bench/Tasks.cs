using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OmniWin.Integration.Harness;

namespace OmniWin.Bench;

/// <summary>
/// The OmniWin side of the nine-task comparison (WS4 brief step one): B01-B07, B09, B10. B08 is dropped (it changes a Windows setting).
/// Scripted tool calls, no model. Every outcome is checked independently of OmniWin: a file on disk, the fixture server's record of the
/// form, or a value the harness reads itself through Win32. Sends REAL mouse and keyboard input through OmniWin: live session only, owner present.
///
/// Reliability rules (file 19, T1):
///  - It never starts without a connected desktop. A disconnected, locked or window-less session writes a "not run" report; it never records 0/20.
///  - Each sample ends passed, failed or not_run. not_run means the precondition did not hold or the foreground could not be taken
///    (reason=foreground_failed / focus_failed): that is the environment, not an OmniWin result, so it is not counted against the success rate.
///  - Waits live only in this script (bounded polls on the harness's own reads). Nothing inside OmniWin waits for a fixed time.
///  - The Save As file name and Explorer's address bar are filled by typing, never set_value (those controls ignore it for what they use).
///  - A new Notepad window is found by comparing windows before and after launching; a Notepad that restored earlier tabs or text is
///    not run (the harness does not touch text it did not type) and no existing window is closed.
///  - Only the windows a sample opened itself (and a dialog one of them owns) are closed. Any other new window is left alone and listed.
///  - File Explorer tasks use a new window only, and send nothing unless that window is in the sample's own test folder.
///  - Each sample removes only the %TEMP%\OmniWin-test-* folder it created itself (by its exact path). Nothing else is ever deleted;
///    a folder that could not be removed is listed in the report.
///  - not_applicable is a fourth sample state: the task cannot be judged in this kind of session (B07 over Remote Desktop).
///  - Browser tasks run with OMNIWIN_BROWSER_HEADLESS=1, and after every sample the harness checks that no msedge.exe whose command line
///    holds that sample's own data folder is left (it stops only those it finds, and reports them).
/// </summary>
internal static partial class Tasks
{
    /// <summary>
    /// One sample's result. <paramref name="Ok"/> is the harness's own independent check. <paramref name="ServerSaidSuccess"/> is only
    /// what OmniWin itself claimed for the deciding act (status confirmed); false_success = the server said confirmed and the
    /// independent check failed. <paramref name="WrongTarget"/> = the harness saw the effect somewhere other than the sample's own window or folder.
    /// </summary>
    private sealed record Outcome(bool Ok, string Note, bool ServerSaidSuccess, bool NotRun = false, bool NotApplicable = false, bool WrongTarget = false);

    /// <summary>Thrown by a task when its precondition does not hold; the sample is recorded as not_run.</summary>
    private sealed class NotRunException(string reason) : Exception(reason);

    /// <summary>Thrown by a task that cannot be judged in this kind of session at all (B07 over Remote Desktop); recorded as not_applicable.</summary>
    private sealed class NotApplicableException(string reason) : Exception(reason);

    /// <summary>Thrown when an OmniWin act already ran and its effect did not happen; recorded as failed (not as not_run), and nothing further is sent.</summary>
    private sealed class TaskFailedException(string reason) : Exception(reason);

    /// <summary>Tools whose call can change something. Their last result is the server's claim for an early failure (false_success).</summary>
    private static readonly HashSet<string> EffectTools = new(StringComparer.Ordinal)
    {
        "desktop_act", "desktop_keys", "desktop_scroll", "browser_act", "browser_nav", "app_launch", "window_manage", "clipboard_set", "audio", "media",
    };

    /// <summary>The apps the tasks drive. Existing windows of these are watched for any change during a sample (wrong_target).</summary>
    private static readonly HashSet<string> WatchedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Notepad.exe", "explorer.exe", "ApplicationFrameHost.exe", "SystemSettings.exe", "CalculatorApp.exe",
    };

    private sealed class Ctx(McpServer server, FixtureWebServer web, string tempDir)
    {
        public McpServer Server { get; } = server;
        public FixtureWebServer Web { get; } = web;
        public string TempDir { get; } = tempDir;
        public int Calls { get; set; }
        public long Chars { get; set; }
        public bool InputSent { get; set; }
        public string? ForegroundFailure { get; set; }
        public string? NotepadWindow { get; set; }
        public IntPtr NotepadHandle { get; set; }
        public string? ExplorerWindow { get; set; }
        public bool ExplorerCreated { get; set; }

        /// <summary>The Shell handle of the sample's own new File Explorer window: the only Explorer window the folder checks look at.</summary>
        public long ExplorerHandle { get; set; }

        /// <summary>The last result of a call that can change something (see <see cref="EffectTools"/>).</summary>
        public ToolResult? LastEffect { get; private set; }

        /// <summary>What the server claimed for the last effect call: used as the claim when a task fails early.</summary>
        public bool LastEffectConfirmed => LastEffect?.Status is "confirmed";

        /// <summary>Effect calls whose status says nothing was done (denied, blocked, stale) but which reported input_sent=true.</summary>
        public List<string> InputSentMismatches { get; } = [];

        /// <summary>Existing windows of the watched apps that changed during the sample (evidence for wrong_target). No titles or text are kept.</summary>
        public List<string> BaselineChanges { get; } = [];

        private Dictionary<string, (string Process, string Title)> _baselineTitles = [];
        private Dictionary<long, string> _baselineShell = [];
        private Dictionary<IntPtr, string> _baselineNotepad = [];
        /// <summary>The Notepad tab holds a file that was saved (closing it must not change the file).</summary>
        public bool NotepadSaved { get; set; }

        /// <summary>Window ids that existed before the sample started. Never closed or changed; see <see cref="Own"/> for what cleanup may close.</summary>
        public HashSet<string> Baseline { get; private set; } = [];

        /// <summary>False when the starting window list could not be read: cleanup then touches nothing, because it could not tell the sample's windows from the owner's.</summary>
        public bool BaselineKnown { get; private set; }

        public async Task SnapshotAsync()
        {
            try
            {
                var windows = await WindowsAsync(this, count: false);
                Baseline = windows.Keys.ToHashSet();
                // What the owner's existing windows of the driven apps look like now: titles, Explorer folders, and a hash of each
                // Notepad window's text (in memory only). Any change by the end of the sample is evidence of a wrong target.
                _baselineTitles = windows.Where(w => WatchedProcesses.Contains(w.Value.Process))
                    .ToDictionary(w => w.Key, w => (w.Value.Process, w.Value.Title));
                _baselineShell = ShellFolders();
                _baselineNotepad = NotepadHandles().ToDictionary(h => h, h => Fingerprint(ReadNotepadText(h)));
                BaselineKnown = true;
            }
            catch
            {
                Baseline = [];
                BaselineKnown = false;
            }
        }

        /// <summary>
        /// After the task and before cleanup: did any existing window of the driven apps change? A changed title, an existing
        /// Explorer window in another folder, or an existing Notepad window with other text. Windows that were closed meanwhile
        /// are skipped (the owner may close their own windows). Only ids and process names are recorded, never titles or text.
        /// </summary>
        public async Task CheckBaselineAsync()
        {
            if (!BaselineKnown) return;
            try
            {
                var now = await WindowsAsync(this, count: false);
                foreach (var (id, before) in _baselineTitles)
                    if (now.TryGetValue(id, out var w) && !string.Equals(w.Title, before.Title, StringComparison.Ordinal))
                        BaselineChanges.Add($"{id} ({before.Process}): an existing window's title changed");

                var shell = ShellFolders();
                foreach (var (hwnd, folders) in _baselineShell)
                    if (shell.TryGetValue(hwnd, out var current) && !string.Equals(current, folders, StringComparison.OrdinalIgnoreCase))
                        BaselineChanges.Add($"an existing File Explorer window (0x{hwnd:X}) now shows another folder");

                foreach (var (hwnd, fingerprint) in _baselineNotepad)
                    if (IsWindow(hwnd) && Fingerprint(ReadNotepadText(hwnd)) != fingerprint)
                        BaselineChanges.Add($"an existing Notepad window's text changed (0x{hwnd.ToInt64():X})");
            }
            catch (Exception ex)
            {
                CleanupErrors.Add("the existing windows could not be compared: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>The windows this sample opened itself (its own launches). Only these, and dialogs they own, are ever closed.</summary>
        public HashSet<string> Own { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The sample's own windows that were still open after cleanup (titles).</summary>
        public List<string> LeftoverWindows { get; } = [];

        /// <summary>New windows the sample did not open (for example one the owner opened meanwhile). They are never touched, only listed.</summary>
        public List<string> OtherNewWindows { get; } = [];

        /// <summary>Anything that went wrong during cleanup. Recorded in the sample, never swallowed.</summary>
        public List<string> CleanupErrors { get; } = [];

        /// <summary>
        /// Closes only the windows this sample opened itself, and a dialog owned by one of them (dialog first). The sample's own
        /// Notepad window is emptied first so Notepad cannot restore the typed text later. Every other window is left alone.
        /// </summary>
        public async Task CleanupAsync()
        {
            if (!BaselineKnown)
            {
                CleanupErrors.Add("the starting window list was unknown: nothing was closed");
                return;
            }

            try
            {
                for (var pass = 0; pass < 3; pass++)
                {
                    var mine = (await WindowsAsync(this, count: false)).Where(w => Own.Contains(w.Key) && !Baseline.Contains(w.Key)).ToList();
                    if (mine.Count == 0) break;
                    foreach (var (id, w) in mine)
                    {
                        // A dialog is closed only when the server says this exact window owns it.
                        var dialog = await Server.ToolAsync("desktop_observe", new { window = id, scope = "dialog", max_elements = 5 });
                        var owned = Regex.Match(dialog.Text, @"^dialog (w[0-9]+) .*\bowner=(w[0-9]+)\b", RegexOptions.Multiline);
                        if (owned.Success && string.Equals(owned.Groups[2].Value, id, StringComparison.OrdinalIgnoreCase) && !Baseline.Contains(owned.Groups[1].Value))
                        {
                            await Server.ToolAsync("desktop_keys", new { keys = "escape", window = owned.Groups[1].Value });
                            await Task.Delay(300);
                        }

                        if (string.Equals(id, NotepadWindow, StringComparison.OrdinalIgnoreCase))
                        {
                            // Only the Notepad window this sample opened AND checked empty before typing can hold text the harness
                            // typed. Notepad reopens the tabs of its last session, so that typed tab is emptied and a saved tab is
                            // closed first.
                            if (w.Title.StartsWith('*')) await Server.ToolAsync("desktop_keys", new { keys = "ctrl+a backspace", window = id });
                            else if (NotepadSaved) await Server.ToolAsync("desktop_keys", new { keys = "ctrl+w", window = id });
                        }

                        var closed = await Server.ToolAsync("window_manage", new { window = id, action = "close" });
                        if (closed.Status is not ("confirmed" or "unverified" or "blocked" or "stale"))
                            CleanupErrors.Add($"close {id} \"{w.Title}\": {closed.Text.Split((char)10)[0]}");
                    }

                    await Task.Delay(400);
                }
            }
            catch (Exception ex)
            {
                CleanupErrors.Add(ex.GetType().Name + ": " + ex.Message);
            }

            // Listed even when closing failed part way, so a leftover window is never under-reported.
            try
            {
                foreach (var (id, w) in await WindowsAsync(this, count: false))
                {
                    if (Baseline.Contains(id) || w.Title == "Taskbar") continue;
                    if (Own.Contains(id)) LeftoverWindows.Add(w.Title);
                    else OtherNewWindows.Add($"{w.Process}: {w.Title}");
                }
            }
            catch (Exception ex)
            {
                CleanupErrors.Add("the window list after cleanup could not be read: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public async Task<ToolResult> Tool(string name, object args)
        {
            var r = await Server.ToolAsync(name, args);
            Calls++;
            Chars += r.Text.Length;
            if (r.InputSent == true) InputSent = true;
            if (r.Reason is "foreground_failed" or "focus_failed") ForegroundFailure = r.Reason;
            if (EffectTools.Contains(name))
            {
                LastEffect = r;
                // A result that says nothing was done must not say input was sent (checked on every call, not once per sample).
                if (r.InputSent == true && r.Status is "denied" or "blocked" or "stale")
                    InputSentMismatches.Add(name + ": " + r.FirstLine);
            }

            return r;
        }

        public async Task<string> Obs(string window, string scope = "window")
        {
            var r = await Tool("desktop_observe", new { window, scope, max_elements = 400 });
            return r.Text;
        }
    }

    [GeneratedRegex(@"^\s*(e[0-9]+) ([a-z]+) ""(.*?)""", RegexOptions.Multiline)]
    private static partial Regex ElementRegex();

    [GeneratedRegex(@"\bgen=([0-9]+)")]
    private static partial Regex GenRegex();

    [GeneratedRegex(@"^(w[0-9]+) (\S+) pid=([0-9]+) ""(.*)""(.*)$", RegexOptions.Multiline)]
    private static partial Regex WindowLineRegex();

    private const string NoDesktop = "not run: no connected desktop";

    public static async Task<int> RunAsync(string root, string stamp, int runs)
    {
        var started = DateTimeOffset.Now;
        var exe = Paths.ServerExe();

        // Up front: never record results from a session that cannot do the work.
        var why = await PreflightAsync(exe);
        if (why is not null)
        {
            var path0 = await WriteReportAsync(root, stamp, started, runs, [], NoDesktop + " (" + why + ")");
            Console.Error.WriteLine(NoDesktop + ": " + why);
            Console.WriteLine("wrote " + path0);
            return 3;
        }

        await using var web = new FixtureWebServer(Paths.Fixtures(root));
        web.Start();
        var tasks = new (string Id, string What, Func<Ctx, Task<Outcome>> Run)[]
        {
            ("B01", "Notepad: launch, type two lines, read the text back", B01),
            ("B02", "Notepad: Save As a new file through the dialog", B02),
            ("B03", "Notepad: select all, clear, confirm empty", B03),
            ("B04", "File Explorer: open a seeded folder, list file names", B04),
            ("B05", "File Explorer: create a folder and rename it", B05),
            ("B06", "File Explorer: select a named file in a 2,000-file folder", B06),
            ("B07", "Settings: open System > Display and read the scale", B07),
            ("B09", "Edge (W01): fill text, email, dropdown, checkbox, click Continue", B09),
            ("B10", "Notepad + Edge: copy a line from Notepad into W01's textarea", B10),
        };

        var results = new List<object>();
        var unusableStreak = 0;
        string? abortReason = null;
        var tempFolders = new List<string>();
        foreach (var (id, what, run) in tasks)
        {
            if (Environment.GetEnvironmentVariable("OMNIWIN_TASKS") is { Length: > 0 } only && !only.Split(',').Contains(id)) continue;
            var wall = new List<double>();
            int ok = 0, failed = 0, notRun = 0, notApplicable = 0, falseSuccess = 0, wrongTarget = 0, inputSentMismatch = 0, leftoverWindows = 0, calls = 0;
            long chars = 0;
            var notes = new List<string>();
            var samples = new List<object>();
            for (var i = 0; i < runs && abortReason is null; i++)
            {
                var temp = Path.Combine(Path.GetTempPath(), "OmniWin-test-" + id + "-" + Guid.NewGuid().ToString("N")[..8]);   // test files start with OmniWin-test-
                Directory.CreateDirectory(temp);
                var dataDir = Paths.NewDataDir();
                var env = new Dictionary<string, string> { ["OMNIWIN_BROWSER_HEADLESS"] = "1" };
                var sw = Stopwatch.StartNew();
                Outcome outcome;
                var inputSent = false;
                var leftover = 0;
                var ctxCalls = 0;
                long ctxChars = 0;
                List<string> leftoverTitles = [], otherNew = [], cleanupErrors = [], baselineChanges = [], mismatchCalls = [];
                var server = await McpServer.StartAsync(exe, dataDir, null, env);
                try
                {
                    var ctx = new Ctx(server, web, temp);
                    var sessionAtStart = DesktopAvailability.IsAvailable(out var startReason);
                    if (sessionAtStart) await ctx.SnapshotAsync();
                    try
                    {
                        if (!sessionAtStart && id != "B09") throw new NotRunException("the session is disconnected (" + startReason + ")");
                        outcome = await run(ctx);
                        if (!outcome.Ok && ctx.ForegroundFailure is { } fg)
                            outcome = new Outcome(false, "not run: " + fg + " (the foreground could not be taken)", false, NotRun: true);
                    }
                    catch (NotRunException ex)
                    {
                        outcome = new Outcome(false, "not run: " + ex.Message, false, NotRun: true);
                    }
                    catch (NotApplicableException ex)
                    {
                        outcome = new Outcome(false, "not applicable: " + ex.Message, false, NotApplicable: true);
                    }
                    catch (TaskFailedException ex)
                    {
                        outcome = ctx.ForegroundFailure is { } fg
                            ? new Outcome(false, "not run: " + fg + " (the foreground could not be taken)", false, NotRun: true)
                            : new Outcome(false, ex.Message, ctx.LastEffectConfirmed);
                    }
                    catch (Exception ex)
                    {
                        outcome = ctx.ForegroundFailure is { } fg
                            ? new Outcome(false, "not run: " + fg + " (the foreground could not be taken)", false, NotRun: true)
                            : new Outcome(false, "exception: " + ex.Message, ctx.LastEffectConfirmed);
                    }

                    if (!outcome.Ok && !outcome.NotRun && !outcome.NotApplicable && id != "B09" && !DesktopAvailability.IsAvailable(out var endReason))
                        outcome = new Outcome(false, "not run: the session disconnected during the sample (" + endReason + ")", false, NotRun: true);
                    if (sessionAtStart)
                    {
                        await ctx.CheckBaselineAsync();   // before cleanup, so cleanup's own actions are not counted
                        await ctx.CleanupAsync();
                    }

                    inputSent = ctx.InputSent;
                    ctxCalls = ctx.Calls;
                    ctxChars = ctx.Chars;
                    leftoverTitles = ctx.LeftoverWindows;
                    otherNew = ctx.OtherNewWindows;
                    cleanupErrors = ctx.CleanupErrors;
                    baselineChanges = ctx.BaselineChanges;
                    mismatchCalls = ctx.InputSentMismatches;
                }
                finally
                {
                    await server.DisposeAsync();
                    // Stops only msedge.exe whose command line holds this sample's own data folder.
                    leftover = EdgeProcesses.StopOwned(dataDir);
                }

                // The sample's own server data folder (and the Edge profile inside it), by its exact path, after Edge stopped.
                var dataError = await Paths.RemoveOwnDataDirAsync(dataDir);
                var dataFolderLeft = dataError is null ? null : dataDir;
                if (dataError is not null) cleanupErrors.Add("data folder not removed: " + dataError);

                wall.Add(sw.Elapsed.TotalSeconds);
                calls += ctxCalls;
                chars += ctxChars;
                var status = outcome.NotApplicable ? "not_applicable" : outcome.NotRun ? "not_run" : outcome.Ok ? "passed" : "failed";
                // Three samples in a row that could not use the desktop (disconnected, locked, or no foreground): stop the whole run instead of recording more noise.
                unusableStreak = outcome.NotRun && (outcome.Note.Contains("the session is disconnected", StringComparison.Ordinal) || outcome.Note.Contains("foreground_failed", StringComparison.Ordinal) || outcome.Note.Contains("session disconnected during", StringComparison.Ordinal) || outcome.Note.Contains("no connected desktop", StringComparison.Ordinal)) ? unusableStreak + 1 : 0;
                if (unusableStreak >= 3) abortReason = "stopped early: the desktop could not be used for 3 samples in a row (" + outcome.Note + ")";
                if (outcome.NotApplicable) notApplicable++;
                else if (outcome.NotRun) notRun++;
                else if (outcome.Ok) ok++;
                else failed++;
                var judged = !outcome.NotRun && !outcome.NotApplicable;
                // Each of these is computed here from independent facts, never copied from the task's own pass/fail.
                var sampleFalseSuccess = judged && outcome.ServerSaidSuccess && !outcome.Ok;
                // Either the effect was seen yet no tool result reported input_sent, or some call said nothing was done but input_sent=true.
                var sampleInputMismatch = (judged && outcome.Ok && !inputSent) || mismatchCalls.Count > 0;
                // The task's own folder check, or any change to an existing window of the driven apps (counted for every sample:
                // touching the owner's window matters even when the sample itself was not run).
                var sampleWrongTarget = outcome.WrongTarget || baselineChanges.Count > 0;
                if (sampleFalseSuccess) falseSuccess++;
                if (sampleWrongTarget) wrongTarget++;
                if (sampleInputMismatch) inputSentMismatch++;
                leftoverWindows += leftoverTitles.Count;

                // The sample removes only the temp folder it created itself (recorded by its exact path), after its checks are done.
                string? folderLeft = null;
                try
                {
                    var tempRoot = Path.GetFullPath(Path.GetTempPath());
                    var full = Path.GetFullPath(temp);
                    if (full.StartsWith(Path.Combine(tempRoot, "OmniWin-test-" + id + "-"), StringComparison.OrdinalIgnoreCase) && Directory.Exists(full))
                        Directory.Delete(full, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    folderLeft = temp;
                    tempFolders.Add(temp);
                    cleanupErrors.Add("temp folder not removed: " + ex.Message);
                }

                notes.Add(outcome.Note + (leftover > 0 ? $" | {leftover} leftover msedge.exe of this sample stopped" : ""));
                samples.Add(new
                {
                    run = i + 1, status, input_sent = inputSent, note = outcome.Note,
                    false_success = sampleFalseSuccess, wrong_target = sampleWrongTarget, wrong_target_evidence = baselineChanges,
                    input_sent_mismatch = sampleInputMismatch, input_sent_mismatch_calls = mismatchCalls,
                    leftover_windows = leftoverTitles, other_new_windows_not_touched = otherNew, cleanup_errors = cleanupErrors,
                    temp_folder_left = folderLeft, data_folder_left = dataFolderLeft, leftover_browser_processes = leftover,
                    tool_calls = ctxCalls, seconds = Math.Round(sw.Elapsed.TotalSeconds, 2),
                });
                Console.WriteLine($"{id} #{i + 1}: {status}");
            }

            var counted = ok + failed;
            results.Add(new
            {
                id, task = what, runs, passed = ok, failed, not_run = notRun, not_applicable = notApplicable,
                false_success = falseSuccess, wrong_target = wrongTarget, input_sent_mismatch = inputSentMismatch, leftover_windows = leftoverWindows,
                success_percent = counted == 0 ? (double?)null : Math.Round(100.0 * ok / counted, 1),
                success_of = counted,
                wall_p50_s = wall.Count == 0 ? (double?)null : Stats.Percentile(wall.ToArray(), 0.5), wall_p95_s = wall.Count == 0 ? (double?)null : Stats.Percentile(wall.ToArray(), 0.95),
                tool_calls_per_run = runs == 0 ? 0 : (double)calls / runs, chars_returned_per_run = runs == 0 ? 0 : (double)chars / runs,
                notes = notes.Distinct().Take(5),
                samples,
            });
            Console.WriteLine($"{id}: passed {ok}, failed {failed}, not run {notRun}, not applicable {notApplicable} of {runs}");
            if (abortReason is not null) break;
        }

        var path = await WriteReportAsync(root, stamp, started, runs, results, abortReason, tempFolders);
        Console.WriteLine("wrote " + path);
        if (abortReason is not null) Console.Error.WriteLine(abortReason);
        return abortReason is null ? 0 : 3;
    }

    private static async Task<string> WriteReportAsync(string root, string stamp, DateTimeOffset started, int runs, List<object> results, string? notRunReason, List<string>? tempFolders = null)
    {
        var dir = Path.Combine(root, "bench", "reports");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{stamp}-omniwin-tasks-v1.json");
        for (var v = 2; File.Exists(path); v++) path = Path.Combine(dir, $"{stamp}-omniwin-tasks-v{v}.json");
        var doc = new
        {
            started,
            finished = DateTimeOffset.Now,
            remote_session = DesktopAvailability.IsRemoteSession(),
            status = notRunReason ?? "ran",
            runs_per_task = runs,
            windows_mcp = "not measured: needs a download the owner has not approved",
            test_folders_left_for_owner = tempFolders ?? [],
            results,
        };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    /// <summary>
    /// Returns null when a connected desktop is attached and OmniWin can read it; otherwise the reason the run must not start.
    /// Checks the input desktop and the screen size (Win32), then asks a short-lived server for windows_list.
    /// </summary>
    private static async Task<string?> PreflightAsync(string exe)
    {
        if (!DesktopAvailability.IsAvailable(out var reason)) return reason;
        var dataDir = Paths.NewDataDir();
        var server = await McpServer.StartAsync(exe, dataDir, null, new Dictionary<string, string> { ["OMNIWIN_BROWSER_HEADLESS"] = "1" });
        try
        {
            var r = await server.ToolAsync("windows_list", new { }, 30);
            if (r.IsError || r.Text.Contains("engine_timeout", StringComparison.Ordinal))
                return "windows_list failed or timed out (" + r.FirstLine + ")";
            var lines = WindowLineRegex().Matches(r.Text);
            if (lines.Count == 0) return "windows_list returned no windows";
            foreach (Match m in lines)
                if (m.Groups[2].Value.Equals("Taskmgr.exe", StringComparison.OrdinalIgnoreCase) && m.Groups[5].Value.Contains("foreground", StringComparison.Ordinal))
                    return "Task Manager is the foreground window and blocks focus changes";
            return null;
        }
        finally
        {
            await server.DisposeAsync();
            await Paths.RemoveOwnDataDirAsync(dataDir);   // the preflight's own data folder
        }
    }

    // ---------- helpers ----------

    private static string? Ref(string obs, string role, string nameContains) =>
        ElementRegex().Matches(obs).Where(m => m.Groups[2].Value == role && m.Groups[3].Value.Contains(nameContains, StringComparison.OrdinalIgnoreCase)).Select(m => m.Groups[1].Value).FirstOrDefault();

    private static long Gen(string obs) => long.Parse(GenRegex().Match(obs).Groups[1].Value);

    /// <summary>Polls the harness's own check until it holds or the limit passes. The only place this script waits.</summary>
    private static async Task<bool> PollAsync(Func<Task<bool>> check, int limitMs = 8000, int stepMs = 250)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            if (await check()) return true;
            if (sw.ElapsedMilliseconds >= limitMs) return false;
            await Task.Delay(stepMs);
        }
    }

    /// <summary>The current window list. <paramref name="count"/> false for the harness's own bookkeeping (snapshot, baseline check, cleanup), which is not a task call.</summary>
    private static async Task<Dictionary<string, (string Process, string Title, string Flags)>> WindowsAsync(Ctx c, bool count = true)
    {
        var r = count ? await c.Tool("windows_list", new { }) : await c.Server.ToolAsync("windows_list", new { });
        var d = new Dictionary<string, (string, string, string)>();
        foreach (Match m in WindowLineRegex().Matches(r.Text)) d[m.Groups[1].Value] = (m.Groups[2].Value, m.Groups[4].Value, m.Groups[5].Value.Trim());
        return d;
    }

    /// <summary>
    /// The one new window of <paramref name="process"/> (optionally with <paramref name="titleContains"/> in its title) that appeared
    /// after this sample's launch. app_launch reports no process id, so a window is adopted only when it is the single new
    /// candidate: with two or more (one could be the owner's), none is adopted and the sample is not run.
    /// </summary>
    private static async Task<string?> NewWindowAsync(Ctx c, HashSet<string> before, string process, int limitMs = 10000, string? titleContains = null)
    {
        List<string> found = [];
        await PollAsync(async () =>
        {
            found = (await WindowsAsync(c))
                .Where(w => !before.Contains(w.Key) && w.Value.Title != "Taskbar"
                    && w.Value.Process.Equals(process, StringComparison.OrdinalIgnoreCase)
                    && (titleContains is null || w.Value.Title.Contains(titleContains, StringComparison.OrdinalIgnoreCase)))
                .Select(w => w.Key).ToList();
            return found.Count > 0;
        }, limitMs);
        if (found.Count > 1)
            throw new NotRunException($"{found.Count} new {process} windows appeared after the launch; none was adopted, because one could be the owner's");
        return found.FirstOrDefault();
    }

    /// <summary>Launches a fresh Notepad window and returns its window id. Not run when Notepad did not open a new window or restored earlier text.</summary>
    private static async Task<string> LaunchNotepad(Ctx c)
    {
        var before = (await WindowsAsync(c)).Keys.ToHashSet();
        var handles = NotepadHandles();
        await c.Tool("app_launch", new { app = "Notepad" });
        var id = await NewWindowAsync(c, before, "Notepad.exe") ?? throw new NotRunException("Notepad opened no new window (it may have reused a tab in an existing window)");
        List<IntPtr> added = [];
        await PollAsync(() =>
        {
            added = NotepadHandles().Except(handles).ToList();
            return Task.FromResult(added.Count > 0);
        });
        if (added.Count > 1) throw new NotRunException($"{added.Count} new Notepad windows appeared through Win32; none was adopted");
        var hwnd = added.FirstOrDefault();
        if (hwnd == IntPtr.Zero) throw new NotRunException("the new Notepad window was not found through Win32");
        // The window must be ready (an editor control is listed) and empty before the task types.
        string obs = "";
        var ready = await PollAsync(async () =>
        {
            obs = await c.Obs(id);
            return (Ref(obs, "document", "") ?? Ref(obs, "edit", "")) is not null;
        });
        if (!ready) throw new NotRunException("the new Notepad window showed no editor control in time");
        if (ReadNotepadText(hwnd) is { Length: > 0 } restored)
            throw new NotRunException($"Notepad restored earlier text ({restored.Length} chars); the harness does not change text it did not type");
        c.NotepadWindow = id;
        c.NotepadHandle = hwnd;
        c.Own.Add(id);   // opened by this sample and checked empty: the only Notepad window cleanup may empty or close
        return id;
    }

    private static async Task<(string Ref, long Gen)> NotepadEditor(Ctx c)
    {
        var obs = await c.Obs(c.NotepadWindow!);
        var r = Ref(obs, "document", "") ?? Ref(obs, "edit", "") ?? throw new NotRunException("Notepad editor control disappeared");
        return (r, Gen(obs));
    }

    /// <summary>The Explorer window for the task: always a new one the sample opened itself. An existing window is never used.</summary>
    private static async Task<string> EnsureExplorer(Ctx c)
    {
        var before = await WindowsAsync(c);
        var shellBefore = ShellFolders().Keys.ToHashSet();
        await c.Tool("app_launch", new { app = "File Explorer" });
        var id = await NewWindowAsync(c, before.Keys.ToHashSet(), "explorer.exe", 6000);
        // Never fall back to a File Explorer window that was already open: that one is the owner's.
        if (id is null) throw new NotRunException("File Explorer opened no new window (an existing window is never used)");
        c.ExplorerWindow = id;
        c.ExplorerCreated = true;
        c.Own.Add(id);
        // The same new window through the Shell, so the folder checks look at this window only, never at the owner's.
        List<long> newShell = [];
        await PollAsync(() =>
        {
            newShell = ShellFolders().Keys.Where(h => !shellBefore.Contains(h)).ToList();
            return Task.FromResult(newShell.Count > 0);
        }, 6000);
        if (newShell.Count != 1)
            throw new NotRunException($"the new File Explorer window was not found through the Shell ({newShell.Count} new Shell windows); nothing was sent to it");
        c.ExplorerHandle = newShell[0];
        await c.Tool("window_manage", new { window = id, action = "focus" });
        return id;
    }

    /// <summary>Goes to a folder the way a person does: Ctrl+L, type the path into the address bar (never set_value), Enter.</summary>
    private static async Task<string> ExplorerDiag(Ctx c, string obs)
    {
        var title = (await WindowsAsync(c)).TryGetValue(c.ExplorerWindow ?? "", out var w) ? w.Title : "(window gone)";
        var items = ElementRegex().Matches(obs).Where(m => m.Groups[2].Value == "listitem").Select(m => m.Groups[3].Value).ToList();
        return $"explorer title \"{title}\", {items.Count} list items [{string.Join("; ", items.Take(6))}], observation {obs.Length} chars";
    }

    /// <summary>
    /// Goes to the sample's own test folder and proves, through the Shell (not OmniWin), that the sample's own Explorer window is
    /// there. When OmniWin's typing of the path failed, or the window did not get there afterwards, the sample is failed (OmniWin
    /// already acted) and nothing further is sent: the next keys would land in some other folder. Returns the result of typing the path.
    /// </summary>
    private static async Task<ToolResult> GoToFolder(Ctx c, string folder)
    {
        var w = c.ExplorerWindow!;
        await c.Tool("desktop_keys", new { keys = "ctrl+l", window = w });
        var obs = await c.Obs(w);
        var address = Ref(obs, "edit", "Address") ?? Ref(obs, "combobox", "Address") ?? throw new NotRunException("no address bar in Explorer");
        var typed = await c.Tool("desktop_act", new { @ref = address, gen = Gen(obs), action = "type", value = folder });
        if (typed.Status is not ("confirmed" or "unverified"))
            throw new TaskFailedException("OmniWin could not type the folder path into the address bar (" + typed.FirstLine + "); nothing further was sent");
        await c.Tool("desktop_keys", new { keys = "enter", window = w });
        var there = await PollAsync(() => Task.FromResult(ExplorerAt(c, folder)), 6000);
        if (!there) throw new TaskFailedException("the sample's own File Explorer window did not reach its test folder after OmniWin typed the path; nothing further was sent");
        return typed;
    }

    /// <summary>The Shell windows that exist now: handle → the folders its tabs show (sorted, joined). Read through the Shell COM object, not OmniWin.</summary>
    private static Dictionary<long, string> ShellFolders()
    {
        var folders = new Dictionary<long, List<string>>();
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
            foreach (dynamic w in shell.Windows())
            {
                try
                {
                    long hwnd = Convert.ToInt64(w.HWND);
                    string url = w.LocationURL ?? "";
                    if (!folders.TryGetValue(hwnd, out var list)) folders[hwnd] = list = [];
                    list.Add(url);
                }
                catch { /* a window that closed meanwhile */ }
            }
        }
        catch { /* no Shell */ }
        return folders.ToDictionary(f => f.Key, f => string.Join("|", f.Value.Order(StringComparer.OrdinalIgnoreCase)));
    }

    /// <summary>The Shell windows of the sample's own File Explorer window (by its handle), as live COM objects.</summary>
    private static IEnumerable<dynamic> OwnShellWindows(Ctx c)
    {
        var list = new List<dynamic>();
        if (c.ExplorerHandle == 0) return list;
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
            foreach (dynamic w in shell.Windows())
            {
                try
                {
                    if (Convert.ToInt64(w.HWND) == c.ExplorerHandle) list.Add(w);
                }
                catch { /* a window that closed meanwhile */ }
            }
        }
        catch { /* no Shell */ }
        return list;
    }

    private static bool IsFolder(string url, string folder) =>
        url.StartsWith("file:", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Path.TrimEndingDirectorySeparator(new Uri(url).LocalPath), Path.TrimEndingDirectorySeparator(folder), StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the sample's own File Explorer window is showing exactly <paramref name="folder"/> (read through the Shell, not OmniWin).</summary>
    private static bool ExplorerAt(Ctx c, string folder)
    {
        foreach (var w in OwnShellWindows(c))
        {
            try
            {
                if (IsFolder((string)w.LocationURL, folder)) return true;
            }
            catch { /* not a folder window */ }
        }

        return false;
    }

    // ---------- Win32 reads the harness does itself (independent of OmniWin) ----------

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumWindows(EnumProc proc, IntPtr param);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc proc, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr wParam, StringBuilder lParam);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(IntPtr h);
    private delegate bool EnumProc(IntPtr h, IntPtr p);

    /// <summary>A short hash of a window's text, so a change can be seen without keeping the text itself.</summary>
    private static string Fingerprint(string? text) =>
        text is null ? "(none)" : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

    /// <summary>Every top-level window of class "Notepad" that exists now.</summary>
    private static List<IntPtr> NotepadHandles()
    {
        var list = new List<IntPtr>();
        EnumWindows((h, _) =>
        {
            var cls = new StringBuilder(64);
            GetClassName(h, cls, 64);
            if (cls.ToString() == "Notepad") list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>Reads one Notepad window's text straight from its editor (WM_GETTEXT), not through OmniWin.</summary>
    private static string? ReadNotepadText(IntPtr top)
    {
        if (top == IntPtr.Zero) return null;
        string? text = null;
        EnumChildWindows(top, (h, _) =>
        {
            var cls = new StringBuilder(64);
            GetClassName(h, cls, 64);
            if (!cls.ToString().StartsWith("RichEdit", StringComparison.Ordinal) && cls.ToString() != "Edit") return true;
            var len = (int)SendMessage(h, 0x000E, IntPtr.Zero, IntPtr.Zero);   // WM_GETTEXTLENGTH
            var sb = new StringBuilder(len + 2);
            SendMessage(h, 0x000D, (IntPtr)(len + 1), sb);                      // WM_GETTEXT
            text = sb.ToString();
            return false;
        }, IntPtr.Zero);
        return text;
    }

    // ---------- the tasks ----------

    private static async Task<Outcome> B01(Ctx c)
    {
        await LaunchNotepad(c);
        var (doc, gen) = await NotepadEditor(c);
        var typed = await c.Tool("desktop_act", new { @ref = doc, gen, action = "type", value = "line one" });
        var enter = await c.Tool("desktop_keys", new { keys = "enter", window = c.NotepadWindow });
        (doc, gen) = await NotepadEditor(c);
        var typed2 = await c.Tool("desktop_act", new { @ref = doc, gen, action = "type", value = "line two" });
        var read = ReadNotepadText(c.NotepadHandle);
        var ok = read is not null && read.Replace("\r", "").TrimEnd() == "line one\nline two";
        var late = ok ? null : await ReadLaterAsync(c.NotepadHandle);
        string Line(ToolResult r) => string.Join(" | ", r.Text.Split((char)10).Take(3));
        return new Outcome(ok, ok ? "ok" : $"harness read: {JsonSerializer.Serialize(read ?? "(none)")}; read 1s later: {JsonSerializer.Serialize(late ?? "(none)")}; type1: {Line(typed)}; enter: {Line(enter)}; type2: {Line(typed2)}", typed.Status is "confirmed");
    }

    private static async Task<string?> ReadLaterAsync(IntPtr hwnd)
    {
        await Task.Delay(1000);
        return ReadNotepadText(hwnd);
    }

    private static string? DialogId(string observation) =>
        Regex.Match(observation, @"^dialog (w[0-9]+) ", RegexOptions.Multiline) is { Success: true } m ? m.Groups[1].Value : null;

    private static async Task<Outcome> B02(Ctx c)
    {
        await LaunchNotepad(c);
        var (doc, gen) = await NotepadEditor(c);
        await c.Tool("desktop_act", new { @ref = doc, gen, action = "type", value = "saved by B02" });
        await c.Tool("desktop_keys", new { keys = "ctrl+shift+s", window = c.NotepadWindow, expect = new { condition = new { kind = "window_opened", title_contains = "Save" }, timeout_ms = 5000 } });
        var dialog = "";
        string? name = null;
        await PollAsync(async () =>
        {
            dialog = await c.Obs(c.NotepadWindow!, "dialog");
            name = Ref(dialog, "edit", "File name") ?? Ref(dialog, "combobox", "File name");
            return name is not null;
        });
        if (name is null) throw new NotRunException("no File name box in the Save As dialog (the dialog did not open or is not readable)");
        var path = Path.Combine(c.TempDir, "b02.txt");
        // The File name box ignores set_value for what it saves: focus it, select what is there, and type the path like a person.
        var dlg = DialogId(dialog) ?? throw new NotRunException("the Save As dialog has no window id");
        await c.Tool("desktop_act", new { @ref = name, gen = Gen(dialog), action = "focus" });
        await c.Tool("desktop_keys", new { keys = "ctrl+a", window = dlg });
        dialog = await c.Obs(c.NotepadWindow!, "dialog");
        name = Ref(dialog, "edit", "File name") ?? Ref(dialog, "combobox", "File name") ?? name;
        var typed = await c.Tool("desktop_act", new { @ref = name, gen = Gen(dialog), action = "type", value = path });
        var after = await c.Obs(c.NotepadWindow!, "dialog");
        var seen = Regex.Match(after, @"^\s*e[0-9]+ (?:edit|combobox) ""File name"".*?value=""(.*?)""", RegexOptions.Multiline).Groups[1].Value;
        await c.Tool("desktop_keys", new { keys = "enter", window = dlg });
        var saved = await PollAsync(() => Task.FromResult(File.Exists(path)), 5000);
        var ok = saved && File.ReadAllText(path).Contains("saved by B02", StringComparison.Ordinal);
        c.NotepadSaved = saved;
        return new Outcome(ok, ok ? "ok" : $"file missing or content differs (File name box read back: \"{seen}\", typed status {typed.Status}, file content {(File.Exists(path) ? System.Text.Json.JsonSerializer.Serialize(File.ReadAllText(path)) : "none")})", typed.Status is "confirmed");
    }

    private static async Task<Outcome> B03(Ctx c)
    {
        await LaunchNotepad(c);
        var (doc, gen) = await NotepadEditor(c);
        await c.Tool("desktop_act", new { @ref = doc, gen, action = "type", value = "to be cleared" });
        var r = await c.Tool("desktop_keys", new { keys = "ctrl+a backspace", window = c.NotepadWindow });
        var read = ReadNotepadText(c.NotepadHandle);
        var ok = read is not null && read.Length == 0;
        return new Outcome(ok, ok ? "ok" : $"harness read: '{read}'", r.Status is "confirmed");
    }

    private static async Task<Outcome> B04(Ctx c)
    {
        var seed = Path.Combine(c.TempDir, "seed");
        Directory.CreateDirectory(Path.Combine(seed, "sub"));
        foreach (var n in new[] { "alpha.txt", "beta.txt", "gamma.txt" }) File.WriteAllText(Path.Combine(seed, "sub", n), n);
        await EnsureExplorer(c);
        var typedPath = await GoToFolder(c, Path.Combine(seed, "sub"));
        var obs = "";
        var names = await PollAsync(async () =>
        {
            obs = await c.Obs(c.ExplorerWindow!);
            // Explorer may hide known extensions, so a file is listed under "alpha" or "alpha.txt".
            return new[] { "alpha", "beta", "gamma" }.All(n => ElementRegex().Matches(obs).Any(m => m.Groups[2].Value == "listitem" && (m.Groups[3].Value == n || m.Groups[3].Value == n + ".txt")));
        }, 6000);
        // The server's own claim for this task is the typed path; the listing is the harness's independent check.
        return new Outcome(names, names ? "ok" : "not all seeded names were listed: " + await ExplorerDiag(c, obs), typedPath.Status is "confirmed",
            WrongTarget: !ExplorerAt(c, Path.Combine(seed, "sub")));   // the window left the sample's folder during the sample
    }

    private static async Task<Outcome> B05(Ctx c)
    {
        var parent = Path.Combine(c.TempDir, "b05");
        Directory.CreateDirectory(parent);
        await EnsureExplorer(c);
        await GoToFolder(c, parent);   // not_run when Explorer is not in the sample's own folder: no key is sent anywhere else
        await c.Tool("desktop_keys", new { keys = "ctrl+shift+n", window = c.ExplorerWindow });
        var made = await PollAsync(() => Task.FromResult(Directory.GetDirectories(parent).Length > 0), 4000);
        var r = await c.Tool("desktop_keys", new { keys = "enter", window = c.ExplorerWindow });   // accept the default name
        if (!made) return new Outcome(false, "no folder created", c.LastEffectConfirmed);
        // Rename it: F2, type the new name into the rename box, Enter, then read the folder name on disk.
        const string newName = "b05-renamed";
        await c.Tool("desktop_keys", new { keys = "f2", window = c.ExplorerWindow });
        var obs = "";
        string? box = null;
        await PollAsync(async () =>
        {
            obs = await c.Obs(c.ExplorerWindow!);
            box = Ref(obs, "edit", "New folder");   // the rename box is named after the item being renamed
            return box is not null;
        }, 4000);
        if (box is null) return new Outcome(false, "no rename box appeared after F2: " + await ExplorerDiag(c, obs) + "; edits: " + string.Join("; ", ElementRegex().Matches(obs).Where(m => m.Groups[2].Value is "edit" or "combobox" or "document").Select(m => m.Groups[2].Value + " " + m.Groups[3].Value)), c.LastEffectConfirmed);
        var typed = await c.Tool("desktop_act", new { @ref = box, gen = Gen(obs), action = "type", value = newName });
        await c.Tool("desktop_keys", new { keys = "enter", window = c.ExplorerWindow });
        var renamed = await PollAsync(() => Task.FromResult(Directory.Exists(Path.Combine(parent, newName))), 4000);
        return new Outcome(renamed, renamed ? "ok (renamed folder found on disk)" : "the renamed folder is not on disk (found: " + string.Join(", ", Directory.GetDirectories(parent).Select(Path.GetFileName)) + "; type " + typed.Status + ")", typed.Status is "confirmed",
            WrongTarget: !ExplorerAt(c, parent));   // the window left the sample's folder during the sample
    }

    /// <summary>The selected item of the sample's own Explorer window while it shows <paramref name="folder"/>, read through the Shell COM object (not through OmniWin).</summary>
    private static string? ExplorerSelection(Ctx c, string folder)
    {
        foreach (var w in OwnShellWindows(c))
        {
            try
            {
                if (!IsFolder((string)w.LocationURL, folder)) continue;
                dynamic selected = w.Document.SelectedItems();
                if (selected.Count == 1) return (string)selected.Item(0).Name;
                return $"({selected.Count} selected)";
            }
            catch { /* not a folder window */ }
        }

        return null;
    }

    private static async Task<Outcome> B06(Ctx c)
    {
        var folder = Path.Combine(c.TempDir, "b06");
        Directory.CreateDirectory(folder);
        for (var i = 1; i <= 2000; i++) File.WriteAllBytes(Path.Combine(folder, $"file-{i:D4}.dat"), []);
        await EnsureExplorer(c);
        await GoToFolder(c, folder);
        var obs = "";
        string? first = null;
        await PollAsync(async () =>
        {
            obs = await c.Obs(c.ExplorerWindow!);
            first = ElementRegex().Matches(obs).Where(m => m.Groups[2].Value == "listitem" && m.Groups[3].Value.StartsWith("file-", StringComparison.Ordinal)).Select(m => m.Groups[1].Value).FirstOrDefault();
            return first is not null;
        }, 6000);
        if (first is null) return new Outcome(false, "the folder list did not appear: " + await ExplorerDiag(c, obs), c.LastEffectConfirmed);
        // The list is virtualized (only the visible rows exist), so go to the end like a person would; file-1990 is then among the last rows.
        await c.Tool("desktop_act", new { @ref = first, gen = Gen(obs), action = "click" });
        await c.Tool("desktop_keys", new { keys = "end", window = c.ExplorerWindow });
        string? item = null;
        await PollAsync(async () =>
        {
            obs = await c.Obs(c.ExplorerWindow!);
            item = ElementRegex().Matches(obs).Where(m => m.Groups[2].Value == "listitem" && m.Groups[3].Value.StartsWith("file-1990", StringComparison.Ordinal)).Select(m => m.Groups[1].Value).FirstOrDefault();
            return item is not null;
        }, 6000);
        if (item is null) return new Outcome(false, "file-1990 not listed after End: " + await ExplorerDiag(c, obs), c.LastEffectConfirmed);
        // An item at the edge of the viewport may be partly covered: bring it fully into view first (a click on a covered item is refused).
        await c.Tool("desktop_act", new { @ref = item, gen = Gen(obs), action = "scroll_into_view" });
        obs = await c.Obs(c.ExplorerWindow!);
        item = ElementRegex().Matches(obs).Where(m => m.Groups[2].Value == "listitem" && m.Groups[3].Value.StartsWith("file-1990", StringComparison.Ordinal)).Select(m => m.Groups[1].Value).FirstOrDefault() ?? item;
        var beforeClick = ExplorerSelection(c, folder);
        var r = await c.Tool("desktop_act", new { @ref = item, gen = Gen(obs), action = "select" });   // the list item's own Select action
        string? selected = null;
        var ok = await PollAsync(() => Task.FromResult((selected = ExplorerSelection(c, folder)) is { } n && n.StartsWith("file-1990", StringComparison.Ordinal)), 4000);
        return new Outcome(ok, ok ? "ok (selection read through the Shell, not OmniWin)" : $"selection after select was \"{selected ?? "unreadable"}\" (before the select: \"{beforeClick ?? "unreadable"}\"; select status {r.Status}: {r.Text.Split((char)10)[0]})", r.Status is "confirmed",
            WrongTarget: !ExplorerAt(c, folder));   // the window left the sample's folder during the sample
    }

    private static async Task<Outcome> B07(Ctx c)
    {
        var before = (await WindowsAsync(c)).Keys.ToHashSet();
        await c.Tool("app_launch", new { app = "Settings", expect = new { condition = new { kind = "window_opened", title_contains = "Settings" }, timeout_ms = 8000 } });
        // Settings runs inside ApplicationFrameHost.exe; its window is the new "Settings" one.
        var win = await NewWindowAsync(c, before, "ApplicationFrameHost.exe", titleContains: "Settings") ?? throw new NotRunException("Settings opened no new window");
        c.Own.Add(win);
        await c.Tool("window_manage", new { window = win, action = "maximize" });
        var obs = "";
        // The Settings window opens on Home: choose System in the navigation list, then the Display entry.
        string? display = null;
        await PollAsync(async () =>
        {
            obs = await c.Obs(win);
            display = Ref(obs, "button", "Display") ?? Ref(obs, "listitem", "Display") ?? Ref(obs, "link", "Display");
            if (display is not null) return true;
            if (Ref(obs, "listitem", "System") is { } system) await c.Tool("desktop_act", new { @ref = system, gen = Gen(obs), action = "click" });
            else if (Ref(obs, "button", "Open Navigation") is { } nav) await c.Tool("desktop_act", new { @ref = nav, gen = Gen(obs), action = "click" });
            return false;
        }, 15000, 800);
        if (display is null) return new Outcome(false, "no Display entry in Settings", c.LastEffectConfirmed);
        // A page that is still animating can make OmniWin refuse a click on a covered item (blocked, nothing sent): look again and retry, like a person.
        var displayClick = await c.Tool("desktop_act", new { @ref = display, gen = Gen(obs), action = "click" });
        for (var retry = 0; retry < 4 && displayClick.Status is "blocked" or "stale"; retry++)
        {
            await Task.Delay(700);
            obs = await c.Obs(win);
            display = Ref(obs, "button", "Display") ?? Ref(obs, "listitem", "Display") ?? Ref(obs, "link", "Display") ?? display;
            // The list item's own Invoke action does not depend on a pointer hit test.
            displayClick = await c.Tool("desktop_act", new { @ref = display, gen = Gen(obs), action = retry == 0 ? "click" : "invoke" });
        }
        // The Display page can take a few seconds to draw the first time Settings runs.
        await PollAsync(async () => (obs = await c.Obs(win)).Contains("Scale & layout", StringComparison.Ordinal), 15000, 500);
        var expected = (int)Math.Round(GetDpiForSystem() / 96.0 * 100);   // the harness's own reading of the scale
        var ok = await PollAsync(async () => (obs = await c.Obs(win)).Contains($"{expected}%", StringComparison.Ordinal), 8000);
        // Under Remote Desktop Windows disables the Scale box and shows no percentage at all: the task cannot be judged there.
        // It is recorded as not_applicable (the owner uses Remote Desktop only), separate from passed, failed and not_run.
        // Windows itself must say this is a Remote Desktop session (SM_REMOTESESSION): on a console a disabled Scale box
        // (custom scaling, a policy) or an unreadable box is a failure, never "not applicable".
        var remote = DesktopAvailability.IsRemoteSession();
        if (!ok && remote && obs.Contains("Scale & layout", StringComparison.Ordinal) &&obs.Contains("combobox \"Scale\" disabled", StringComparison.Ordinal) && !Regex.IsMatch(obs, @"[0-9]{3}%"))
            throw new NotApplicableException("over Remote Desktop Windows disables the Scale box and shows no percentage (the Display page was reached; SM_REMOTESESSION is set)");
        return new Outcome(ok, ok ? "ok" : $"Settings text did not show {expected}% (display click: {displayClick.Text.Split((char)10)[0]}; remote-session={remote}; page has Scale&layout={obs.Contains("Scale & layout", StringComparison.Ordinal)}, scale-disabled={obs.Contains("combobox \"Scale\" disabled", StringComparison.Ordinal)}, percents=[{string.Join(",", Regex.Matches(obs, @"[0-9]+%").Select(m => m.Value))}])", displayClick.Status is "confirmed");
    }

    private static async Task<Outcome> B09(Ctx c)
    {
        c.Web.Clear();
        await c.Tool("browser_nav", new { action = "navigate", url = c.Web.Url("w01.html") });
        foreach (var (element, action, value) in new (string, string, string?)[] { ("Full text", "set_value", "Ada"), ("Email address", "set_value", "ada@example.invalid"), ("Choice", "select", "beta"), ("I agree", "check", null) })
        {
            var obs = (await c.Tool("browser_observe", new { })).Text;
            var args = new Dictionary<string, object?> { ["ref"] = BRef(obs, element), ["gen"] = Gen2(obs), ["action"] = action };
            if (value is not null) args["value"] = value;
            await c.Tool("browser_act", args);
        }

        var last = (await c.Tool("browser_observe", new { })).Text;
        var click = await c.Tool("browser_act", new { @ref = BRef(last, "Continue"), gen = Gen2(last), action = "click" });
        var got = await PollAsync(() => Task.FromResult(c.Web.Forms.Count > 0), 3000);
        var form = got ? c.Web.Forms.LastOrDefault() : null;
        var ok = form is not null && JsonDocument.Parse(form).RootElement is { } r && r.GetProperty("text").GetString() == "Ada" && r.GetProperty("choice").GetString() == "beta" && r.GetProperty("agree").GetBoolean();
        return new Outcome(ok, ok ? "ok" : "the server's record of the form does not match", click.Status is "confirmed");
    }

    private static async Task<Outcome> B10(Ctx c)
    {
        // The task must copy through the clipboard, so the owner's clipboard text is saved first (by the harness, through Win32)
        // and put back afterwards. A clipboard that holds something other than text could not be put back: then nothing is touched.
        if (!ClipboardState.TryRead(out var original, out var why))
            throw new NotRunException("the clipboard could not be saved for restoring (" + why + "); nothing was touched");
        if (original is { Length: > 1_000_000 })
            throw new NotRunException("the clipboard text is longer than clipboard_set can put back; nothing was touched");
        try
        {
            await LaunchNotepad(c);
            var (doc, gen) = await NotepadEditor(c);
            await c.Tool("desktop_act", new { @ref = doc, gen, action = "type", value = "line to copy B10" });
            await c.Tool("desktop_keys", new { keys = "ctrl+a ctrl+c", window = c.NotepadWindow });
            var clip = await c.Tool("clipboard_get", new { });
            var text = clip.Text.Split('\n').Skip(2).FirstOrDefault() ?? "";
            c.Web.Clear();
            await c.Tool("browser_nav", new { action = "navigate", url = c.Web.Url("w01.html") });
            var page = (await c.Tool("browser_observe", new { })).Text;
            var set = await c.Tool("browser_act", new { @ref = BRef(page, "Notes"), gen = Gen2(page), action = "set_value", value = text });
            page = (await c.Tool("browser_observe", new { })).Text;
            await c.Tool("browser_act", new { @ref = BRef(page, "Continue"), gen = Gen2(page), action = "click" });
            var got = await PollAsync(() => Task.FromResult(c.Web.Forms.Count > 0), 3000);
            var form = got ? c.Web.Forms.LastOrDefault() : null;
            var ok = form is not null && JsonDocument.Parse(form).RootElement.GetProperty("notes").GetString() == "line to copy B10";
            return new Outcome(ok, ok ? "ok" : "the form record does not hold the copied line", set.Status is "confirmed");
        }
        finally
        {
            // Put the owner's clipboard back (a harness call, not counted as a task call).
            if (original is null)
            {
                if (!ClipboardState.Empty()) c.CleanupErrors.Add("the clipboard was empty before B10 and could not be emptied again");
            }
            else
            {
                var restored = await c.Server.ToolAsync("clipboard_set", new { text = original });
                if (restored.Status is not "confirmed")
                    c.CleanupErrors.Add("the owner's clipboard text could not be put back: " + restored.FirstLine);
            }
        }
    }

    private static string BRef(string obs, string contains) =>
        obs.Split('\n').FirstOrDefault(l => l.Contains(contains, StringComparison.Ordinal) && l.TrimStart().StartsWith('b'))?.Trim().Split(' ')[0]
        ?? throw new NotRunException($"the fixture page showed no '{contains}' control");

    private static long Gen2(string obs) => Gen(obs);
}
