using System.Text.Json.Serialization;

namespace OmniWin.Core;

// ---------- expectations (contract §11) ----------
// JSON form as the model sends it: {"kind":"window_opened","title_contains":"Save As"}.
// Deserialize with CoreJson.Options (snake_case, "kind" may appear anywhere in the object).

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(WindowOpened), "window_opened")]
[JsonDerivedType(typeof(WindowClosed), "window_closed")]
[JsonDerivedType(typeof(ElementAppears), "element_appears")]
[JsonDerivedType(typeof(ElementGone), "element_gone")]
[JsonDerivedType(typeof(ValueEquals), "value_equals")]
[JsonDerivedType(typeof(FocusOn), "focus_on")]
[JsonDerivedType(typeof(ToggleIs), "toggle_is")]
[JsonDerivedType(typeof(UrlContains), "url_contains")]
[JsonDerivedType(typeof(TextAppears), "text_appears")]
[JsonDerivedType(typeof(AllOf), "all_of")]
[JsonDerivedType(typeof(AnyOf), "any_of")]
public abstract record Expectation;

public sealed record WindowOpened(string? TitleContains = null, string? Process = null) : Expectation;
public sealed record WindowClosed(string Window) : Expectation;                        // "w4"
public sealed record ElementAppears(string? Role = null, string? NameContains = null, string? Within = null) : Expectation;
public sealed record ElementGone(string Ref) : Expectation;
public sealed record ValueEquals(string Ref, string Value) : Expectation;
public sealed record FocusOn(string Ref) : Expectation;
public sealed record ToggleIs(string Ref, string State) : Expectation;                 // "on" | "off" | "mixed"
public sealed record UrlContains(string Value, string? Tab = null) : Expectation;      // browser only
public sealed record TextAppears(string Value, string? Within = null) : Expectation;   // "w3" / "t1" / ref
public sealed record AllOf(IReadOnlyList<Expectation> Items) : Expectation;
public sealed record AnyOf(IReadOnlyList<Expectation> Items) : Expectation;

public sealed record ExpectSpec(Expectation Condition, int TimeoutMs = ExpectSpec.DefaultTimeoutMs)
{
    public const int DefaultTimeoutMs = 2000;
    public const int MaxTimeoutMs = 30000;

    /// <summary>Timeout clamped to 1..30000 ms.</summary>
    public int EffectiveTimeoutMs => Math.Clamp(TimeoutMs, 1, MaxTimeoutMs);
}
