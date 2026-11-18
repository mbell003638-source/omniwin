using System.Globalization;
using System.Text.RegularExpressions;

namespace OmniWin.Server.Router;

/// <summary>What a recognised command wants. The service turns it into a call of OmniWin's own tools.</summary>
internal abstract record RouteCommand
{
    /// <summary>Name of the rule that matched, for the result line (for example "media.pause").</summary>
    public string Rule { get; init; } = string.Empty;
}

internal sealed record MediaCommand(string Action) : RouteCommand;

internal sealed record VolumeSetCommand(int Percent) : RouteCommand;

/// <summary>Relative change: needs the current volume first.</summary>
internal sealed record VolumeStepCommand(int Delta) : RouteCommand;

internal sealed record MuteCommand(bool Mute) : RouteCommand;

internal sealed record MuteToggleCommand : RouteCommand;

/// <summary>A window action. <see cref="Target"/> is null for "this window" (the foreground window).</summary>
internal sealed record WindowCommand(string Action, string? Target) : RouteCommand;

internal sealed record LaunchCommand(string AppName) : RouteCommand;

internal sealed record WorkflowCommand(string Name) : RouteCommand;

/// <summary>A read: time, battery or volume.</summary>
internal sealed record ReadCommand(string Kind) : RouteCommand;

/// <summary>Why a request was not run locally. <see cref="Code"/> is a short machine word; the brain handles the request.</summary>
internal sealed record NotRouted(string Code, string Detail);

internal sealed record ParseOutcome(RouteCommand? Command, NotRouted? Why)
{
    public static ParseOutcome Matched(RouteCommand command) => new(command, null);

    public static ParseOutcome No(string code, string detail) => new(null, new NotRouted(code, detail));
}

/// <summary>
/// The strict phrase table (stage A of the router). A request is run only when ALL of these hold: it is short, it has no risky word,
/// and exactly ONE rule matches the whole sentence. Anything else is left to the brain. Pure text in, a command or a reason out:
/// it sends no input and calls nothing.
/// </summary>
internal static partial class PhraseRouter
{
    public const int MaxWords = 12;

    private static readonly string[] Fillers =
    [
        "hey", "ok", "okay", "please", "can you", "could you", "would you", "will you", "i want you to", "i would like to", "i want to",
        "just", "kindly", "go ahead and",
    ];

    private static readonly string[] Trailers = ["please", "for me", "now", "thanks", "thank you", "right now"];

    /// <summary>Words that make the request something for the brain (and for the safety rules) rather than a quick local command.</summary>
    [GeneratedRegex(@"\b(delet\w*|remov\w*|eras\w*|uninstall\w*|format\w*|shut ?down|restart\w*|reboot\w*|sign ?out|log ?off|logoff|lock\w*|password\w*|wip\w*|reset\w*|install\w*|download\w*|buy\w*|pay\w*|send\w*|submit\w*)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RiskyWord();

    /// <summary>True when already-normalised text holds a word the quick paths never handle (delete, install, send, pay and so on).</summary>
    public static bool HasRiskyWord(string normalized) => RiskyWord().IsMatch(normalized ?? string.Empty);

    /// <summary>Lower case, no punctuation, no filler: "Hey, could you please pause the music?" becomes "pause the music".</summary>
    public static string Normalize(string text)
    {
        var s = (text ?? string.Empty).Trim().ToLowerInvariant().Replace("'", string.Empty).Replace("’", string.Empty);
        s = NonWord().Replace(s, " ");
        s = Spaces().Replace(s, " ").Trim();
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var filler in Fillers)
            {
                if (s == filler || s.StartsWith(filler + " ", StringComparison.Ordinal))
                {
                    s = s[filler.Length..].TrimStart();
                    changed = true;
                }
            }

            foreach (var trailer in Trailers)
            {
                if (s.EndsWith(" " + trailer, StringComparison.Ordinal))
                {
                    s = s[..^(trailer.Length + 1)].TrimEnd();
                    changed = true;
                }
            }
        }

        return s;
    }

    [GeneratedRegex(@"[^\p{L}\p{N}%/ ._+-]")]
    private static partial Regex NonWord();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    private sealed record Rule(string Name, Regex Pattern, Func<Match, RouteCommand?> Make);

    private static Regex R(string pattern) => new("^(?:" + pattern + ")$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private const string Thing = @"(?:the |my |this |that )?";
    private const string MediaWord = @"(?:music|song|songs|video|media|playback|track|audio|podcast)";
    private const string NameChars = @"([a-z0-9][a-z0-9 ._+-]{0,40})";

    private static readonly Rule[] Rules =
    [
        new("media.play", R($@"(?:play|resume|unpause)(?: {Thing}{MediaWord})?"), _ => new MediaCommand("play")),
        new("media.pause", R($@"(?:pause|stop|halt)(?: {Thing}{MediaWord})?"), _ => new MediaCommand("pause")),
        new("media.toggle", R(@"play pause|play/pause|toggle (?:play(?:back)?|pause|media)"), _ => new MediaCommand("play_pause")),
        new("media.next", R($@"(?:next|skip)(?: {Thing}(?:track|song|one|video))?"), _ => new MediaCommand("next")),
        new("media.previous", R(@"(?:previous|prev)(?: (?:track|song|one))?|go back (?:a |one )?(?:track|song)|last (?:track|song)"), _ => new MediaCommand("previous")),

        new("volume.set", R(@"(?:set |turn |put |change )?(?:the )?(?:volume|sound|audio)(?: level)? (?:to |at )?(\d{1,3}) ?(?:%|percent)?"),
            // A number above 100 is not a volume: refuse it (the brain asks) instead of quietly clamping it.
            m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) is var percent and <= 100 ? new VolumeSetCommand(percent) : null),
        new("volume.word", R(@"(?:set |turn |put )?(?:the )?(?:volume|sound) (?:to )?(max|maximum|full|half|zero|min|minimum)"),
            m => new VolumeSetCommand(m.Groups[1].Value switch { "max" or "maximum" or "full" => 100, "half" => 50, _ => 0 })),
        new("volume.step", R(@"(?:turn |put |bring )?(?:the )?(?:volume|sound|audio) (up|down)(?: (?:by )?(\d{1,2}) ?(?:%|percent)?)?"),
            m => new VolumeStepCommand((m.Groups[1].Value == "up" ? 1 : -1) * (m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 10))),
        new("volume.louder", R(@"louder|turn it up|raise the volume|volume higher"), _ => new VolumeStepCommand(10)),
        new("volume.quieter", R(@"quieter|softer|turn it down|lower the volume|volume lower"), _ => new VolumeStepCommand(-10)),
        new("audio.mute", R($@"mute(?: {Thing}(?:sound|audio|volume|speakers|computer|pc))?"), _ => new MuteCommand(true)),
        new("audio.unmute", R($@"unmute(?: {Thing}(?:sound|audio|volume|speakers|computer|pc))?|turn (?:the )?sound on"), _ => new MuteCommand(false)),
        new("audio.toggle", R(@"toggle mute|mute unmute"), _ => new MuteToggleCommand()),

        new("window.focus", R($@"(?:switch|go|jump|alt tab) to {NameChars}|focus(?: on)? {NameChars}|bring up {NameChars}|bring {NameChars} (?:up|forward|to the front)"),
            m => Named(FirstGroup(m), name => new WindowCommand("focus", name))),
        new("window.minimize", R($@"minimi[sz]e(?: (?:the |this )?(?:current )?{NameChars}?)?"), m => Window("minimize", m)),
        new("window.maximize", R($@"maximi[sz]e(?: (?:the |this )?(?:current )?{NameChars}?)?"), m => Window("maximize", m)),
        new("window.restore", R($@"restore(?: (?:the |this )?(?:current )?{NameChars}?)?"), m => Window("restore", m)),
        new("window.close", R($@"close(?: (?:the |this )?(?:current )?{NameChars}?)?"), m => Window("close", m)),
        new("window.snap", R(@"snap(?: (?:this|the current))?(?: window)? (left|right)"), m => new WindowCommand("snap_" + m.Groups[1].Value, null)),

        // "open X" is an app launch; "run workflow X" is a workflow. The launch pattern refuses a name that starts with "workflow".
        new("app.launch", R($@"(?:open|launch|start) (?:the )?(?:app )?(?!workflow\b){NameChars}"),
            m => Named(m.Groups[1].Value, name => name is null ? null : new LaunchCommand(name))),
        new("workflow.run", R(@"(?:run|play|do|start) (?:the )?workflow (?:called |named )?([a-z0-9][a-z0-9 ._-]{0,60})"), m => new WorkflowCommand(Clean(m.Groups[1].Value)!)),

        new("read.time", R(@"what ?s the time|what time is it(?: now)?|(?:the )?time(?: now)?"), _ => new ReadCommand("time")),
        new("read.battery", R(@"battery|battery (?:level|status)|how much battery(?: (?:do i have|is left))?"), _ => new ReadCommand("battery")),
        new("read.volume", R(@"what ?s the volume|volume|volume level|how loud is it"), _ => new ReadCommand("volume")),
    ];

    private static string FirstGroup(Match m) => m.Groups.Cast<Group>().Skip(1).FirstOrDefault(g => g.Success)?.Value ?? string.Empty;

    private static RouteCommand? Window(string action, Match m) => Named(FirstGroup(m), name => new WindowCommand(action, name));

    /// <summary>
    /// A name that is really a media or volume word ("go to the next song", "start playing again", "make it louder") is never a window or an
    /// app: the rule does not match, and the sentence goes on to the brain (or to the Laya stage, which names the right intent).
    /// </summary>
    [GeneratedRegex(@"\b(?:song|songs|track|tracks|music|video|videos|playing|playback|next|previous|prev|last|again|louder|quieter|volume|sound|audio|mute|unmute|episode)\b")]
    private static partial Regex MediaOrVolumeWord();

    private static RouteCommand? Named(string raw, Func<string?, RouteCommand?> make)
    {
        var name = Clean(raw);
        return name is not null && MediaOrVolumeWord().IsMatch(name) ? null : make(name);
    }

    /// <summary>"this", "current", "it" and an empty name all mean the foreground window (null).</summary>
    private static string? Clean(string? name)
    {
        var n = TrailingNoun().Replace((name ?? string.Empty).Trim(), string.Empty).Trim();
        return n.Length == 0 || n is "this" or "current" or "it" or "window" or "this window" or "current window" or "the window" ? null : n;
    }

    /// <summary>Strict parse: the whole sentence must match exactly one rule.</summary>
    public static ParseOutcome Parse(string text)
    {
        var normalized = Normalize(text);
        if (normalized.Length == 0) return ParseOutcome.No("empty", "There is nothing to run.");
        if (normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > MaxWords)
        {
            return ParseOutcome.No("too_long", "Long requests are for the brain.");
        }

        if (RiskyWord().IsMatch(normalized))
        {
            return ParseOutcome.No("risky_words", "The request contains a word (delete, install, send, pay, restart and similar) that is never handled by the quick router.");
        }

        var matches = new List<(Rule Rule, Match Match)>();
        foreach (var rule in Rules)
        {
            var match = rule.Pattern.Match(normalized);
            if (match.Success) matches.Add((rule, match));
        }

        if (matches.Count == 0) return ParseOutcome.No("no_rule", "No quick rule matches this request.");
        if (matches.Count > 1) return ParseOutcome.No("ambiguous", "More than one quick rule matches: " + string.Join(", ", matches.Select(m => m.Rule.Name)) + ".");

        var (rule1, match1) = matches[0];
        var command = rule1.Make(match1);
        return command is null ? ParseOutcome.No("no_rule", "The rule could not read its values.") : ParseOutcome.Matched(command with { Rule = rule1.Name });
    }

    // ------------------------------------------------------------------ intent -> command (used for a model's guess; stage B)

    /// <summary>The intents a classifier may name. "none" is always allowed and means "leave it to the brain".</summary>
    public static readonly IReadOnlyList<string> Intents =
    [
        "media_play", "media_pause", "media_toggle", "media_next", "media_previous", "volume_set", "volume_up", "volume_down", "mute", "unmute",
        "window_focus", "window_minimize", "window_maximize", "window_restore", "window_close", "app_launch", "workflow_run",
        "read_time", "read_battery", "read_volume", "none",
    ];

    private static readonly HashSet<string> NameStopWords = new(StringComparer.Ordinal)
    {
        "switch", "to", "go", "open", "launch", "start", "run", "focus", "on", "bring", "up", "the", "my", "app", "window", "this", "that",
        "minimize", "minimise", "maximize", "maximise", "restore", "close", "workflow", "called", "named", "play", "do", "in", "a", "an", "it",
    };

    /// <summary>
    /// Builds the command for an intent that a classifier chose, reading the numbers and names out of the text with rules. Returns
    /// null when the slots cannot be read unambiguously (then the request goes to the brain): a classifier never invents a value.
    /// </summary>
    public static RouteCommand? FromIntent(string intent, string text)
    {
        var normalized = Normalize(text);
        if (normalized.Length == 0 || RiskyWord().IsMatch(normalized)) return null;
        var numbers = Number().Matches(normalized).Select(m => int.Parse(m.Value, CultureInfo.InvariantCulture)).ToList();
        string? Name()
        {
            var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !NameStopWords.Contains(w)).ToArray();
            return words.Length == 0 ? null : Clean(string.Join(' ', words));
        }

        RouteCommand? command = intent switch
        {
            "media_play" => new MediaCommand("play"),
            "media_pause" => new MediaCommand("pause"),
            "media_toggle" => new MediaCommand("play_pause"),
            "media_next" => new MediaCommand("next"),
            "media_previous" => new MediaCommand("previous"),
            "volume_set" when numbers.Count == 1 && numbers[0] is >= 0 and <= 100 => new VolumeSetCommand(numbers[0]),
            "volume_up" when numbers.Count <= 1 => new VolumeStepCommand(numbers.Count == 1 ? Math.Clamp(numbers[0], 1, 50) : 10),
            "volume_down" when numbers.Count <= 1 => new VolumeStepCommand(-(numbers.Count == 1 ? Math.Clamp(numbers[0], 1, 50) : 10)),
            "mute" => new MuteCommand(true),
            "unmute" => new MuteCommand(false),
            "window_focus" when Name() is { } n => new WindowCommand("focus", n),
            "window_minimize" => new WindowCommand("minimize", Name()),
            "window_maximize" => new WindowCommand("maximize", Name()),
            "window_restore" => new WindowCommand("restore", Name()),
            "window_close" when Name() is { } n => new WindowCommand("close", n),
            "app_launch" when Name() is { } n => new LaunchCommand(n),
            "workflow_run" when Name() is { } n => new WorkflowCommand(n),
            "read_time" => new ReadCommand("time"),
            "read_battery" => new ReadCommand("battery"),
            "read_volume" => new ReadCommand("volume"),
            _ => null,
        };
        return command is null ? null : command with { Rule = "laya." + intent };
    }

    [GeneratedRegex(@"\d{1,3}")]
    private static partial Regex Number();

    [GeneratedRegex(@" (?:window|app|application)$")]
    private static partial Regex TrailingNoun();
}
