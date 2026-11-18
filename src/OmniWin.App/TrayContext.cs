using System.Runtime.InteropServices;
using OmniWin.Safety;

namespace OmniWin.App;

/// <summary>
/// The app's life: a tray icon with a menu, the two optional global hotkeys, and the windows. It starts nothing with Windows and
/// registers nothing system-wide; closing it from the tray ends its server child too.
/// </summary>
internal sealed partial class TrayContext : ApplicationContext
{
    private const int CommandId = 1, PolishId = 2, DictationId = 3, VoiceId = 4;

    private readonly OmniWinPaths _paths;
    private readonly string? _dataDirOverride;
    private readonly NotifyIcon _tray;
    private readonly HotkeyWindow _keys;
    private AppSettings _settings;
    private ServerLink _server;
    private CommandBox? _box;
    private SettingsForm? _settingsForm;
    private bool _polishing;
    private readonly MicRecorder _mic = new();
    private int _recordingFor;   // 0 none, else DictationId or VoiceId

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(IntPtr window, int id);

    public TrayContext(OmniWinPaths paths, string? dataDirOverride)
    {
        _paths = paths;
        _dataDirOverride = dataDirOverride;
        _settings = AppSettings.Load(paths.DataDir);
        _server = new ServerLink(_settings.ResolveServerPath(), dataDirOverride);
        _keys = new HotkeyWindow(OnHotkey);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Command box", null, (_, _) => ShowBox());
        menu.Items.Add("Polish selected text", null, async (_, _) => await PolishAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Settings", null, (_, _) => ShowSettings());
        menu.Items.Add("Quit OmniWin app", null, (_, _) => ExitThread());
        _tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "OmniWin", ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => ShowBox();
        ApplyHotkeys();
    }

    private void ApplyHotkeys()
    {
        foreach (var id in new[] { CommandId, PolishId, DictationId, VoiceId }) UnregisterHotKey(_keys.Handle, id);
        var failed = new List<string>();
        if (_settings.Speech.DictationHotkeyEnabled && Hotkey.Parse(_settings.Speech.DictationHotkey, out _) is { } dictation &&
            !RegisterHotKey(_keys.Handle, DictationId, dictation.Modifiers, dictation.Key)) failed.Add(_settings.Speech.DictationHotkey);
        if (_settings.Speech.VoiceHotkeyEnabled && Hotkey.Parse(_settings.Speech.VoiceHotkey, out _) is { } voice &&
            !RegisterHotKey(_keys.Handle, VoiceId, voice.Modifiers, voice.Key)) failed.Add(_settings.Speech.VoiceHotkey);
        if (_settings.CommandHotkeyEnabled && Hotkey.Parse(_settings.CommandHotkey, out _) is { } command &&
            !RegisterHotKey(_keys.Handle, CommandId, command.Modifiers, command.Key)) failed.Add(_settings.CommandHotkey);
        if (_settings.PolishHotkeyEnabled && Hotkey.Parse(_settings.PolishHotkey, out _) is { } polish &&
            !RegisterHotKey(_keys.Handle, PolishId, polish.Modifiers, polish.Key)) failed.Add(_settings.PolishHotkey);
        if (failed.Count > 0) Notify("Another program already uses " + string.Join(" and ", failed) + ". Pick another hotkey in Settings.");
    }

    private void OnHotkey(int id)
    {
        if (id == CommandId) ShowBox();
        else if (id == PolishId) _ = PolishAsync();
        else if (id is DictationId or VoiceId) _ = ToggleRecordingAsync(id);
    }

    /// <summary>The same key starts and stops a recording. Dictation types the words at the cursor; a voice command only fills the box.</summary>
    private async Task ToggleRecordingAsync(int id)
    {
        if (_recordingFor == 0)
        {
            try
            {
                _mic.Start();
                _recordingFor = id;
                _tray.Text = "OmniWin: recording, press the hotkey again to stop";
                Notify(id == DictationId ? "Recording for dictation. Press the hotkey again to stop." : "Recording a command. Press the hotkey again to stop.");
            }
            catch (InvalidOperationException ex)
            {
                Notify(ex.Message);
            }

            return;
        }

        var purpose = _recordingFor;
        _recordingFor = 0;
        _tray.Text = "OmniWin";
        var wav = _mic.Stop();
        var model = SpeechModels.Find(_settings.Speech.Model) ?? SpeechModels.All[1];
        var dictation = new Dictation(_server, new WhisperCliTranscriber(_settings.Speech.EnginePath, SpeechModels.PathOf(_paths.DataDir, model), _settings.Speech.Language));
        try
        {
            if (purpose == DictationId)
            {
                Notify((await dictation.DictateAsync(wav, CancellationToken.None)).Message);
                return;
            }

            var heard = await dictation.TranscribeAsync(wav, CancellationToken.None);
            if (!heard.Ok)
            {
                Notify(heard.Message);
                return;
            }

            if (_box is null || _box.IsDisposed) _box = new CommandBox(_server);
            _box.ShowWithSpokenText(heard.Text);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            Notify("That did not work. " + ex.Message);
        }
    }

    private void ShowBox()
    {
        if (_box is null || _box.IsDisposed) _box = new CommandBox(_server);
        _box.ShowForRequest();
    }

    private void ShowSettings()
    {
        if (_settingsForm is { IsDisposed: false })
        {
            _settingsForm.Activate();
            return;
        }

        _settingsForm = new SettingsForm(_paths, _settings, _server.ServerExe, saved =>
        {
            _settings = saved;
            // The server reads config.json when it starts, so the app's own copy is restarted to pick the changes up.
            _server.Dispose();
            _server = new ServerLink(_settings.ResolveServerPath(), _dataDirOverride);
            if (_box is { IsDisposed: false }) _box.Close();
            _box = null;
            ApplyHotkeys();
        });
        _settingsForm.Show();
    }

    private async Task PolishAsync()
    {
        if (_polishing) return;
        if (_settings.Polish.Endpoint.Length == 0)
        {
            Notify("Polish is not set up yet. Open Settings and enter a model address, or ask your AI to polish the selection: it needs no key.");
            return;
        }

        _polishing = true;
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(_settings.Polish.TimeoutSeconds + 30));
            var result = await new Polisher(_server, new ChatEndpointRewriter(_settings.Polish)).PolishAsync(_settings.Polish.DefaultPrompt, limit.Token);
            Notify(result.Message);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or OperationCanceledException or FileNotFoundException)
        {
            Notify("Polish did not run. " + ex.Message);
        }
        finally
        {
            _polishing = false;
        }
    }

    private void Notify(string message) => _tray.ShowBalloonTip(6000, "OmniWin", message.Length > 250 ? message[..250] : message, ToolTipIcon.None);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var id in new[] { CommandId, PolishId, DictationId, VoiceId }) UnregisterHotKey(_keys.Handle, id);
            _mic.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            _keys.DestroyHandle();
            _box?.Dispose();
            _settingsForm?.Dispose();
            _server.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>A hidden window that only receives the hotkey messages.</summary>
    private sealed class HotkeyWindow : NativeWindow
    {
        private const int WmHotkey = 0x0312;
        private readonly Action<int> _pressed;

        public HotkeyWindow(Action<int> pressed)
        {
            _pressed = pressed;
            CreateHandle(new CreateParams());
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmHotkey) _pressed((int)m.WParam);
            base.WndProc(ref m);
        }
    }
}
