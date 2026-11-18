using OmniWin.Core;
using OmniWin.Safety;
using OmniWin.Server.Tools;

namespace OmniWin.Router.Tests;

/// <summary>A pretend text box with a selection, a clipboard and a paste key, so every effect of the selection tools can be read off.</summary>
internal sealed class PretendEditor : ISelectionSource, IDesktopEngine, ISystemTools, IClipboardProbe
{
    public string Before { get; set; } = "Dear team, ";
    public string Selected { get; set; } = "i has wrote the report";
    public string After { get; set; } = " yesterday.";
    public string Process { get; set; } = "notepad.exe";
    public bool Supported { get; set; } = true;
    public string? Reason { get; set; }
    public string Clipboard { get; set; } = "what the user copied earlier";
    public bool HoldsNonText { get; set; }
    public bool PasteWorks { get; set; } = true;
    public ActResult? KeysResult { get; set; }
    public List<string> Keys { get; } = [];
    public List<string> ClipboardWrites { get; } = [];

    public string Document => Before + Selected + After;

    public Task<SelectionRead> ReadSelectionAsync(int beforeCaretChars, CancellationToken ct)
    {
        if (Reason is not null) return Task.FromResult(new SelectionRead(false, null, null, false, Process, "Doc", 7, "Text", "document", Reason, "refused by the test"));
        if (!Supported) return Task.FromResult(new SelectionRead(false, null, null, false, Process, "Doc", 7, "Text", "document", null, null));
        var upToCaret = Before + Selected;
        var before = beforeCaretChars > 0 ? upToCaret[Math.Max(0, upToCaret.Length - beforeCaretChars)..] : null;
        return Task.FromResult(new SelectionRead(true, Selected, before, false, Process, "Doc", 7, "Text", "document", null, null));
    }

    public Task<ActResult> SendKeysAsync(KeysRequest req, CancellationToken ct)
    {
        Keys.Add(req.Keys);
        if (KeysResult is { } scripted) return Task.FromResult(scripted);
        if (req.Keys == "ctrl+v" && PasteWorks)
        {
            Before += Clipboard;     // the pasted text now ends at the caret, and nothing is selected
            Selected = string.Empty;
        }

        return Task.FromResult(new ActResult(ActStatus.Unverified, true, null, null, null, Channels.Keys, Timing.Total(1)));
    }

    public Task<string> ClipboardGetAsync(CancellationToken ct) => Task.FromResult(Clipboard);

    public Task<ActResult> ClipboardSetAsync(string text, CancellationToken ct)
    {
        Clipboard = text;
        ClipboardWrites.Add(text);
        return Task.FromResult(new ActResult(ActStatus.Confirmed, true, null, null, null, Channels.Api, Timing.Total(1)));
    }

    // Not used by the selection tools.
    public Task<IReadOnlyList<WindowInfo>> ListWindowsAsync(CancellationToken ct) => throw new NotSupportedException();
    public Task<Observation> ObserveAsync(DesktopObserveRequest req, CancellationToken ct) => throw new NotSupportedException();
    public Task<ActResult> ActAsync(DesktopActRequest req, CancellationToken ct) => throw new NotSupportedException();
    public Task<ActResult> ScrollAsync(ScrollRequest req, CancellationToken ct) => throw new NotSupportedException();
    public Task<WaitResult> WaitForAsync(Expectation condition, int timeoutMs, CancellationToken ct) => throw new NotSupportedException();
    public Task<ElementDescriptor?> DescribeAsync(string elementRef, CancellationToken ct) => throw new NotSupportedException();
    public Task<ResolvedRef?> ResolveSelectorAsync(string process, string windowTitlePattern, IReadOnlyList<SelectorSegment> path, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<InstalledApp>> ListAppsAsync(string? filter, CancellationToken ct) => throw new NotSupportedException();
    public Task<ActResult> LaunchAppAsync(string app, string? args, ExpectSpec? expect, CancellationToken ct) => throw new NotSupportedException();
    public Task<ActResult> WindowManageAsync(WindowManageRequest req, CancellationToken ct) => throw new NotSupportedException();
    public Task<AudioState> GetAudioAsync(CancellationToken ct) => throw new NotSupportedException();
    public Task<ActResult> SetAudioAsync(int? volumePercent, bool? mute, CancellationToken ct) => throw new NotSupportedException();
    public Task<ActResult> MediaAsync(string action, CancellationToken ct) => throw new NotSupportedException();
    public Task<string> SystemInfoAsync(string kind, CancellationToken ct) => throw new NotSupportedException();
}

/// <summary>selection_get, selection_replace and polish_prompts (contract 2.6) against a pretend editor and the real safety gate.</summary>
public sealed class SelectionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "omniwin-test-sel-" + Guid.NewGuid().ToString("N"));
    private readonly PretendEditor _editor = new();
    private readonly SelectionService _service;

    public SelectionTests()
    {
        _service = new SelectionService(_editor, _editor, _editor, new SafetyGate(new OmniWinConfig(), new FreeKill()), new FreeKill(),
            OmniWinPaths.FromOverride(_dir), _editor);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);   // the temp folder this test created
        }
        catch (IOException)
        {
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<string> Sel()
    {
        var text = await _service.GetAsync(Ct);
        return text.Split('\n')[1].Split(' ').First(t => t.StartsWith("sel=", StringComparison.Ordinal))[4..];
    }

    [Fact]
    public async Task Get_returns_the_selected_text_as_untrusted_content_with_an_id_and_sends_nothing()
    {
        var text = await _service.GetAsync(Ct);
        var lines = text.Split('\n');

        Assert.StartsWith("status=confirmed input_sent=false", lines[0], StringComparison.Ordinal);
        Assert.Matches(@"^selection sel=[0-9a-f]{12} chars=22 process=notepad\.exe control=""Text""$", lines[1]);
        Assert.Equal(ResultFormatter.UntrustedHeader, lines[2]);
        Assert.Equal("i has wrote the report", lines[3]);
        Assert.Empty(_editor.Keys);
        Assert.Empty(_editor.ClipboardWrites);
    }

    [Fact]
    public async Task Replace_pastes_over_the_selection_reads_it_back_and_puts_the_earlier_clipboard_text_back()
    {
        var sel = await Sel();

        var result = await _service.ReplaceAsync(sel, "I have written the report", Ct);

        Assert.StartsWith("status=confirmed input_sent=true", result, StringComparison.Ordinal);
        Assert.Contains("retry=done", result, StringComparison.Ordinal);
        Assert.Equal("Dear team, I have written the report yesterday.", _editor.Document);
        Assert.Equal(["ctrl+v"], _editor.Keys);
        Assert.Equal("what the user copied earlier", _editor.Clipboard);
        Assert.Contains("earlier text was put back", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Replace_is_refused_when_the_selection_changed_since_it_was_read()
    {
        var sel = await Sel();
        _editor.Selected = "something else the user selected meanwhile";

        var result = await _service.ReplaceAsync(sel, "I have written the report", Ct);

        Assert.StartsWith("status=stale input_sent=false", result, StringComparison.Ordinal);
        Assert.Empty(_editor.Keys);
        Assert.Empty(_editor.ClipboardWrites);
        Assert.Equal("what the user copied earlier", _editor.Clipboard);
    }

    [Fact]
    public async Task A_paste_that_cannot_be_read_back_is_unverified_and_never_confirmed()
    {
        var sel = await Sel();
        _editor.PasteWorks = false;

        var result = await _service.ReplaceAsync(sel, "I have written the report", Ct);

        Assert.StartsWith("status=unverified input_sent=true", result, StringComparison.Ordinal);
        Assert.Contains("retry=check_state", result, StringComparison.Ordinal);
        Assert.Equal("what the user copied earlier", _editor.Clipboard);
    }

    [Fact]
    public async Task When_the_paste_key_is_blocked_nothing_is_sent_and_the_clipboard_is_put_back()
    {
        var sel = await Sel();
        _editor.KeysResult = new ActResult(ActStatus.Blocked, false, ReasonCodes.UserActive, "Recent user input paused automation.", null, null, Timing.Total(1));

        var result = await _service.ReplaceAsync(sel, "I have written the report", Ct);

        Assert.StartsWith("status=blocked input_sent=false reason=user_active", result, StringComparison.Ordinal);
        Assert.Equal("Dear team, i has wrote the report yesterday.", _editor.Document);
        Assert.Equal("what the user copied earlier", _editor.Clipboard);
    }

    [Fact]
    public async Task An_image_or_files_on_the_clipboard_are_never_overwritten()
    {
        var sel = await Sel();
        _editor.HoldsNonText = true;

        var result = await _service.ReplaceAsync(sel, "I have written the report", Ct);

        Assert.StartsWith("status=blocked input_sent=false reason=unsupported_action", result, StringComparison.Ordinal);
        Assert.Empty(_editor.ClipboardWrites);
        Assert.Empty(_editor.Keys);
    }

    [Fact]
    public async Task A_delete_command_is_not_pasted_into_a_terminal()
    {
        _editor.Process = "cmd.exe";
        var sel = await Sel();

        var result = await _service.ReplaceAsync(sel, "del /s /q C:\\Users\\<you>\\Documents\\*", Ct);

        Assert.StartsWith("status=denied input_sent=false reason=policy_forbidden", result, StringComparison.Ordinal);
        Assert.Empty(_editor.Keys);
        Assert.Empty(_editor.ClipboardWrites);
    }

    [Theory]
    [InlineData(ReasonCodes.PolicyForbidden, "status=denied input_sent=false reason=policy_forbidden")]     // a password field or protected program
    [InlineData(ReasonCodes.SecureDesktop, "status=blocked input_sent=false reason=secure_desktop")]
    [InlineData(ReasonCodes.ElevatedTarget, "status=blocked input_sent=false reason=elevated_target")]
    public async Task What_must_not_be_read_is_refused_for_get_and_for_replace(string reason, string expected)
    {
        _editor.Reason = reason;

        Assert.StartsWith(expected, await _service.GetAsync(Ct), StringComparison.Ordinal);
        Assert.StartsWith(expected, await _service.ReplaceAsync("000000000000", "x", Ct), StringComparison.Ordinal);
        Assert.Empty(_editor.Keys);
    }

    [Fact]
    public async Task An_app_without_a_readable_selection_and_an_empty_selection_say_so()
    {
        _editor.Supported = false;
        Assert.StartsWith("status=blocked input_sent=false reason=unsupported_action", await _service.GetAsync(Ct), StringComparison.Ordinal);

        _editor.Supported = true;
        _editor.Selected = string.Empty;
        Assert.StartsWith("status=blocked input_sent=false reason=not_found", await _service.GetAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Empty_and_oversized_replacement_text_is_refused()
    {
        var sel = await Sel();

        Assert.Contains("reason=unsupported_action", await _service.ReplaceAsync(sel, string.Empty, Ct), StringComparison.Ordinal);
        Assert.Contains("reason=unsupported_action", await _service.ReplaceAsync(sel, new string('x', SelectionService.MaxReplaceChars + 1), Ct), StringComparison.Ordinal);
        Assert.Empty(_editor.Keys);
    }

    [Fact]
    public async Task Insert_puts_dictated_text_at_the_caret_reads_it_back_and_restores_the_clipboard()
    {
        _editor.Selected = string.Empty;

        var result = await _service.InsertAsync("Hello there.", Ct);

        Assert.StartsWith("status=confirmed input_sent=true", result, StringComparison.Ordinal);
        Assert.Equal("Dear team, Hello there. yesterday.", _editor.Document);
        Assert.Equal("what the user copied earlier", _editor.Clipboard);
    }

    [Fact]
    public async Task Insert_into_an_app_that_hides_its_text_is_sent_but_never_called_confirmed()
    {
        _editor.Supported = false;

        var result = await _service.InsertAsync("Hello there.", Ct);

        Assert.StartsWith("status=unverified input_sent=true", result, StringComparison.Ordinal);
        Assert.Equal(["ctrl+v"], _editor.Keys);
        Assert.Equal("what the user copied earlier", _editor.Clipboard);
    }

    [Fact]
    public async Task Insert_is_refused_for_a_password_field_a_delete_command_in_a_terminal_and_a_non_text_clipboard()
    {
        _editor.Reason = ReasonCodes.PolicyForbidden;
        Assert.StartsWith("status=denied input_sent=false reason=policy_forbidden", await _service.InsertAsync("x", Ct), StringComparison.Ordinal);

        _editor.Reason = null;
        _editor.Process = "cmd.exe";
        Assert.StartsWith("status=denied input_sent=false reason=policy_forbidden", await _service.InsertAsync(@"del /s /q C:\Users\<you>\Documents\*", Ct), StringComparison.Ordinal);

        _editor.Process = "notepad.exe";
        _editor.HoldsNonText = true;
        Assert.StartsWith("status=blocked input_sent=false reason=unsupported_action", await _service.InsertAsync("x", Ct), StringComparison.Ordinal);
        Assert.Empty(_editor.Keys);
        Assert.Empty(_editor.ClipboardWrites);
    }

    [Fact]
    public void Prompts_come_from_the_built_in_list_until_the_owner_has_a_file_and_a_broken_file_falls_back()
    {
        Assert.StartsWith("polish_prompts count=5 source=built-in", _service.PromptsText(), StringComparison.Ordinal);
        Assert.Contains("formal | Make formal | ", _service.PromptsText(), StringComparison.Ordinal);

        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "prompts.json"), """{"prompts":[{"id":"pirate","name":"Pirate","prompt":"Rewrite as a pirate."}]}""");
        Assert.Equal("polish_prompts count=1 source=prompts.json\npirate | Pirate | Rewrite as a pirate.", _service.PromptsText());

        File.WriteAllText(Path.Combine(_dir, "prompts.json"), "{ not json");
        Assert.StartsWith("polish_prompts count=5 source=built-in", _service.PromptsText(), StringComparison.Ordinal);
    }
}
