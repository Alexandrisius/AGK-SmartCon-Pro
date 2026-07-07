using SmartCon.Core.Models.FamilyManager;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Models;

/// <summary>
/// Unit tests for SharedFamilyDecisionRequest record.
/// Issue #67 — record carries the data needed to ask the user
/// how to handle a single conflicting shared nested family.
/// </summary>
public sealed class SharedFamilyDecisionRequestTests
{
    [Fact]
    public void Constructor_StoresAllFields()
    {
        var request = new SharedFamilyDecisionRequest(
            SharedFamilyName: "M_Flange",
            IsFamilyInUse: true,
            ParentFamilyName: "M_Pipe");

        Assert.Equal("M_Flange", request.SharedFamilyName);
        Assert.True(request.IsFamilyInUse);
        Assert.Equal("M_Pipe", request.ParentFamilyName);
    }

    [Fact]
    public void Equality_TwoRequestsWithSameFields_AreEqual()
    {
        var a = new SharedFamilyDecisionRequest("M_Flange", false, "M_Pipe");
        var b = new SharedFamilyDecisionRequest("M_Flange", false, "M_Pipe");

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Equality_DifferentInUse_NotEqual()
    {
        var a = new SharedFamilyDecisionRequest("M_Flange", false, "M_Pipe");
        var b = new SharedFamilyDecisionRequest("M_Flange", true, "M_Pipe");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Equality_DifferentParent_NotEqual()
    {
        var a = new SharedFamilyDecisionRequest("M_Flange", false, "M_Pipe");
        var b = new SharedFamilyDecisionRequest("M_Flange", false, "M_Valve");

        Assert.NotEqual(a, b);
    }
}
