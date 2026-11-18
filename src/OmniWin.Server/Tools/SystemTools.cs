using System.ComponentModel;
using ModelContextProtocol.Server;
using OmniWin.Core;
using OmniWin.SystemTools;

namespace OmniWin.Server.Tools;

/// <summary>
/// WS3 system tools (contract §8): apps_list, app_launch, window_manage, audio, media, system_info, clipboard_get,
/// clipboard_set. Nothing here runs a shell, a script or an arbitrary executable. A handler never throws: any failure
/// comes back as a status line with an accurate input_sent.
/// </summary>
[McpServerToolType]
internal sealed class SystemTools(SystemToolsService system)
{
    [McpServerTool(Name = "apps_list", Title = "List Start-menu apps", ReadOnly = true, Idempotent = true, Destructive = false)]
    [Description("List installed Start-menu apps with the name and launch id app_launch accepts. filter is a case-insensitive part of the name. " +
        ToolDocs.UntrustedContent)]
    public Task<string> AppsList(CancellationToken ct, string? filter = null) => GuardAsync(async () =>
    {
        var outcome = await system.ListAppsOutcomeAsync(filter, ct).ConfigureAwait(false);
        if (outcome.Failure is { } failure) return ResultFormatter.Format(failure);

        return SystemFormat.AppsList(outcome.Value!);
    });

    [McpServerTool(Name = "app_launch", Title = "Launch a Start-menu app", ReadOnly = false, Idempotent = false, Destructive = false)]
    [Description("Start an app from the Start menu by its name or launch id (see apps_list). Paths, URLs, command lines and files are refused " +
        "unless the exact executable path is in app_launch_allow_paths. args are never passed to cmd, PowerShell, a terminal or a script host, " +
        "and Start-menu apps take no args. " + ToolDocs.ExpectParameter + " " + ToolDocs.RetryRules)]
    public Task<string> AppLaunch(string app, CancellationToken ct, string? args = null, ExpectSpec? expect = null) => GuardActAsync(async () =>
        ResultFormatter.Format(await system.LaunchAppAsync(app, args, expect, ct).ConfigureAwait(false)));

    [McpServerTool(Name = "window_manage", Title = "Manage a window", ReadOnly = false, Idempotent = false, Destructive = true)]
    [Description("Focus, minimize, maximize, restore, move, resize, snap_left, snap_right or close a window. " +
        "window is a wN id from windows_list, a process name, or a unique title substring. move needs x and y; resize needs width and height " +
        "(physical pixels). close is labelled risky in the log. The result reads the window back. " + ToolDocs.RetryRules)]
    public Task<string> WindowManage(string window, string action, CancellationToken ct,
        int? x = null, int? y = null, int? width = null, int? height = null) => GuardActAsync(async () =>
        ResultFormatter.Format(await system.WindowManageAsync(new WindowManageRequest(window, action, x, y, width, height), ct).ConfigureAwait(false)));

    [McpServerTool(Name = "audio", Title = "Read or set volume", ReadOnly = false, Idempotent = true, Destructive = false)]
    [Description("With no parameters, read the default output device's volume (0-100) and mute state. With volume and/or mute, set them " +
        "and read them back. " + ToolDocs.RetryRules)]
    public Task<string> Audio(CancellationToken ct, int? volume = null, bool? mute = null) => GuardActAsync(async () =>
    {
        if (volume is null && mute is null)
        {
            var outcome = await system.GetAudioOutcomeAsync(ct).ConfigureAwait(false);
            if (outcome.Failure is { } failure) return ResultFormatter.Format(failure);

            return SystemFormat.Audio(outcome.Value!, outcome.Timing);
        }

        return ResultFormatter.Format(await system.SetAudioAsync(volume, mute, ct).ConfigureAwait(false));
    });

    [McpServerTool(Name = "media", Title = "Control media playback", ReadOnly = false, Idempotent = false, Destructive = false)]
    [Description("Control the current media session (the app the volume flyout shows): play, pause (both safe to repeat: nothing is sent if it is already so), play_pause (a toggle), next, previous or stop. " +
        "Skipping can't be read back and returns unverified. " + ToolDocs.RetryRules)]
    public Task<string> Media(string action, CancellationToken ct) => GuardActAsync(async () =>
        ResultFormatter.Format(await system.MediaAsync(action, ct).ConfigureAwait(false)));

    [McpServerTool(Name = "system_info", Title = "Read system information", ReadOnly = true, Idempotent = true, Destructive = false)]
    [Description("Read-only system information. kind is network (connection state and adapters, no addresses), battery, displays, or time. " +
        ToolDocs.UntrustedContent)]
    public Task<string> SystemInfo(string kind, CancellationToken ct) => GuardAsync(async () =>
    {
        var outcome = await system.SystemInfoOutcomeAsync(kind, ct).ConfigureAwait(false);
        if (outcome.Failure is { } failure) return ResultFormatter.Format(failure);

        return SystemFormat.SystemInfo(kind, outcome.Value!);
    });

    [McpServerTool(Name = "clipboard_get", Title = "Read the clipboard text", ReadOnly = true, Idempotent = true, Destructive = false)]
    [Description("Read the text on the clipboard (up to 20000 characters). It is labelled risky in the log, and the text is never logged. " +
        ToolDocs.UntrustedContent)]
    public Task<string> ClipboardGet(CancellationToken ct) => GuardAsync(async () =>
    {
        var outcome = await system.ClipboardGetOutcomeAsync(ct).ConfigureAwait(false);
        if (outcome.Failure is { } failure) return ResultFormatter.Format(failure);

        return SystemFormat.Clipboard(outcome.Value!, outcome.Timing);
    });

    [McpServerTool(Name = "clipboard_set", Title = "Set the clipboard text", ReadOnly = false, Idempotent = true, Destructive = false)]
    [Description("Replace the clipboard with this text (up to 1000000 characters) and read it back. The text is never logged; only its length is. " +
        ToolDocs.RetryRules)]
    public Task<string> ClipboardSet(string text, CancellationToken ct) => GuardActAsync(async () =>
        ResultFormatter.Format(await system.ClipboardSetAsync(text, ct).ConfigureAwait(false)));

    private static Task<string> GuardAsync(Func<Task<string>> read) => RunAsync(read, sentOnFailure: false);

    private static Task<string> GuardActAsync(Func<Task<string>> act) => RunAsync(act, sentOnFailure: false);

    private static async Task<string> RunAsync(Func<Task<string>> body, bool sentOnFailure)
    {
        try
        {
            return await body().ConfigureAwait(false);
        }
        catch (SystemToolFault fault)
        {
            return ResultFormatter.Format(fault.Result);
        }
        catch (Exception)
        {
            // The service already maps every expected failure; this is the last line of defence (§16: nothing escapes).
            return ResultFormatter.Format(ActResult.Failed(sentOnFailure, ReasonCodes.NotFound,
                "The system call could not be completed.", Timing.Total(0)));
        }
    }
}
