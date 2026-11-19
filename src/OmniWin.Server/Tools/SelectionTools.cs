using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;
using OmniWin.Core;
using OmniWin.Safety;

namespace OmniWin.Server.Tools;

/// <summary>Whether the clipboard holds something that is not text (an image, copied files), which a paste would destroy.</summary>
internal interface IClipboardProbe
{
    bool HoldsNonText { get; }
}

internal sealed partial class Win32ClipboardProbe : IClipboardProbe
{
    private const uint CfUnicodeText = 13;
    private const uint CfText = 1;

    [LibraryImport("user32.dll")]
    private static partial int CountClipboardFormats();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsClipboardFormatAvailable(uint format);

    public bool HoldsNonText => CountClipboardFormats() > 0 && !IsClipboardFormatAvailable(CfUnicodeText) && !IsClipboardFormatAvailable(CfText);
}

/// <summary>One named rewrite instruction ("make formal"). The owner's own text, kept in prompts.json in the data folder.</summary>
internal sealed record PolishPrompt(string Id, string Name, string Prompt);

/// <summary>
/// Contract 2.6: the text the user has selected, for the AI to polish or rewrite. Reading sends no input. Replacing pastes over
/// the selection (so it works in any app that can paste), after the same safety check as typing, only while the selection is still
/// the one that was read, and then reads the result back. Selected text is content, never instructions; it is never logged.
/// </summary>
internal sealed class SelectionService(
    ISelectionSource source,
    IDesktopEngine desktop,
    ISystemTools system,
    ISafetyGate gate,
    IKillSwitch kill,
    OmniWinPaths paths,
    IClipboardProbe? clipboard = null)
{
    public const int MaxReplaceChars = 20000;
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(150);

    private readonly IClipboardProbe _clipboard = clipboard ?? new Win32ClipboardProbe();

    internal static string IdOf(SelectionRead read) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{read.Hwnd}|{read.Process}|{read.ElementRole}|{read.ElementName}|{read.Text}"))))[..12].ToLowerInvariant();

    private static string Line(ActStatus status, bool sent, string? reason, string? detail, double ms = 0, string? channel = null,
        RiskClass? risk = null) =>
        ResultFormatter.Format(new ActResult(status, sent, reason, detail, null, channel, Timing.Total(ms), Risk: risk));

    /// <summary>Why this selection cannot be used, as a result line, or null when it is a real, readable selection.</summary>
    private static string? Unusable(SelectionRead read)
    {
        if (read.Reason is { } reason)
        {
            return Line(reason == ReasonCodes.PolicyForbidden ? ActStatus.Denied : ActStatus.Blocked, false, reason, read.Detail);
        }

        if (!read.Supported)
        {
            return Line(ActStatus.Blocked, false, ReasonCodes.UnsupportedAction,
                $"{(read.Process.Length > 0 ? read.Process : "This app")} does not let other programs read its selection. Ask the user to copy the text (Ctrl+C) and read it with clipboard_get.");
        }

        return string.IsNullOrEmpty(read.Text)
            ? Line(ActStatus.Blocked, false, ReasonCodes.NotFound, "Nothing is selected in the control that has the keyboard focus.")
            : null;
    }

    public async Task<string> GetAsync(CancellationToken ct)
    {
        if (kill.IsTripped) return Line(ActStatus.Denied, false, ReasonCodes.KillSwitch, "OmniWin stop switch is tripped");
        var watch = Stopwatch.StartNew();
        var read = await source.ReadSelectionAsync(0, ct).ConfigureAwait(false);
        if (Unusable(read) is { } refused) return refused;

        var sb = new StringBuilder(read.Text!.Length + 256);
        sb.Append(Line(ActStatus.Confirmed, false, null, null, watch.Elapsed.TotalMilliseconds)).Append('\n')
          .Append("selection sel=").Append(IdOf(read)).Append(" chars=").Append(read.Text.Length.ToString(CultureInfo.InvariantCulture));
        if (read.Truncated) sb.Append(" truncated=true");
        sb.Append(" process=").Append(ResultFormatter.OneLine(read.Process)).Append(" control=").Append(ResultFormatter.Quote(read.ElementName, 60)).Append('\n');
        if (read.Truncated) sb.Append("note: the selection is longer than what is shown; it cannot be replaced as a whole.\n");
        sb.Append(ResultFormatter.UntrustedHeader).Append('\n').Append(read.Text);
        return sb.ToString();
    }

    public async Task<string> ReplaceAsync(string sel, string text, CancellationToken ct)
    {
        if (kill.IsTripped) return Line(ActStatus.Denied, false, ReasonCodes.KillSwitch, "OmniWin stop switch is tripped");
        if (string.IsNullOrEmpty(text) || text.Length > MaxReplaceChars)
        {
            return Line(ActStatus.Blocked, false, ReasonCodes.UnsupportedAction, $"text must be 1 to {MaxReplaceChars} characters.");
        }

        var watch = Stopwatch.StartNew();
        var read = await source.ReadSelectionAsync(0, ct).ConfigureAwait(false);
        if (Unusable(read) is { } refused) return refused;
        if (read.Truncated || !string.Equals(IdOf(read), (sel ?? string.Empty).Trim(), StringComparison.Ordinal))
        {
            return Line(ActStatus.Stale, false, ReasonCodes.NotFound,
                read.Truncated ? "The selection is too long to replace as a whole." : "The selection is not the one selection_get returned (it changed, or sel is wrong). Read it again.");
        }

        return await PasteAsync(read, text, watch, "The selection was replaced and read back.", "Ctrl+V was sent over the selection", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Contract 2.6: puts text at the caret of the control that has the keyboard focus (dictation). Same path as a replace: the
    /// safety check of typing, a paste through the clipboard with its earlier text put back, and a read-back.
    /// </summary>
    public async Task<string> InsertAsync(string text, CancellationToken ct)
    {
        if (kill.IsTripped) return Line(ActStatus.Denied, false, ReasonCodes.KillSwitch, "OmniWin stop switch is tripped");
        if (string.IsNullOrEmpty(text) || text.Length > MaxReplaceChars)
        {
            return Line(ActStatus.Blocked, false, ReasonCodes.UnsupportedAction, $"text must be 1 to {MaxReplaceChars} characters.");
        }

        var watch = Stopwatch.StartNew();
        var read = await source.ReadSelectionAsync(0, ct).ConfigureAwait(false);
        if (read.Reason is { } reason)
        {
            return Line(reason == ReasonCodes.PolicyForbidden ? ActStatus.Denied : ActStatus.Blocked, false, reason, read.Detail);
        }

        // An app that does not expose its text can still be typed into; the result is then unverified, never confirmed.
        return await PasteAsync(read, text, watch, "The text was inserted at the caret and read back.", "Ctrl+V was sent to the focused control", ct).ConfigureAwait(false);
    }

    private async Task<string> PasteAsync(SelectionRead read, string text, Stopwatch watch, string confirmed, string sentWords, CancellationToken ct)
    {
        // The same check as typing: a delete command pasted into a terminal, the Run box or an address bar is refused.
        var target = new ElementDescriptor("selection", "desktop", read.Process, read.WindowTitle, null, read.ElementRole, read.ElementName, null, false, true, []);
        var authorized = await gate.AuthorizeAsync(new ActionDescriptor("desktop_act", "desktop", "type", target, read.Process, read.WindowTitle, null, text, null), ct)
            .ConfigureAwait(false);
        if (!authorized.Allowed)
        {
            return Line(ActStatus.Denied, false, authorized.Reason ?? ReasonCodes.PolicyForbidden, authorized.Detail ?? "The safety rules refuse this text here.");
        }

        if (_clipboard.HoldsNonText)
        {
            return Line(ActStatus.Blocked, false, ReasonCodes.UnsupportedAction,
                "The clipboard holds something that is not text (an image or files). Replacing pastes through the clipboard and would lose it, so nothing was changed.");
        }

        string earlier;
        try
        {
            earlier = await system.ClipboardGetAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Line(ActStatus.Blocked, false, ReasonCodes.UnsupportedAction, "The clipboard could not be read, so the text could not be pasted safely. Nothing was changed.");
        }

        var placed = await system.ClipboardSetAsync(text, ct).ConfigureAwait(false);
        if (placed.Status != ActStatus.Confirmed)
        {
            return Line(placed.Status == ActStatus.Denied ? ActStatus.Denied : ActStatus.Blocked, false, placed.Reason ?? ReasonCodes.UnsupportedAction,
                "The new text could not be put on the clipboard; nothing was typed.");
        }

        var pasted = await desktop.SendKeysAsync(new KeysRequest("ctrl+v"), ct).ConfigureAwait(false);
        var verified = false;
        if (pasted.InputSent)
        {
            await Task.Delay(Settle, CancellationToken.None).ConfigureAwait(false);
            try
            {
                var after = await source.ReadSelectionAsync(text.Length + 8, ct).ConfigureAwait(false);
                verified = after.BeforeCaret is { } before && Plain(before).EndsWith(Plain(text), StringComparison.Ordinal);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                verified = false;
            }
        }

        // Put the user's earlier clipboard text back, whatever happened to the paste.
        var restored = earlier.Length == 0;
        if (earlier.Length > 0)
        {
            try
            {
                restored = (await system.ClipboardSetAsync(earlier, CancellationToken.None).ConfigureAwait(false)).Status == ActStatus.Confirmed;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                restored = false;
            }
        }

        var clipboardNote = earlier.Length == 0 ? "The clipboard was empty before and now holds the new text."
            : restored ? "The clipboard's earlier text was put back." : "The clipboard's earlier text could NOT be put back; it now holds the new text.";
        if (!pasted.InputSent)
        {
            return Line(pasted.Status, false, pasted.Reason, (pasted.Detail is null ? "The paste was not sent. " : pasted.Detail + " ") + clipboardNote);
        }

        // safety.allow_delete: text the gate let through as a delete is labelled risk=risky (and so in the audit log).
        var risk = authorized.Risk == RiskClass.Risky || pasted.Risk == RiskClass.Risky ? RiskClass.Risky : (RiskClass?)null;
        return verified
            ? Line(ActStatus.Confirmed, true, null, confirmed + " " + clipboardNote, watch.Elapsed.TotalMilliseconds, Channels.Keys, risk)
            : Line(ActStatus.Unverified, true, null, sentWords + ", but the new text could not be read back. Look before you do more. " + clipboardNote,
                watch.Elapsed.TotalMilliseconds, Channels.Keys, risk);
    }


    private static string Plain(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').TrimEnd('\n');

    // ------------------------------------------------------------------ named instructions

    internal static readonly IReadOnlyList<PolishPrompt> DefaultPrompts =
    [
        new("fix_grammar", "Fix grammar", "Fix spelling, grammar and punctuation. Keep the wording, the meaning and the language. Return only the corrected text."),
        new("clearer", "Make clearer", "Rewrite so it is clear and easy to read: short sentences, plain words, the main point first. Keep every fact. Return only the rewritten text."),
        new("formal", "Make formal", "Rewrite in a polite, professional tone. Keep every fact and the language. Return only the rewritten text."),
        new("shorter", "Shorten", "Make it about half as long. Keep the facts that matter and the language. Return only the shortened text."),
        new("ai_prompt", "Turn into an AI prompt", "Rewrite this as a clear instruction for an AI assistant: say the goal, the needed context, the constraints and the wanted output format. Return only the prompt."),
    ];

    private sealed record PromptFile(IReadOnlyList<PolishPrompt>? Prompts);

    public IReadOnlyList<PolishPrompt> Prompts()
    {
        try
        {
            if (File.Exists(paths.PromptsFile))
            {
                using var stream = new FileStream(paths.PromptsFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var file = JsonSerializer.Deserialize<PromptFile>(stream, CoreJson.Options);
                var usable = file?.Prompts?.Where(p => !string.IsNullOrWhiteSpace(p?.Id) && !string.IsNullOrWhiteSpace(p.Prompt)).Take(50).ToList();
                if (usable is { Count: > 0 }) return usable;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // An unreadable file falls back to the built-in list.
        }

        return DefaultPrompts;
    }

    public string PromptsText()
    {
        var sb = new StringBuilder();
        var prompts = Prompts();
        sb.Append("polish_prompts count=").Append(prompts.Count.ToString(CultureInfo.InvariantCulture))
          .Append(" source=").Append(ReferenceEquals(prompts, DefaultPrompts) ? "built-in" : "prompts.json");
        foreach (var p in prompts)
        {
            sb.Append('\n').Append(ResultFormatter.OneLine(p.Id)).Append(" | ").Append(ResultFormatter.OneLine(p.Name ?? p.Id)).Append(" | ")
              .Append(ResultFormatter.OneLine(p.Prompt.Length > 600 ? p.Prompt[..600] : p.Prompt));
        }

        return sb.ToString();
    }
}

[McpServerToolType]
internal sealed class SelectionTools(SelectionService selection)
{
    [McpServerTool(Name = "selection_get", Title = "Read the selected text", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Read the text the user has selected in the control that has the keyboard focus, and get a sel id for it. Use it when " +
        "the user asks you to polish, rewrite, translate or explain \"what I selected\". Password fields and protected programs are " +
        "refused. Read-only; sends no input. " + ToolDocs.UntrustedContent)]
    public Task<string> SelectionGet(CancellationToken ct) => Guard(() => selection.GetAsync(ct));

    [McpServerTool(Name = "selection_replace", Title = "Replace the selected text", ReadOnly = false, Idempotent = false, Destructive = true, OpenWorld = false)]
    [Description("Replace the selection that selection_get returned (pass its sel id) with text. Refused when the selection changed since " +
        "it was read. It pastes over the selection: the clipboard is used and its earlier text is put back; it is refused when the " +
        "clipboard holds an image or files. confirmed means the new text was read back in place. " + ToolDocs.RetryRules)]
    public Task<string> SelectionReplace(string sel, string text, CancellationToken ct) => Guard(() => selection.ReplaceAsync(sel, text, ct));

    [McpServerTool(Name = "text_insert", Title = "Insert text at the caret", ReadOnly = false, Idempotent = false, Destructive = true, OpenWorld = false)]
    [Description("Put text at the caret of the control that has the keyboard focus (used for dictation). It pastes: the clipboard is " +
        "used and its earlier text is put back; refused when the clipboard holds an image or files, for password fields and protected " +
        "programs, and for a delete command in a terminal. confirmed means the text was read back at the caret. " + ToolDocs.RetryRules)]
    public Task<string> TextInsert(string text, CancellationToken ct) => Guard(() => selection.InsertAsync(text, ct));

    [McpServerTool(Name = "polish_prompts", Title = "List the user's rewrite instructions", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("List the user's named rewrite instructions (id | name | instruction), for example fix_grammar, clearer, formal, shorter, " +
        "ai_prompt. When the user says \"make it formal\" or names one, apply that instruction to the selected text yourself, then call " +
        "selection_replace. Read-only; sends no input.")]
    public string PolishPrompts() => selection.PromptsText();

    private static async Task<string> Guard(Func<Task<string>> body)
    {
        try
        {
            return await body().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return ResultFormatter.Format(ActResult.Failed(false, ReasonCodes.EngineTimeout, "The selection could not be read in time.", Timing.Total(0)));
        }
        catch (Exception)
        {
            return ResultFormatter.Format(ActResult.Failed(true, ReasonCodes.InputInterrupted, "The selection call failed part-way; look at the text before doing more.", Timing.Total(0)));
        }
    }
}
