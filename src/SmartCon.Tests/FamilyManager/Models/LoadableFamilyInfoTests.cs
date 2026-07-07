using SmartCon.Core.Models.FamilyManager;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Models;

public sealed class LoadableFamilyInfoTests
{
    [Fact]
    public void Constructor_StoresAllFields()
    {
        var sut = new LoadableFamilyInfo(
            FamilyName: "PPR_Отвод_DN50",
            FamilyUniqueId: "abc-123",
            CategoryName: "Pipe Fittings",
            TypeCount: 3);

        Assert.Equal("PPR_Отвод_DN50", sut.FamilyName);
        Assert.Equal("abc-123", sut.FamilyUniqueId);
        Assert.Equal("Pipe Fittings", sut.CategoryName);
        Assert.Equal(3, sut.TypeCount);
    }

    [Fact]
    public void Equality_SameValues_AreEqual()
    {
        var a = new LoadableFamilyInfo("Family", "uid", "Cat", 1);
        var b = new LoadableFamilyInfo("Family", "uid", "Cat", 1);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Equality_DifferentTypeCount_NotEqual()
    {
        var a = new LoadableFamilyInfo("Family", "uid", "Cat", 1);
        var b = new LoadableFamilyInfo("Family", "uid", "Cat", 2);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Equality_DifferentName_NotEqual()
    {
        var a = new LoadableFamilyInfo("Family A", "uid", "Cat", 1);
        var b = new LoadableFamilyInfo("Family B", "uid", "Cat", 1);
        Assert.NotEqual(a, b);
    }
}
