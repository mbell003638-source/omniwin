using System.Text.Json;
using OmniWin.Core;

namespace OmniWin.Core.Tests;

public sealed class ExpectationJsonTests
{
    private static Expectation Parse(string json) =>
        JsonSerializer.Deserialize<Expectation>(json, CoreJson.Options) ?? throw new InvalidOperationException("null");

    [Fact]
    public void Parses_the_contract_example()
    {
        var e = Parse("""{"kind":"window_opened","title_contains":"Save As"}""");
        Assert.Equal(new WindowOpened("Save As"), e);
    }

    [Fact]
    public void Kind_may_come_after_other_properties()
    {
        var e = Parse("""{"ref":"e5","value":"hello","kind":"value_equals"}""");
        Assert.Equal(new ValueEquals("e5", "hello"), e);
    }

    [Fact]
    public void Parses_nested_all_of_and_any_of()
    {
        var e = Parse("""
            {"kind":"all_of","items":[
              {"kind":"element_gone","ref":"e9"},
              {"kind":"any_of","items":[{"kind":"url_contains","value":"/done"},{"kind":"text_appears","value":"Saved","within":"w3"}]}
            ]}
            """);

        var all = Assert.IsType<AllOf>(e);
        Assert.Equal(2, all.Items.Count);
        Assert.Equal(new ElementGone("e9"), all.Items[0]);
        var any = Assert.IsType<AnyOf>(all.Items[1]);
        Assert.Equal(new UrlContains("/done"), any.Items[0]);
        Assert.Equal(new TextAppears("Saved", "w3"), any.Items[1]);
    }

    [Fact]
    public void Missing_required_value_is_an_error_not_a_null()
    {
        Assert.ThrowsAny<JsonException>(() => Parse("""{"kind":"element_gone"}"""));
    }

    [Fact]
    public void Unknown_kind_is_an_error()
    {
        Assert.ThrowsAny<JsonException>(() => Parse("""{"kind":"click_anywhere"}"""));
    }

    [Fact]
    public void Expect_spec_defaults_and_clamps_timeout()
    {
        var spec = JsonSerializer.Deserialize<ExpectSpec>(
            """{"condition":{"kind":"focus_on","ref":"e2"}}""", CoreJson.Options)!;
        Assert.Equal(new FocusOn("e2"), spec.Condition);
        Assert.Equal(2000, spec.TimeoutMs);

        var big = JsonSerializer.Deserialize<ExpectSpec>(
            """{"condition":{"kind":"focus_on","ref":"e2"},"timeout_ms":99999}""", CoreJson.Options)!;
        Assert.Equal(30000, big.EffectiveTimeoutMs);
    }

    [Fact]
    public void Round_trips_with_kind_discriminator()
    {
        Expectation original = new AnyOf([new WindowClosed("w4"), new ToggleIs("e7", "on")]);
        var json = JsonSerializer.Serialize(original, CoreJson.Options);
        Assert.Contains("\"kind\":\"any_of\"", json, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"toggle_is\"", json, StringComparison.Ordinal);

        var back = Assert.IsType<AnyOf>(Parse(json));
        Assert.Equal(new WindowClosed("w4"), back.Items[0]);
        Assert.Equal(new ToggleIs("e7", "on"), back.Items[1]);
    }
}
