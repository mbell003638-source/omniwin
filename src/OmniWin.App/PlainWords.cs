namespace OmniWin.App;

internal enum Outcome { Done, Unverified, NeedsAi, Refused, Stopped, Failed }

/// <summary>What a request came to, in words for the owner, plus what the box needs to know to offer Right / Wrong.</summary>
internal sealed record Explained(Outcome Outcome, string Headline, string? Detail, string? Recipe, bool InputSent, IReadOnlyList<string> Suggestions);

/// <summary>
/// Turns an OmniWin result into plain words. It only reads the result: the status and input_sent on the first line are OmniWin's
/// own, and this never upgrades them ("unverified" is never shown as done).
/// </summary>
internal static class PlainWords
{
    internal static string? Token(string line, string key)
    {
        foreach (var token in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith(key, StringComparison.Ordinal)) return token[key.Length..];
        }

        return null;
    }

    private static string? LineAfter(string[] lines, string prefix) =>
        lines.FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..].Trim();

    public static Explained Explain(string text)
    {
        var lines = (text ?? string.Empty).Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        var first = lines[0];
        var detail = LineAfter(lines, "detail:");
        var sent = Token(first, "input_sent=") == "true";
        var rule = LineAfter(lines, "routed rule=");
        string? recipe = null;
        if (rule is not null)
        {
            if (rule.StartsWith("recipe.", StringComparison.Ordinal)) recipe = rule["recipe.".Length..];
            else if (rule.StartsWith("decision.recipe.", StringComparison.Ordinal)) recipe = rule["decision.recipe.".Length..];
        }

        var suggestions = lines.Where(l => l.StartsWith("recipe: ", StringComparison.Ordinal)).Select(l => l["recipe: ".Length..]).ToList();

        if (first.StartsWith("needs_brain", StringComparison.Ordinal))
        {
            var reason = Token(first, "reason=");
            var headline = reason switch
            {
                "risky_words" => "This needs your AI: it has a word (delete, send, pay, install ...) OmniWin never handles by itself.",
                "recipe_not_ready" => "This needs your AI: the app for this task is not open or not ready.",
                "low_confidence" or "no_slots" or "model_unavailable" => "This needs your AI: OmniWin was not sure enough to do it by itself.",
                "ambiguous" => "This needs your AI: more than one thing matches these words.",
                "router_off" => "The quick commands are switched off in Settings.",
                _ => "This needs your AI: OmniWin has not learned it yet.",
            };
            return new Explained(Outcome.NeedsAi, headline, detail, null, false, suggestions);
        }

        var status = Token(first, "status=");
        if (Token(first, "stopped_at=") is { } step && status != "confirmed")
        {
            return new Explained(Outcome.Stopped,
                $"Stopped at step {step}: the screen is not what the learned task expects. Nothing else was clicked. Ask your AI to finish it; the task will be repaired.",
                detail, recipe, sent, suggestions);
        }

        var what = recipe is not null ? $"the learned task \"{recipe}\"" : "it";
        return status switch
        {
            "confirmed" => new Explained(Outcome.Done, recipe is not null ? $"Done: ran {what} and checked every step." : "Done, and checked.", detail, recipe, sent, suggestions),
            "unverified" => new Explained(Outcome.Unverified, $"Sent, but OmniWin could not check that {what} worked. Please look.", detail, recipe, sent, suggestions),
            "denied" => new Explained(Outcome.Refused, Token(first, "reason=") == "kill_switch" ? "OmniWin is stopped (the stop key was pressed)." : "Refused by the safety rules. Nothing was done.", detail, recipe, sent, suggestions),
            "blocked" or "stale" => new Explained(Outcome.Refused, sent ? "Stopped part-way. Please look at the screen." : "Not done: " + Reason(Token(first, "reason=")), detail, recipe, sent, suggestions),
            null => new Explained(Outcome.Failed, "OmniWin gave an answer this app does not understand.", first, null, false, suggestions),
            _ => new Explained(sent ? Outcome.Stopped : Outcome.Failed, sent ? "Something went wrong after input was sent. Please look at the screen." : "It did not work, and nothing was sent.", detail, recipe, sent, suggestions),
        };
    }

    private static string Reason(string? code) => code switch
    {
        "user_active" => "you were using the mouse or keyboard, so OmniWin waited. Try again.",
        "rate_limit" => "OmniWin is busy with another action. Try again in a moment.",
        "not_found" => "what it needs is not on screen.",
        "unsupported_action" => "this is not something OmniWin can do that way.",
        "secure_desktop" or "elevated_target" => "that window is protected by Windows.",
        null => "nothing was sent.",
        _ => code.Replace('_', ' ') + ".",
    };
}
