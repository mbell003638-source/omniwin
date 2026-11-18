using System.Globalization;
using OmniWin.Safety;

namespace OmniWin.App;

/// <summary>
/// One window for the settings an owner changes: learning, the quick commands, the optional decision model, the two hotkeys and the
/// polish model. Each line says what it does in plain words. OmniWin's own settings are checked by the server before they are
/// saved. The safety protections are shown and cannot be changed here.
/// </summary>
internal sealed class SettingsForm : Form
{
    private readonly OmniWinPaths _paths;
    private readonly ConfigEditor _editor;
    private readonly Action<AppSettings> _saved;
    private readonly TableLayoutPanel _grid = new() { Dock = DockStyle.Fill, ColumnCount = 2, AutoScroll = true, Padding = new Padding(12) };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 64, ForeColor = Color.Firebrick, Padding = new Padding(12, 4, 12, 4) };

    private readonly CheckBox _learning = Tick("Learn tasks the AI did, so they can be replayed");
    private readonly CheckBox _autoRun = Tick("Let a trusted learned task run by itself from your words");
    private readonly NumericUpDown _promote = new() { Minimum = 1, Maximum = 20, Width = 60 };
    private readonly CheckBox _router = Tick("Quick commands (volume, mute, switch window ...)");
    private readonly CheckBox _engines = Tick("Other engines (Windows-MCP, agent-desktop) for the AI");
    private readonly TextBox _provider = new() { Width = 180 };
    private readonly ComboBox _mode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
    private readonly NumericUpDown _threshold = new() { Minimum = 0.50m, Maximum = 1.00m, DecimalPlaces = 2, Increment = 0.01m, Width = 60 };
    private readonly CheckBox _decisionRemote = Tick("The decision model is not on this PC: allow sending my request wording to it");
    private readonly CheckBox _commandOn = Tick("Hotkey opens the command box");
    private readonly TextBox _commandKey = new() { Width = 180 };
    private readonly CheckBox _polishOn = Tick("Hotkey polishes the selected text");
    private readonly TextBox _polishKey = new() { Width = 180 };
    private readonly TextBox _endpoint = new() { Width = 320, PlaceholderText = "http://localhost:11434/v1" };
    private readonly TextBox _model = new() { Width = 180 };
    private readonly TextBox _keyEnv = new() { Width = 180, PlaceholderText = "for example OPENAI_API_KEY" };
    private readonly CheckBox _polishRemote = Tick("The address is not on this PC: allow sending my selected text to it");
    private readonly TextBox _prompt = new() { Width = 180 };
    private readonly CheckBox _dictationOn = Tick("Hotkey starts and stops dictation (your words are typed at the cursor)");
    private readonly TextBox _dictationKey = new() { Width = 180 };
    private readonly CheckBox _voiceOn = Tick("Hotkey starts and stops a spoken command (it goes into the command box for you to check)");
    private readonly TextBox _voiceKey = new() { Width = 180 };
    private readonly TextBox _enginePath = new() { Width = 320, PlaceholderText = @"C:\...\whisper-cli.exe (from whisper.cpp)" };
    private readonly ComboBox _speechModel = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 320 };
    private readonly Button _download = new() { Text = "Download this model", AutoSize = true };
    private readonly TextBox _language = new() { Width = 60 };
    private readonly TextBox _serverPath = new() { Width = 320, PlaceholderText = "(found automatically)" };

    private static CheckBox Tick(string text) => new() { Text = text, AutoSize = true };

    public SettingsForm(OmniWinPaths paths, AppSettings app, string serverExe, Action<AppSettings> saved)
    {
        _paths = paths;
        _editor = new ConfigEditor(paths.ConfigFile, serverExe);
        _saved = saved;
        Text = "OmniWin settings";
        ClientSize = new Size(700, 860);
        StartPosition = FormStartPosition.CenterScreen;
        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _mode.Items.AddRange(["off", "shadow", "act"]);

        ConfigView view;
        try
        {
            view = _editor.Load();
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        {
            view = new ConfigView(true, true, 3, true, "none", "off", 0.95, false, true, false, false);
            _status.Text = "config.json could not be read; the values shown are the defaults. " + ex.Message;
        }

        Heading("Learning");
        Wide(_learning);
        Wide(_autoRun);
        Row("Clean replays before a task is trusted", _promote);
        Heading("Quick commands and engines");
        Wide(_router);
        Wide(_engines);
        Heading("Decision model (optional; leave \"none\" unless you have wired one)");
        Row("Model name", _provider);
        Row("Mode (shadow only watches; act may run a pick)", _mode);
        Row("How sure it must be (0.50 to 1.00)", _threshold);
        Wide(_decisionRemote);
        Heading("Hotkeys (off until you tick them; never Ctrl+Alt+Shift+K, the stop key)");
        Wide(_commandOn);
        Row("Command box hotkey", _commandKey);
        Wide(_polishOn);
        Row("Polish hotkey", _polishKey);
        Heading("Polish by hotkey (your AI can polish with no key: just ask it)");
        Row("Model address", _endpoint);
        Row("Model name", _model);
        Row("Environment variable holding the key", _keyEnv);
        Wide(_polishRemote);
        Row("Instruction to use (for example fix_grammar)", _prompt);
        Heading("Voice (optional; runs on this PC with whisper.cpp; nothing listens until you press the hotkey)");
        Wide(_dictationOn);
        Row("Dictation hotkey", _dictationKey);
        Wide(_voiceOn);
        Row("Voice command hotkey", _voiceKey);
        Row("Speech program (whisper-cli.exe)", _enginePath);
        Row("Speech model", _speechModel);
        Row("", _download);
        Row("Language (auto, or a code such as en)", _language);
        Heading("Safety (shown only; these cannot be changed here)");
        Wide(new Label { AutoSize = true, Text = $"Deleting is refused: {(view.AllowDelete ? "NO (allow_delete is on in config.json)" : "yes")}" });
        Wide(new Label { AutoSize = true, Text = $"Power tools (shell, files, registry, process): {(view.PowerToolsEnabled ? "ON in config.json" : "off")}" });
        Wide(new Label { AutoSize = true, Text = "Stop key: Ctrl+Alt+Shift+K" });
        Heading("Advanced");
        Row("OmniWin.Server.exe", _serverPath);

        _learning.Checked = view.LearningEnabled;
        _autoRun.Checked = view.LearningAutoRun;
        _promote.Value = Math.Clamp(view.PromoteAfter, 1, 20);
        _router.Checked = view.RouterEnabled;
        _engines.Checked = view.EnginesEnabled;
        _provider.Text = view.DecisionProvider;
        _mode.SelectedItem = _mode.Items.Contains(view.DecisionMode) ? view.DecisionMode : "off";
        _threshold.Value = (decimal)Math.Clamp(view.DecisionThreshold, 0.5, 1.0);
        _decisionRemote.Checked = view.DecisionAllowRemote;
        _commandOn.Checked = app.CommandHotkeyEnabled;
        _commandKey.Text = app.CommandHotkey;
        _polishOn.Checked = app.PolishHotkeyEnabled;
        _polishKey.Text = app.PolishHotkey;
        _endpoint.Text = app.Polish.Endpoint;
        _model.Text = app.Polish.Model;
        _keyEnv.Text = app.Polish.ApiKeyEnv;
        _polishRemote.Checked = app.Polish.AllowRemote;
        _prompt.Text = app.Polish.DefaultPrompt;
        _serverPath.Text = app.ServerPath;
        _dictationOn.Checked = app.Speech.DictationHotkeyEnabled;
        _dictationKey.Text = app.Speech.DictationHotkey;
        _voiceOn.Checked = app.Speech.VoiceHotkeyEnabled;
        _voiceKey.Text = app.Speech.VoiceHotkey;
        _enginePath.Text = app.Speech.EnginePath;
        _language.Text = app.Speech.Language;
        foreach (var model in SpeechModels.All) _speechModel.Items.Add(ModelLine(model));
        _speechModel.SelectedIndex = Math.Max(0, SpeechModels.All.ToList().FindIndex(m => m.Id == app.Speech.Model));
        _speechModel.SelectedIndexChanged += (_, _) => _download.Enabled = !File.Exists(SpeechModels.PathOf(_paths.DataDir, SpeechModels.All[_speechModel.SelectedIndex]));
        _download.Enabled = !File.Exists(SpeechModels.PathOf(_paths.DataDir, SpeechModels.All[_speechModel.SelectedIndex]));
        _download.Click += async (_, _) => await DownloadModelAsync();

        var save = new Button { Text = "Save", AutoSize = true, Dock = DockStyle.Right };
        var close = new Button { Text = "Close", AutoSize = true, Dock = DockStyle.Right };
        var bar = new Panel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(12, 6, 12, 6) };
        bar.Controls.AddRange([save, close]);
        save.Click += (_, _) => Save(view);
        close.Click += (_, _) => Close();
        Controls.AddRange([_grid, _status, bar]);
    }

    private string ModelLine(SpeechModel model) =>
        $"{model.Name}, {model.SizeMb} MB: {model.Note}{(File.Exists(SpeechModels.PathOf(_paths.DataDir, model)) ? "  [downloaded]" : string.Empty)}";

    /// <summary>The only place a model is downloaded: the owner picked it and pressed the button.</summary>
    private async Task DownloadModelAsync()
    {
        var model = SpeechModels.All[_speechModel.SelectedIndex];
        if (MessageBox.Show(this, $"Download {model.Name} ({model.SizeMb} MB) from huggingface.co into the OmniWin data folder?", "OmniWin",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        _download.Enabled = false;
        _status.ForeColor = SystemColors.ControlText;
        try
        {
            await SpeechModels.DownloadAsync(_paths.DataDir, model, new Progress<int>(p => _status.Text = $"Downloading {model.Name}: {p}%"), CancellationToken.None);
            _status.ForeColor = Color.SeaGreen;
            _status.Text = $"{model.Name} is downloaded.";
            _speechModel.Items[_speechModel.SelectedIndex] = ModelLine(model);
        }
        catch (InvalidDataException ex)
        {
            _status.ForeColor = Color.Firebrick;
            _status.Text = ex.Message;
            _download.Enabled = true;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException)
        {
            _status.ForeColor = Color.Firebrick;
            _status.Text = "The download did not finish: " + ex.Message;
            _download.Enabled = true;
        }
    }

    private void Heading(string text)
    {
        var label = new Label { Text = text, AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 14, 0, 4) };
        _grid.Controls.Add(label);
        _grid.SetColumnSpan(label, 2);
    }

    private void Wide(Control control)
    {
        _grid.Controls.Add(control);
        _grid.SetColumnSpan(control, 2);
    }

    private void Row(string text, Control control)
    {
        _grid.Controls.Add(new Label { Text = text, AutoSize = true, Margin = new Padding(0, 6, 12, 0) });
        _grid.Controls.Add(control);
    }

    private void Save(ConfigView loaded)
    {
        var app = new AppSettings
        {
            CommandHotkeyEnabled = _commandOn.Checked,
            CommandHotkey = _commandKey.Text.Trim(),
            PolishHotkeyEnabled = _polishOn.Checked,
            PolishHotkey = _polishKey.Text.Trim(),
            ServerPath = _serverPath.Text.Trim(),
            Speech = new SpeechSettings
            {
                DictationHotkeyEnabled = _dictationOn.Checked,
                DictationHotkey = _dictationKey.Text.Trim(),
                VoiceHotkeyEnabled = _voiceOn.Checked,
                VoiceHotkey = _voiceKey.Text.Trim(),
                EnginePath = _enginePath.Text.Trim(),
                Model = SpeechModels.All[Math.Max(0, _speechModel.SelectedIndex)].Id,
                Language = _language.Text.Trim().Length == 0 ? "auto" : _language.Text.Trim().ToLowerInvariant(),
            },
            Polish = new PolishSettings
            {
                Endpoint = _endpoint.Text.Trim(),
                Model = _model.Text.Trim(),
                ApiKeyEnv = _keyEnv.Text.Trim(),
                AllowRemote = _polishRemote.Checked,
                DefaultPrompt = _prompt.Text.Trim().Length == 0 ? "fix_grammar" : _prompt.Text.Trim(),
            },
        };
        var problems = new List<string>(app.Validate());
        if (problems.Count == 0)
        {
            var view = loaded with
            {
                LearningEnabled = _learning.Checked,
                LearningAutoRun = _autoRun.Checked,
                PromoteAfter = (int)_promote.Value,
                RouterEnabled = _router.Checked,
                EnginesEnabled = _engines.Checked,
                DecisionProvider = _provider.Text.Trim().Length == 0 ? "none" : _provider.Text.Trim(),
                DecisionMode = (string?)_mode.SelectedItem ?? "off",
                DecisionThreshold = (double)_threshold.Value,
                DecisionAllowRemote = _decisionRemote.Checked,
            };
            problems.AddRange(_editor.Save(view));
        }

        if (problems.Count > 0)
        {
            _status.ForeColor = Color.Firebrick;
            _status.Text = "Not saved:\n" + string.Join("\n", problems);
            return;
        }

        try
        {
            app.Save(_paths.DataDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _status.Text = "OmniWin's settings were saved, but the app's own settings were not: " + ex.Message;
            return;
        }

        _status.ForeColor = Color.SeaGreen;
        _status.Text = string.Create(CultureInfo.InvariantCulture,
            $"Saved {DateTime.Now:HH:mm:ss}. The command box uses the new settings now. An AI app that is already running uses them the next time it starts OmniWin.");
        _saved(app);
    }
}
