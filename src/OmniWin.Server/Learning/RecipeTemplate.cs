using System.Text;
using System.Text.RegularExpressions;
using OmniWin.Server.Router;

namespace OmniWin.Server.Learning;

/// <summary>
/// A request wording with holes: <c>open {file} in word</c>. Strict on purpose (contract 2.6): the whole sentence must match, a
/// template needs at least two plain words, two holes never touch, and a hole takes 1 to 60 ordinary characters. So a template can
/// never match "anything", and a value is always text the user really wrote.
/// </summary>
internal sealed partial class RecipeTemplate
{
    public const int MinLiteralWords = 2;
    public const int MaxValueChars = 60;

    private readonly Regex _pattern;

    private RecipeTemplate(string text, IReadOnlyList<string> slots, Regex pattern)
    {
        Text = text;
        Slots = slots;
        _pattern = pattern;
    }

    /// <summary>Canonical form: normalised words and <c>{slot}</c> holes.</summary>
    public string Text { get; }

    public IReadOnlyList<string> Slots { get; }

    [GeneratedRegex(@"\{([a-z][a-z0-9_]{0,31})\}", RegexOptions.CultureInvariant)]
    private static partial Regex SlotPattern();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9 ._+\-]{0,59}$", RegexOptions.CultureInvariant)]
    private static partial Regex ValuePattern();

    [GeneratedRegex(@"[{}]")]
    private static partial Regex Brace();

    /// <summary>The router's normal form, without the full stop that ends a sentence ("open notes in word." is "open notes in word").</summary>
    public static string Normalize(string? text) => PhraseRouter.Normalize(text ?? string.Empty).TrimEnd('.', ' ');

    /// <summary>Parses what a caller wrote or what a card stores. Null with a reason when it breaks a rule.</summary>
    public static RecipeTemplate? Parse(string? raw, out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            problem = "The template is empty.";
            return null;
        }

        // Swap each hole for a marker word that survives normalising, normalise the sentence once, then cut it at the markers.
        var slots = new List<string>();
        var marked = SlotPattern().Replace(raw, m =>
        {
            slots.Add(m.Groups[1].Value);
            return $" zzslot{slots.Count - 1}zz ";
        });
        if (Brace().IsMatch(marked))
        {
            problem = "A slot is written {name}: a lower case letter, then lower case letters, digits or '_'.";
            return null;
        }

        if (slots.Distinct(StringComparer.Ordinal).Count() != slots.Count)
        {
            problem = "A slot name is used twice.";
            return null;
        }

        var words = Normalize(marked).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var canonical = new StringBuilder();
        var pattern = new StringBuilder("^");
        var literalWords = 0;
        var previousWasSlot = false;
        var seen = 0;
        for (var i = 0; i < words.Length; i++)
        {
            var word = words[i];
            if (i > 0)
            {
                canonical.Append(' ');
                pattern.Append(' ');
            }

            var marker = MarkerIndex(word);
            if (marker >= 0)
            {
                if (previousWasSlot)
                {
                    problem = "Two slots stand side by side; put a word between them.";
                    return null;
                }

                canonical.Append('{').Append(slots[marker]).Append('}');
                pattern.Append("(?<").Append(slots[marker]).Append(">.+?)");
                previousWasSlot = true;
                seen++;
            }
            else
            {
                canonical.Append(word);
                pattern.Append(Regex.Escape(word));
                previousWasSlot = false;
                literalWords++;
            }
        }

        if (seen != slots.Count)
        {
            problem = "A slot was lost while reading the template (it must stand as its own word).";
            return null;
        }

        if (literalWords < MinLiteralWords)
        {
            problem = $"A template needs at least {MinLiteralWords} plain words besides its slots.";
            return null;
        }

        pattern.Append('$');
        return new RecipeTemplate(canonical.ToString(), slots, new Regex(pattern.ToString(), RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)));
    }

    private static int MarkerIndex(string word) =>
        word.StartsWith("zzslot", StringComparison.Ordinal) && word.EndsWith("zz", StringComparison.Ordinal) &&
        int.TryParse(word.AsSpan(6, word.Length - 8), out var n) ? n : -1;

    /// <summary>
    /// Matches an already-normalised request. Returns the slot values, or null. <paramref name="original"/> is the request as it was
    /// written: when a value is found there exactly once (ignoring case), that spelling is returned, so "Invoice-42" keeps its capitals.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Match(string normalized, string? original = null)
    {
        Match match;
        try
        {
            match = _pattern.Match(normalized);
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }

        if (!match.Success) return null;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var slot in Slots)
        {
            var value = match.Groups[slot].Value.Trim();
            if (!IsValue(value)) return null;
            values[slot] = AsWritten(value, original);
        }

        return values;
    }

    /// <summary>True when a slot value is acceptable text (used when a value comes from somewhere other than a match).</summary>
    public static bool IsValue(string value)
    {
        if (!ValuePattern().IsMatch(value)) return false;
        var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // A value is a name, not a second request: at most five words and no joining word ("bob and then close it" is for the AI).
        return words.Length <= MaxValueWords && !words.Any(JoiningWords.Contains);
    }

    public const int MaxValueWords = 5;

    private static readonly HashSet<string> JoiningWords = new(StringComparer.Ordinal) { "and", "then", "also", "but", "after", "before", "plus" };

    private static string AsWritten(string value, string? original)
    {
        if (string.IsNullOrEmpty(original)) return value;
        var first = original.IndexOf(value, StringComparison.OrdinalIgnoreCase);
        if (first < 0) return value;
        var second = original.IndexOf(value, first + 1, StringComparison.OrdinalIgnoreCase);
        return second >= 0 ? value : original.Substring(first, value.Length);
    }
}
