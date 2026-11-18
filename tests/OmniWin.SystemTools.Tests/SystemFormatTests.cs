using OmniWin.Core;

namespace OmniWin.SystemTools.Tests;

public sealed class SystemFormatTests
{
    [Fact]
    public void Apps_list_starts_with_the_untrusted_header_and_quotes_names()
    {
        var text = SystemFormat.AppsList([new InstalledApp("Calc\nulator", "Microsoft.Calc!App", null)]);

        var lines = text.Split('\n');
        Assert.Equal(ResultFormatter.UntrustedHeader, lines[0]);
        Assert.StartsWith("\"Calc", lines[1]);
        Assert.Contains("id=\"Microsoft.Calc!App\"", lines[1]);
        Assert.Equal("apps: 1", lines[2]);
        Assert.Equal(3, lines.Length); // the embedded newline did not add a line
    }

    [Fact]
    public void Apps_list_is_capped_and_says_how_many_more()
    {
        var apps = Enumerable.Range(0, SystemFormat.MaxAppsListed + 25).Select(i => new InstalledApp("App" + i, "id" + i, null)).ToList();

        var text = SystemFormat.AppsList(apps);

        // header + 300 listed apps + the "apps: N (...)" line
        Assert.Equal(SystemFormat.MaxAppsListed + 2, text.Split('\n').Length);
        Assert.EndsWith("apps: " + apps.Count + " (25 more: narrow with filter)", text);
    }

    [Fact]
    public void Audio_is_one_line_with_a_quoted_device()
    {
        var text = SystemFormat.Audio(new AudioState(40, true, "Speakers \"Main\""), Timing.Total(1));

        Assert.StartsWith("audio volume=40 muted=true device=\"", text);
        Assert.DoesNotContain('\n', text);
    }

    [Theory]
    [InlineData("network", true)]
    [InlineData("NETWORK", true)]
    [InlineData("time", false)]
    [InlineData("battery", false)]
    public void Only_network_info_carries_the_untrusted_header(string kind, bool header)
    {
        var text = SystemFormat.SystemInfo(kind, "body");

        Assert.Equal(header, text.StartsWith(ResultFormatter.UntrustedHeader, StringComparison.Ordinal));
        Assert.EndsWith("body", text);
    }

    [Fact]
    public void Clipboard_first_line_has_the_label_and_never_the_text_and_the_header_comes_before_the_text()
    {
        const string secret = "my secret clipboard text";

        var text = SystemFormat.Clipboard(new ClipboardText(secret, secret.Length, false), Timing.Total(2));

        var lines = text.Split('\n');
        Assert.StartsWith($"clipboard chars={secret.Length} risk=risky total=", lines[0]);
        Assert.DoesNotContain("secret", lines[0]);
        Assert.Equal(ResultFormatter.UntrustedHeader, lines[1]);
        Assert.Equal(secret, lines[2]);
    }

    [Fact]
    public void Clipboard_says_when_the_text_was_cut()
    {
        var text = SystemFormat.Clipboard(new ClipboardText("abc", 99999, true), Timing.Total(1));

        Assert.Contains("chars=99999 truncated=true risk=risky", text.Split('\n')[0]);
    }
}
