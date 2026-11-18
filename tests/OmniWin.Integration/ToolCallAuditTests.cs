using OmniWin.Server.Hosting;

namespace OmniWin.Integration;

/// <summary>
/// Q6: the audit line carries the Risky label. The browser tools are not registered until M2, so no browser audit line
/// exists yet; these tests prove the plumbing from a result's first line to <see cref="OmniWin.Core.AuditEntry.Risk"/>.
/// </summary>
public sealed class ToolCallAuditTests
{
    [Fact]
    public void Line_with_risk_risky_is_recorded_as_risky()
    {
        var entry = ToolCallAudit.BuildEntry(DateTimeOffset.UtcNow, "browser_act", "{}",
            "status=unverified input_sent=true channel=api risk=risky total=12.4ms (resolve 1.0) step=s7", false, 12.4);

        Assert.Equal("risky", entry.Risk);
        Assert.Equal("unverified", entry.Status);
        Assert.True(entry.InputSent);
        Assert.Equal("s7", entry.StepId);
    }

    [Theory]
    [InlineData("status=confirmed input_sent=true channel=invoke total=12.4ms (resolve 1.0)")]
    [InlineData("status=denied input_sent=false reason=policy_forbidden total=1.0ms")]
    [InlineData("risk=normal status=confirmed input_sent=true total=1.0ms")]
    [InlineData(null)]
    public void Line_without_risk_risky_has_no_risk(string? firstLine)
    {
        var entry = ToolCallAudit.BuildEntry(DateTimeOffset.UtcNow, "desktop_act", "{}", firstLine, false, 1);

        Assert.Null(entry.Risk);
    }

    [Fact]
    public void Risk_label_does_not_change_the_other_audit_fields()
    {
        const string plain = "status=denied input_sent=false reason=policy_forbidden total=1.0ms";
        const string labelled = "status=denied input_sent=false reason=policy_forbidden risk=risky total=1.0ms";
        var a = ToolCallAudit.BuildEntry(DateTimeOffset.UnixEpoch, "t", "{}", plain, false, 1);
        var b = ToolCallAudit.BuildEntry(DateTimeOffset.UnixEpoch, "t", "{}", labelled, false, 1);

        Assert.Equal(a with { Risk = null }, b with { Risk = null });
        Assert.Null(a.Risk);
        Assert.Equal("risky", b.Risk);
    }
}
