namespace OmniWin.App;

/// <summary>
/// The command box: type a request, press Enter, and OmniWin runs it if it is a quick command or a task it has learned. The box has
/// no AI of its own: anything else is shown as "needs your AI" with a Copy button. After a learned task ran, Right / Wrong tell
/// OmniWin directly whether that was the right thing, which is the safest signal for trust.
/// </summary>
internal sealed class CommandBox : Form
{
    private readonly IToolCaller _omniwin;
    private readonly TextBox _input = new() { Dock = DockStyle.Top, Font = new Font("Segoe UI", 12f), PlaceholderText = "Type a request and press Enter  (Esc closes)" };
    private readonly Label _headline = new() { Dock = DockStyle.Top, AutoSize = false, Height = 54, Font = new Font("Segoe UI", 10f), Padding = new Padding(2, 8, 2, 0) };
    private readonly Label _detail = new() { Dock = DockStyle.Fill, ForeColor = SystemColors.GrayText, Font = new Font("Segoe UI", 9f), Padding = new Padding(2) };
    private readonly FlowLayoutPanel _buttons = new() { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 4, 0, 0) };
    private readonly Button _copy = new() { Text = "Copy request", AutoSize = true, Visible = false };
    private readonly Button _right = new() { Text = "That was right", AutoSize = true, Visible = false };
    private readonly Button _wrong = new() { Text = "That was wrong", AutoSize = true, Visible = false };
    private string? _lastRecipe;
    private bool _busy;

    public CommandBox(IToolCaller omniwin)
    {
        _omniwin = omniwin;
        Text = "OmniWin";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(560, 190);
        Padding = new Padding(10);
        TopMost = true;
        ShowInTaskbar = false;
        KeyPreview = true;
        _buttons.Controls.AddRange([_copy, _wrong, _right]);
        Controls.AddRange([_detail, _buttons, _headline, _input]);

        _input.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            await RunAsync();
        };
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) Hide();
        };
        _copy.Click += (_, _) =>
        {
            if (_input.Text.Length > 0) Clipboard.SetText(_input.Text);
            _detail.Text = "Copied. Paste it into your AI (Claude, Codex, Antigravity).";
        };
        _right.Click += async (_, _) => await RateAsync("right", "Thanks. That counts toward trusting this task.");
        _wrong.Click += async (_, _) => await RateAsync("wrong_pick", "Noted. OmniWin will not run this task by itself until it has earned trust again.");
        FormClosing += (_, e) =>
        {
            if (e.CloseReason != CloseReason.UserClosing) return;
            e.Cancel = true;   // closing the box only hides it; the tray icon quits the app
            Hide();
        };
    }

    public void ShowForRequest()
    {
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + area.Height / 4);
        _input.Clear();
        Say(string.Empty, null);
        Show();
        Activate();
        _input.Focus();
    }

    /// <summary>Opens the box with words that were spoken, for the owner to check, correct and run with Enter. Nothing runs by itself.</summary>
    public void ShowWithSpokenText(string text)
    {
        ShowForRequest();
        _input.Text = text;
        _input.SelectionStart = text.Length;
        Say("Heard this. Press Enter to run it, or correct it first. Esc cancels.", null);
    }

    private void Say(string headline, string? detail, bool copy = false, bool rate = false)
    {
        _headline.Text = headline;
        _detail.Text = detail ?? string.Empty;
        _copy.Visible = copy;
        _right.Visible = _wrong.Visible = rate;
    }

    private async Task RunAsync()
    {
        var request = _input.Text.Trim();
        if (request.Length == 0 || _busy) return;
        _busy = true;
        _lastRecipe = null;
        Say("Working ...", null);
        try
        {
            // Step out of the way first: the request acts on the window that was in front before the box opened.
            Hide();
            await Task.Delay(250);
            var text = await _omniwin.CallAsync("omniwin_do", new { text = request }, CancellationToken.None);
            var explained = PlainWords.Explain(text);
            _lastRecipe = explained.Recipe;
            if (explained.Outcome == Outcome.NeedsAi)
            {
                // The box is not an AI and will not finish the task: close the episode the server opened, so nothing half-done is learned.
                await _omniwin.CallAsync("task_done", new { success = false }, CancellationToken.None);
            }

            var detail = explained.Detail;
            if (explained.Suggestions.Count > 0) detail = (detail is null ? string.Empty : detail + "\n") + "Your AI can run a saved task for this: " + explained.Suggestions[0].Split('(')[0];
            Say(explained.Headline, detail, copy: explained.Outcome == Outcome.NeedsAi, rate: explained.Recipe is not null && explained.Outcome is Outcome.Done or Outcome.Unverified);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or FileNotFoundException)
        {
            Say("OmniWin could not be reached.", ex.Message);
        }
        finally
        {
            _busy = false;
            Show();
            Activate();
            _input.SelectAll();
            _input.Focus();
        }
    }

    private async Task RateAsync(string action, string thanks)
    {
        if (_lastRecipe is null) return;
        try
        {
            await _omniwin.CallAsync("workflow_manage", new { name = _lastRecipe, action }, CancellationToken.None);
            Say(_headline.Text, thanks);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            Say(_headline.Text, "That could not be recorded: " + ex.Message);
        }
    }
}
