using OmniWin.Integration.Harness;

namespace OmniWin.Integration;

/// <summary>
/// Pure tests of the harness's own safety helpers: no process, no browser, no server, no desktop. They run in any session,
/// including a disconnected one.
/// </summary>
public sealed class HarnessSafetyTests
{
    private const string Marker = @"C:\Users\<you>\AppData\Local\Temp\omniwin-e2e\0123456789abcdef0123456789abcdef";

    [Fact]
    public void Only_edge_processes_with_this_sessions_data_folder_are_selected()
    {
        var processes = new (int, string)[]
        {
            (101, "\"msedge.exe\" --remote-debugging-port=0 --user-data-dir=\"" + Marker + @"\browser-profile"" about:blank"),
            (102, "\"msedge.exe\" --type=renderer --user-data-dir=\"" + Marker + @"\browser-profile"" --lang=en-US"),
            (103, "\"msedge.exe\" --profile-directory=Default"),                                                       // the owner's Edge
            (104, @"""msedge.exe"" --user-data-dir=""C:\Users\<you>\AppData\Local\Temp\omniwin-e2e\ffffffffffffffffffffffffffffffff\browser-profile"""),  // another session's
        };

        var owned = EdgeProcesses.SelectOwned(processes, Marker);

        Assert.Equal([101, 102], owned);
    }

    [Fact]
    public void Matching_ignores_letter_case()
    {
        var owned = EdgeProcesses.SelectOwned([(7, "msedge.exe --user-data-dir=" + Marker.ToUpperInvariant())], Marker);

        Assert.Equal([7], owned);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("msedge")]
    [InlineData(@"C:\Users")]                                                  // too short and not ours
    [InlineData(@"C:\Users\<you>\AppData\Local\Microsoft\Edge\User Data")] // a real browser folder: no "omniwin" in it
    [InlineData(@"relative\omniwin-e2e\abc")]
    public void A_marker_that_could_match_unrelated_processes_is_refused(string marker)
    {
        Assert.Throws<ArgumentException>(() => EdgeProcesses.SelectOwned([(1, "msedge.exe")], marker));
    }

    [Fact]
    public void The_listing_is_parsed_into_pid_and_command_line()
    {
        var list = EdgeProcesses.ParseListing("12|\"msedge.exe\" --a=1\r\nnoise without a bar\r\n34|\"msedge.exe\" --type=gpu | piped\r\n");

        Assert.Equal(2, list.Count);
        Assert.Equal(12, list[0].Pid);
        Assert.Equal("\"msedge.exe\" --a=1", list[0].CommandLine);
        Assert.Equal(34, list[1].Pid);
        Assert.Contains("| piped", list[1].CommandLine);
    }

    [Theory]
    [InlineData(640, 480, false)]    // the placeholder of a session with no display
    [InlineData(604, 1256, true)]    // a narrow Remote Desktop window is a real screen
    [InlineData(1536, 864, true)]
    [InlineData(0, 0, false)]
    [InlineData(800, 384, false)]    // 307,200 pixels: exactly the placeholder area, not larger
    public void A_screen_is_usable_only_when_it_is_larger_than_the_640x480_placeholder(int width, int height, bool usable)
    {
        Assert.Equal(usable, DesktopAvailability.IsUsableScreen(width, height));
    }

    [Fact]
    public void Live_tests_skip_with_a_reason_when_the_owner_has_not_asked_for_them()
    {
        if (Live.RequestedByOwner) return;   // in a live session this check is not applicable

        Assert.False(Live.Enabled);
        Assert.Contains("OMNIWIN_LIVE_SESSION=1", Live.WhyNot);
    }

    [Fact]
    public void The_skip_text_names_both_conditions()
    {
        Assert.Contains("OMNIWIN_LIVE_SESSION=1", Live.Skip);
        Assert.Contains("connected", Live.Skip);
    }
}
