using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OmniWin.Core;
using OmniWin.Workflows;

namespace OmniWin.Server.Hosting;

/// <summary>
/// Records single acts in the run log (contract §12) and puts the step id on the result's status line. It wraps the tool
/// calls that act (desktop_act, desktop_keys, desktop_scroll, browser_act, browser_nav and the system acts), so those tool
/// files need no change. Steps that run inside run_steps or workflow_run are recorded by the runner itself.
/// <para>
/// Register it after <see cref="ToolCallAudit.Filter"/> so the audit line, which is built from the final status line,
/// sees the <c>step=</c> token. Everything here is best effort: a failure to describe or record never changes the result.
/// </para>
/// </summary>
internal static class RunLogFilter
{
    /// <summary>The longest the pre-act element lookup may take. A slow lookup only means the step is not recorded.</summary>
    private static readonly TimeSpan PrepareLimit = TimeSpan.FromMilliseconds(750);

    internal const string PrepareMetric = "run_log_prepare";

    internal static McpRequestFilter<CallToolRequestParams, CallToolResult> Filter => next => async (request, ct) =>
    {
        var name = request.Params?.Name;
        var services = request.Services ?? request.Server.Services;
        var recorder = services?.GetService<StepRecorder>();
        var learning = services?.GetService<OmniWin.Server.Learning.LearningService>();
        if (name == "run_steps" && learning is not null)
        {
            // run_steps records its own steps; the open task only needs to know whether the whole batch passed.
            var batch = await next(request, ct).ConfigureAwait(false);
            var first = batch.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text;
            learning.NoteAct(first is not null && first.Split('\n', 2)[0].Contains(" status=confirmed ", StringComparison.Ordinal));
            return batch;
        }

        if (name is null || !StepTools.IsKnown(name) || recorder is null)
        {
            var other = await next(request, ct).ConfigureAwait(false);

            // Review 27, L3: a tool that is not in the run log but sent input (engine_call, selection_replace, text_insert, a quick
            // command through omniwin_do, a power tool) is a step no recipe can replay. workflow_run keeps its own books.
            if (learning is not null && name is not (null or "workflow_run") && SentInput(other)) learning.NoteUntrackedAct();
            return other;
        }

        var call = ToCall(name, request.Params?.Arguments);
        StepContext? context = null;
        if (call is not null)
        {
            var start = Stopwatch.GetTimestamp();
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(PrepareLimit);
            try
            {
                context = await recorder.PrepareAsync(call, limit.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                context = null;
            }

            services?.GetService<IMetrics>()?.Record(PrepareMetric, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        var result = await next(request, ct).ConfigureAwait(false);
        var recorded = false;
        if (call is null || context is null)
        {
            learning?.NoteAct(false);
            return result;
        }

        try
        {
            var index = result.Content.ToList().FindIndex(c => c is TextContentBlock);
            if (index >= 0 && result.Content[index] is TextContentBlock block)
            {
                var text = block.Text;
                if (StepRecorder.TryParseStatusLine(text, out var status, out _) &&
                    recorder.Record(call, context, status, StepRecorder.HasDiffLines(text), StepRecorder.ExpectLine(text)) is { } stepId)
                {
                    result.Content[index] = new TextContentBlock { Text = StepRecorder.WithStepToken(text, stepId) };
                    recorded = true;
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ArgumentException)
        {
            // Recording is best effort; the tool result stands as it was.
        }

        learning?.NoteAct(recorded);   // contract 2.6: an act that is not in the run log makes the open task "noisy"
        return result;
    };

    /// <summary>True when the result's first line says input was sent (or may have been).</summary>
    internal static bool SentInput(CallToolResult result)
    {
        var first = result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text?.Split('\n', 2)[0];

        // A recipe that omniwin_do ran itself answers with a workflow_run line: that replay is already on the books.
        return first is not null && !first.StartsWith("workflow_run ", StringComparison.Ordinal) &&
               first.Split(' ').Contains("input_sent=true", StringComparer.Ordinal);
    }

    /// <summary>Builds the call from the request's arguments, or null when they do not fit (the engine will report that itself).</summary>
    internal static StepCall? ToCall(string tool, IDictionary<string, JsonElement>? arguments)
    {
        try
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("tool", tool);
                if (arguments is not null)
                {
                    foreach (var (key, value) in arguments)
                    {
                        if (!string.Equals(key, "tool", StringComparison.Ordinal))
                        {
                            writer.WritePropertyName(key);
                            value.WriteTo(writer);
                        }
                    }
                }

                writer.WriteEndObject();
            }

            return JsonSerializer.Deserialize<StepCall>(stream.ToArray(), CoreJson.Options);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }
}
