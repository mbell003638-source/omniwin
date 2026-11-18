using OmniWin.Core;
using OmniWin.Workflows;
using Xunit;

namespace OmniWin.Core.Tests;

/// <summary>Reviewer, 2026-11-18: a saved step keeps the expectation its first run met, so a replay can prove the step the same way.</summary>
public sealed class SavedExpectationTests
{
    [Fact]
    public void A_url_expectation_is_kept_and_comes_back_on_replay()
    {
        var call = new StepCall("browser_act", Ref: "b81", Gen: 2, Action: "press", Value: "Enter",
            Expect: new ExpectSpec(new UrlContains("q="), 8000));

        var (action, args, _) = StepTools.Describe(call, null);
        var replayed = StepTools.ToCall("browser_act", action, args, null);

        Assert.True(args.ContainsKey("expect"));
        var back = Assert.IsType<UrlContains>(replayed.Expect!.Condition);
        Assert.Equal("q=", back.Value);
        Assert.Equal(8000, replayed.Expect.TimeoutMs);
    }

    [Fact]
    public void A_pressed_key_name_is_a_literal_not_a_parameter()
    {
        var (action, args, hasValue) = StepTools.Describe(new StepCall("browser_act", Ref: "b1", Gen: 1, Action: "press", Value: "Enter"), null);

        Assert.False(hasValue);
        Assert.Equal("Enter", StepTools.ToCall("browser_act", action, args, null).Value);
    }

    [Fact]
    public void A_window_opened_expectation_is_kept()
    {
        var call = new StepCall("desktop_keys", Keys: "win+n", Expect: new ExpectSpec(new WindowOpened("Notification Center")));
        var (action, args, _) = StepTools.Describe(call, null);

        Assert.IsType<WindowOpened>(StepTools.ToCall("desktop_keys", action, args, null).Expect!.Condition);
    }

    [Fact]
    public void Expectations_that_could_hold_typed_text_or_a_session_window_id_are_never_saved()
    {
        foreach (var condition in new Expectation[] { new TextAppears("my secret note"), new ValueEquals("b1", "typed"), new WindowClosed("w4") })
        {
            var (_, args, _) = StepTools.Describe(new StepCall("browser_act", Ref: "b1", Gen: 1, Action: "type", Value: "x", Expect: new ExpectSpec(condition)), null);
            Assert.False(args.ContainsKey("expect"));
        }
    }

    [Fact]
    public void A_hand_edited_expectation_is_ignored_not_trusted()
    {
        var args = new Dictionary<string, string> { ["expect"] = "{\"condition\":{\"kind\":\"text_appears\",\"value\":\"x\"}}" };
        Assert.Null(StepTools.ToCall("browser_act", "click", args, null).Expect);

        args["expect"] = "not json";
        Assert.Null(StepTools.ToCall("browser_act", "click", args, null).Expect);
    }
}
