namespace OmniWin.Desktop.Tests;

public sealed class DesktopTextTests
{
    [Theory]
    [InlineData("RichEditD2DPT", "plain café Ω", false)]
    [InlineData("RichEditD2DPT", "rocket 🚀", true)]
    [InlineData("RichEditD2DPT", "a\r\nb", true)]
    [InlineData("RichEditD2DPT", "a\nb", true)]
    [InlineData("Edit", "rocket 🚀\r\n", false)]
    public void Known_packet_editor_rejects_unsupported_text_before_typing(string nativeClass, string text, bool expected) =>
        Assert.Equal(expected, DesktopText.UnsupportedPacketText(nativeClass, text));

    [Theory]
    [InlineData("", 16, 0)]
    [InlineData("x", 16, 1)]
    [InlineData("x🚀z", 1, 1)]
    [InlineData("🚀z", 1, 2)]
    [InlineData("a🚀z", 2, 1)]
    [InlineData("ab🚀", 3, 2)]
    public void Unicode_batches_never_split_a_surrogate_pair_or_stall_at_scalar_size(string text, int maximum, int expected)
    {
        Assert.Equal(expected, DesktopText.UnicodeBatchLength(text, maximum));
    }

    [Theory]
    [InlineData("RichEditD2DPT", 1)]
    [InlineData("richeditd2dpt", 1)]
    [InlineData("Edit", 1)]
    [InlineData("WindowsForms10.EDIT.app.0", 1)]
    [InlineData("", 1)]
    public void Every_control_gets_one_scalar_per_batch_so_a_busy_target_cannot_merge_characters(string className, int expected)
    {
        Assert.Equal(expected, DesktopText.UnicodeBatchLimit(className));
    }

    [Theory]
    [InlineData("", 0, 0, "hello", "hello")]
    [InlineData("hello", 5, 5, " world", "hello world")]
    [InlineData("world", 0, 0, "hello ", "hello world")]
    [InlineData("hello world", 5, 5, " brave", "hello brave world")]
    [InlineData("hello world", 6, 11, "OmniWin", "hello OmniWin")]
    [InlineData("hello world", 0, 11, "replacement", "replacement")]
    [InlineData("hello world", 6, 11, "", "hello ")]
    [InlineData("first\r\nsecond", 7, 13, "last", "first\r\nlast")]
    [InlineData("A🚀B", 1, 3, "Ω", "AΩB")]
    [InlineData("A🚀B", 3, 3, " café", "A🚀 caféB")]
    public void Typing_inserts_at_the_caret_or_replaces_the_selected_range(string original, int start, int end,
        string insertion, string expected)
    {
        Assert.Equal(expected, DesktopText.ExpectedAfterInsertion(original, start, end, insertion));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(0, null)]
    [InlineData(null, 0)]
    [InlineData(-1, 0)]
    [InlineData(1, 0)]
    [InlineData(0, 6)]
    [InlineData(6, 6)]
    public void Missing_or_invalid_selection_keeps_verification_inconclusive(int? start, int? end)
    {
        Assert.Null(DesktopText.ExpectedAfterInsertion("hello", start, end, "world"));
    }

    [Theory]
    [InlineData(2, 2)]
    [InlineData(0, 2)]
    [InlineData(2, 4)]
    public void Selection_offsets_inside_a_surrogate_pair_are_not_assumed_to_be_valid(int start, int end)
    {
        Assert.Null(DesktopText.ExpectedAfterInsertion("A🚀B", start, end, "x"));
    }

    [Fact]
    public void A_provider_range_must_reconstruct_the_same_document_readback()
    {
        Assert.Equal("hello OmniWin", DesktopText.ExpectedFromRanges("hello world", "hello ", "world", "", "OmniWin"));
        Assert.Equal("hello brave world", DesktopText.ExpectedFromRanges("hello world", "hello", "", " world", " brave"));
        Assert.Null(DesktopText.ExpectedFromRanges("hello world", "HELLO ", "world", "", "OmniWin"));
        Assert.Null(DesktopText.ExpectedFromRanges("hello world", "hello ", "WORLD", "", "OmniWin"));
        Assert.Null(DesktopText.ExpectedFromRanges("hello world", "hello ", "world", "\r", "OmniWin"));
        Assert.Null(DesktopText.ExpectedFromRanges("hello world", "hello ", "", "", "OmniWin"));
    }

    [Fact]
    public void Oversized_capture_has_no_default_text_expectation()
    {
        var original = new string('x', DesktopText.MaxCapturedCharacters + 1);
        Assert.Null(DesktopText.ExpectedAfterInsertion(original, 0, 0, "y"));
    }

    [Fact]
    public void A_capture_at_the_limit_can_replace_text_without_exceeding_the_bound()
    {
        var original = new string('x', DesktopText.MaxCapturedCharacters);
        var expected = DesktopText.ExpectedAfterInsertion(original, 0, 1, "y");

        Assert.NotNull(expected);
        Assert.Equal(original.Length, expected.Length);
        Assert.Equal('y', expected[0]);
        Assert.Null(DesktopText.ExpectedAfterInsertion(original, original.Length, original.Length, "y"));
    }

    [Fact]
    public void Range_reconstruction_accepts_source_spans_without_copying_the_unchanged_suffix()
    {
        const string original = "first selected last";
        var result = DesktopText.ExpectedFromRanges(original, original.AsSpan(0, 6), original.AsSpan(6, 8),
            original.AsSpan(14), "changed");

        Assert.Equal("first changed last", result);
    }

    [Fact]
    public void Inserting_the_requested_text_does_not_imply_replacing_the_whole_field()
    {
        var expected = DesktopText.ExpectedAfterInsertion("existing ", 9, 9, "text");
        Assert.Equal("existing text", expected);
        Assert.NotEqual("text", expected);
    }
}
