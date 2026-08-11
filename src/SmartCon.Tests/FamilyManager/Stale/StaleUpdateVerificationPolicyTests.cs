using SmartCon.FamilyManager.Services.Stale;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Stale;

/// <summary>
/// #209 round-3 (validator findings Critical #1/#2): the tri-state
/// verification arbitration — "not applicable" (<c>null</c>) must never
/// be treated as "verified" (would skip/mask reloads in the PROJECT
/// context), only explicit match/mismatch drive decisions.
/// </summary>
public class StaleUpdateVerificationPolicyTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void ShouldSkipReload_OnlyExplicitMatch(bool? verified, bool expected)
    {
        Assert.Equal(expected, StaleUpdateVerificationPolicy.ShouldSkipReload(verified));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void ShouldAcceptFailedReload_OnlyExplicitMatch(bool? verified, bool expected)
    {
        Assert.Equal(expected, StaleUpdateVerificationPolicy.ShouldAcceptFailedReload(verified));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void ShouldFailSuccessfulReload_OnlyExplicitMismatch(bool? verified, bool expected)
    {
        Assert.Equal(expected, StaleUpdateVerificationPolicy.ShouldFailSuccessfulReload(verified));
    }
}
