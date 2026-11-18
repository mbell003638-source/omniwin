using System.Text.Json;
using OmniWin.Core;

namespace OmniWin.App;

/// <summary>Where the polish hotkey sends the selected text to be rewritten. Off until an endpoint is set.</summary>
internal sealed record PolishSettings
{
    /// <summary>An OpenAI-style address, for example http://localhost:11434/v1 (a model on this PC) or https://api.openai.com/v1.</summary>
    public string Endpoint { get; init; } = string.Empty;

    public string Model { get; init; } = string.Empty;

    /// <summary>Name of the environment variable that holds the key. The key itself is never written to a file.</summary>
    public string ApiKeyEnv { get; init; } = string.Empty;

    /// <summary>An address that is not this PC receives the selected text. Blocked unless the owner ticks this.</summary>
    public bool AllowRemote { get; init; }

    /// <summary>The instruction used by the hotkey (an id from polish_prompts).</summary>
    public string DefaultPrompt { get; init; } = "fix_grammar";

    public int TimeoutSeconds { get; init; } = 60;
}

/// <summary>Voice: a local speech model for dictation and spoken commands. Off until a hotkey is ticked and a model is there.</summary>
internal sealed record SpeechSettings
{
    public bool DictationHotkeyEnabled { get; init; }

    /// <summary>Press once to start recording, again to stop: the words are typed at the cursor.</summary>
    public string DictationHotkey { get; init; } = "Ctrl+Alt+D";

    public bool VoiceHotkeyEnabled { get; init; }

    /// <summary>Press once to start, again to stop: the words go into the command box for you to check and run.</summary>
    public string VoiceHotkey { get; init; } = "Ctrl+Alt+V";

    /// <summary>Path of whisper.cpp's whisper-cli.exe.</summary>
    public string EnginePath { get; init; } = string.Empty;

    /// <summary>An id from the model list (tiny, base, small ...).</summary>
    public string Model { get; init; } = "base";

    /// <summary>"auto", or a language code such as en.</summary>
    public string Language { get; init; } = "auto";
}

/// <summary>
/// The app's own settings (<c>app.json</c> in the OmniWin data folder). Everything that sends input by a hotkey is off until the
/// owner turns it on. OmniWin's safety and learning settings are not here: they stay in config.json, which the server owns.
/// </summary>
internal sealed record AppSettings
{
    public bool CommandHotkeyEnabled { get; init; }

    public string CommandHotkey { get; init; } = "Ctrl+Alt+Space";

    public bool PolishHotkeyEnabled { get; init; }

    public string PolishHotkey { get; init; } = "Ctrl+Alt+P";

    /// <summary>Path of OmniWin.Server.exe. Empty: the one beside this app, else ..\publish.</summary>
    public string ServerPath { get; init; } = string.Empty;

    public PolishSettings Polish { get; init; } = new();

    public SpeechSettings Speech { get; init; } = new();

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Speech.DictationHotkeyEnabled && Hotkey.Parse(Speech.DictationHotkey, out var speechWhy) is null) errors.Add("Dictation hotkey: " + speechWhy);
        if (Speech.VoiceHotkeyEnabled && Hotkey.Parse(Speech.VoiceHotkey, out speechWhy) is null) errors.Add("Voice command hotkey: " + speechWhy);
        if (SpeechModels.Find(Speech.Model) is null) errors.Add($"\"{Speech.Model}\" is not a speech model in the list.");
        if (Speech.Language != "auto" && (Speech.Language.Length is < 2 or > 3 || !Speech.Language.All(char.IsAsciiLetterLower)))
        {
            errors.Add("Speech language is \"auto\" or a short code such as en.");
        }

        var used = new List<Hotkey>();
        foreach (var (on, text) in new[] { (CommandHotkeyEnabled, CommandHotkey), (PolishHotkeyEnabled, PolishHotkey),
                     (Speech.DictationHotkeyEnabled, Speech.DictationHotkey), (Speech.VoiceHotkeyEnabled, Speech.VoiceHotkey) })
        {
            if (!on || Hotkey.Parse(text, out _) is not { } key) continue;
            if (used.Contains(key)) errors.Add($"The hotkey {text} is used twice.");
            used.Add(key);
        }

        if (CommandHotkeyEnabled && Hotkey.Parse(CommandHotkey, out var why) is null) errors.Add("Command box hotkey: " + why);
        if (PolishHotkeyEnabled && Hotkey.Parse(PolishHotkey, out why) is null) errors.Add("Polish hotkey: " + why);
        if (Polish.Endpoint.Length > 0)
        {
            if (!Uri.TryCreate(Polish.Endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            {
                errors.Add("Polish address must start with http:// or https://");
            }
            else if (!uri.IsLoopback && !Polish.AllowRemote)
            {
                errors.Add("The polish address is not on this PC. Tick \"allow sending text to this address\" if that is what you want.");
            }

            if (Polish.Model.Length == 0) errors.Add("Polish needs a model name.");
        }

        if (Polish.TimeoutSeconds is < 5 or > 600) errors.Add("Polish time limit must be 5 to 600 seconds.");
        return errors;
    }

    public static string FileIn(string dataDir) => Path.Combine(dataDir, "app.json");

    public static AppSettings Load(string dataDir)
    {
        var file = FileIn(dataDir);
        try
        {
            if (File.Exists(file)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file), CoreJson.Indented) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // An unreadable file means defaults: every hotkey off.
        }

        return new AppSettings();
    }

    /// <summary>Writes the app's own file: a temp file in the same folder, then one rename over the old one.</summary>
    public void Save(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        var file = FileIn(dataDir);
        var temp = file + ".new";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, CoreJson.Indented));
        File.Move(temp, file, overwrite: true);
    }

    public string ResolveServerPath()
    {
        if (ServerPath.Length > 0) return Path.GetFullPath(Environment.ExpandEnvironmentVariables(ServerPath));
        var beside = Path.Combine(AppContext.BaseDirectory, "OmniWin.Server.exe");
        if (File.Exists(beside)) return beside;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var published = Path.Combine(dir.FullName, "publish", "OmniWin.Server.exe");
            if (File.Exists(published)) return published;
        }

        return beside;
    }
}

/// <summary>A global hotkey: modifier flags and a virtual key, as RegisterHotKey wants them.</summary>
internal sealed record Hotkey(uint Modifiers, uint Key)
{
    public const uint Alt = 1, Ctrl = 2, Shift = 4, Win = 8;

    /// <summary>Parses "Ctrl+Alt+Space". Needs at least one of Ctrl/Alt/Win, and is never OmniWin's stop key.</summary>
    public static Hotkey? Parse(string? text, out string? problem)
    {
        problem = null;
        uint modifiers = 0;
        uint key = 0;
        foreach (var raw in (text ?? string.Empty).Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= Ctrl; break;
                case "alt": modifiers |= Alt; break;
                case "shift": modifiers |= Shift; break;
                case "win" or "windows": modifiers |= Win; break;
                default:
                    if (key != 0 || KeyCode(raw) is not { } code)
                    {
                        problem = $"\"{raw}\" is not a key this app knows (use a letter, a digit, F1 to F24 or Space).";
                        return null;
                    }

                    key = code;
                    break;
            }
        }

        if (key == 0)
        {
            problem = "it needs a key after the modifiers, for example Ctrl+Alt+Space.";
            return null;
        }

        if ((modifiers & (Ctrl | Alt | Win)) == 0)
        {
            problem = "it needs Ctrl, Alt or Win, so ordinary typing never triggers it.";
            return null;
        }

        if (modifiers == (Ctrl | Alt | Shift) && key == 'K')
        {
            problem = "Ctrl+Alt+Shift+K is OmniWin's stop key and cannot be used for anything else.";
            return null;
        }

        return new Hotkey(modifiers, key);
    }

    private static uint? KeyCode(string name)
    {
        var n = name.ToUpperInvariant();
        if (n.Length == 1 && (char.IsAsciiLetterUpper(n[0]) || char.IsAsciiDigit(n[0]))) return n[0];
        if (n == "SPACE") return 0x20;
        if (n.Length is 2 or 3 && n[0] == 'F' && int.TryParse(n.AsSpan(1), out var f) && f is >= 1 and <= 24) return (uint)(0x70 + f - 1);
        return null;
    }
}
