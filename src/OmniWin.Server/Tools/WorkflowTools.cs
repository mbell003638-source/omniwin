using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using OmniWin.Core;
using OmniWin.Safety;
using OmniWin.Workflows;

namespace OmniWin.Server.Tools;

/// <summary>run_steps, workflow_list, workflow_save, workflow_run and wait_for (contract §8).</summary>
[McpServerToolType]
internal sealed class WorkflowTools(
    WorkflowService workflows,
    IKillSwitch killSwitch,
    OmniWinConfig config,
    IMetrics metrics,
    IDesktopEngine desktop,
    IServiceProvider services,
    OmniWin.Server.Learning.LearningService? learning = null)
{
    private const int StepSeconds = 40;
    private const int MaxToolSeconds = 300;

    [McpServerTool(Name = "run_steps", Title = "Run several steps", ReadOnly = false, Idempotent = false, Destructive = true)]
    [Description("Run several acts in order in one call. Each step is an object with a tool and that tool's own parameters: " +
        "{\"tool\":\"desktop_act\",\"ref\":\"e3\",\"gen\":5,\"action\":\"click\"}. Allowed tools: desktop_act, desktop_keys, desktop_scroll, " +
        "browser_act, browser_nav, app_launch, window_manage, audio, media, clipboard_set (clipboard_set takes text). " +
        "Every step goes through the normal checks and the safety gate, exactly as a single call. " +
        "stop_on_failure (default true) stops at the first step that did not pass; a step passes when it is confirmed, " +
        "or unverified with a non-empty diff. Refs and gen must come from observations you already made, so a step that " +
        "changes the screen can make the next step's ref stale. " + ToolDocs.RetryRules)]
    public Task<string> RunSteps(StepCall[] steps, CancellationToken ct, bool stop_on_failure = true) =>
        GuardedAsync("run_steps", steps?.Length ?? 1, async token =>
        {
            var outcome = await workflows.RunStepsAsync(steps ?? [], stop_on_failure, token).ConfigureAwait(false);
            return WorkflowFormatter.Format(outcome);
        }, ct);

    [McpServerTool(Name = "workflow_list", Title = "List saved workflows", ReadOnly = true, Idempotent = true, Destructive = false)]
    [Description("List saved workflows: name, description, number of steps and parameter names. Learned recipes also show trust " +
        "(candidate, trusted, disabled), runs, and first-run time against replay time. Read-only; sends no input.")]
    public Task<string> WorkflowList(CancellationToken ct) =>
        GuardedAsync("workflow_list", 1, _ =>
        {
            var text = WorkflowFormatter.Format(workflows.List());
            var learned = learning?.ListLines() ?? [];
            return Task.FromResult(learned.Count == 0 ? text : text + "\n" + string.Join('\n', learned));
        }, ct);

    [McpServerTool(Name = "workflow_save", Title = "Save steps as a workflow", ReadOnly = false, Idempotent = false, Destructive = false)]
    [Description("Save a range of already-run steps as a named workflow. from_step and to_step are the step=sN ids shown on act results " +
        "(both included). Typed and clipboard text is never saved: each step with text gets a placeholder (p1, p2 …), and params can rename " +
        "placeholders, for example {\"p1\":\"search_text\"}. Every step is checked against the safety gate first; a workflow with a step " +
        "the gate would deny is not saved. A name that already exists is refused; nothing is ever replaced or deleted. " +
        "Workflows are plain data (tool names and selectors), never code.")]
    public Task<string> WorkflowSave(string name, string description, string from_step, string to_step, CancellationToken ct,
        Dictionary<string, string>? @params = null) =>
        GuardedAsync("workflow_save", 1, _ =>
            Task.FromResult(WorkflowFormatter.Format(workflows.Save(name, description, from_step, to_step, @params))), ct);

    [McpServerTool(Name = "workflow_run", Title = "Run a saved workflow", ReadOnly = false, Idempotent = false, Destructive = true)]
    [Description("Run a saved workflow. Each step finds its element again from the saved selector, then acts through the normal checks " +
        "and the safety gate. params supplies a value for every parameter the workflow lists (see workflow_list). " +
        "It stops at the first step that did not pass and returns that step and a current observation. " + ToolDocs.UntrustedContent + " " +
        ToolDocs.RetryRules)]
    public Task<string> WorkflowRun(string name, CancellationToken ct, Dictionary<string, string>? @params = null) =>
        GuardedAsync("workflow_run", 8, async token =>
        {
            // A learned recipe keeps its books here: a clean run or a stop is counted, and a stop part-way opens a repair.
            var outcome = learning is null
                ? await workflows.RunWorkflowAsync(name, @params, token).ConfigureAwait(false)
                : await learning.RunAsync(name, @params, null, token).ConfigureAwait(false);
            return WorkflowFormatter.Format(outcome);
        }, ct);

    [McpServerTool(Name = "wait_for", Title = "Wait for a condition", ReadOnly = true, Idempotent = true, Destructive = false)]
    [Description("Wait until a condition is met or the timeout passes. The condition runs on the desktop or the browser depending on what it names: " +
        "url_contains, and refs or scopes starting with b or t, run on the browser; refs and scopes starting with e, o or w, and anything that names " +
        "nothing specific, run on the desktop. A condition that mixes both is refused: send one wait_for for each. " +
        "Example: {\"kind\":\"window_opened\",\"title_contains\":\"Save As\"}. timeout_ms defaults to 2000, max 30000. Read-only; sends no input. " +
        ToolDocs.UntrustedContent)]
    public async Task<string> WaitFor(Expectation condition, CancellationToken ct, int? timeout_ms = null)
    {
        var timeout = Math.Clamp(timeout_ms ?? config.Timeouts.DefaultExpectMs, 1, ExpectSpec.MaxTimeoutMs);
        return await GuardedAsync("wait_for", 1, async token =>
        {
            var start = Stopwatch.GetTimestamp();
            WaitResult result;
            string? detail = null;
            switch (ConditionRouter.Route(condition))
            {
                case WaitRoute.Mixed:
                    result = new WaitResult(false, ReasonCodes.UnsupportedAction, null, Timing.Total(0));
                    detail = "The condition mixes desktop and browser parts. Send one wait_for for each.";
                    break;
                case WaitRoute.Browser:
                    var browser = services.GetService<IBrowserEngine>();
                    if (browser is null)
                    {
                        result = new WaitResult(false, ReasonCodes.BrowserNotConnected, null, Timing.Total(0));
                        detail = "The browser engine is not available in this server.";
                    }
                    else
                    {
                        result = await browser.WaitForAsync(ConditionRouter.TabOf(condition), condition, timeout, token).ConfigureAwait(false);
                    }

                    break;
                default:
                    result = await desktop.WaitForAsync(condition, timeout, token).ConfigureAwait(false);
                    break;
            }

            metrics.Record("wait_for", Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            var text = ResultFormatter.Format(result);
            return detail is null ? text : text + "\ndetail: " + detail;
        }, ct, extraSeconds: timeout / 1000 + 5);
    }

    /// <summary>
    /// Runs a handler with the kill switch and a time limit linked to the call's token. It never throws: a stop, a timeout or a
    /// failure comes back as a result that says whether input was sent (for these tools the runner reports that itself; a
    /// failure here means nothing was started).
    /// </summary>
    private async Task<string> GuardedAsync(string op, int steps, Func<CancellationToken, Task<string>> handler, CancellationToken ct, int extraSeconds = 0)
    {
        var seconds = Math.Min(MaxToolSeconds, 10 + (StepSeconds * Math.Max(1, steps)) + extraSeconds);
        var start = Stopwatch.GetTimestamp();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, killSwitch.Token);
        cts.CancelAfter(TimeSpan.FromSeconds(seconds));
        try
        {
            var text = await handler(cts.Token).ConfigureAwait(false);
            metrics.Record(op, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            return text;
        }
        catch (OperationCanceledException)
        {
            // F6: a stop or a timeout in the middle of run_steps / workflow_run can come after some steps already sent input,
            // so those two report input_sent=true conservatively (contract 13.3). The other tools send no input.
            if (op is "run_steps" or "workflow_run")
            {
                return ResultFormatter.Format(new ActResult(ActStatus.Error, true, ReasonCodes.InputInterrupted,
                    killSwitch.IsTripped ? "OmniWin was stopped while steps were running; some input may already have been sent."
                        : $"{op} took longer than {seconds} s and was cancelled; some input may already have been sent.",
                    null, null, Timing.Total(0)));
            }

            return killSwitch.IsTripped
                ? ResultFormatter.Format(ActResult.Denied(ReasonCodes.KillSwitch, "OmniWin stop switch is tripped", Timing.Total(0)))
                : ResultFormatter.Format(ActResult.Failed(false, ReasonCodes.EngineTimeout, $"{op} took longer than {seconds} s and was cancelled.", Timing.Total(0)));
        }
        catch (Exception)
        {
            return op is "run_steps" or "workflow_run"
                ? ResultFormatter.Format(new ActResult(ActStatus.Error, true, ReasonCodes.InputInterrupted,
                    $"{op} failed part-way; some input may already have been sent.", null, null, Timing.Total(0)))
                : ResultFormatter.Format(ActResult.Failed(false, ReasonCodes.EngineTimeout, $"{op} could not be completed.", Timing.Total(0)));
        }
    }
}
