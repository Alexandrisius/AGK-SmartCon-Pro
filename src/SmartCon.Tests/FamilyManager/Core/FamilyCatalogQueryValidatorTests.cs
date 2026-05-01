using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.FamilyManager;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Core;

public sealed class FamilyCatalogQueryValidatorTests
{
    [Fact]
    public void Validate_ClampsNegativeOffsetToZero()
    {
        var query = new FamilyCatalogQuery(null, null, null, null, null, FamilyCatalogSort.NameAsc, -5, 50);
        var result = FamilyCatalogQueryValidator.Validate(query);
        Assert.Equal(0, result.Offset);
    }

    [Fact]
    public void Validate_ClampsZeroLimitTo50()
    {
        var query = new FamilyCatalogQuery(null, null, null, null, null, FamilyCatalogSort.NameAsc, 0, 0);
        var result = FamilyCatalogQueryValidator.Validate(query);
        Assert.Equal(50, result.Limit);
    }

    [Fact]
    public void Validate_ClampsLargeLimitTo500()
    {
        var query = new FamilyCatalogQuery(null, null, null, null, null, FamilyCatalogSort.NameAsc, 0, 1000);
        var result = FamilyCatalogQueryValidator.Validate(query);
        Assert.Equal(500, result.Limit);
    }

    [Fact]
    public void Validate_ValidQuery_Unchanged()
    {
        var query = new FamilyCatalogQuery("pipe", "Pipes", null, null, null, FamilyCatalogSort.NameAsc, 10, 100);
        var result = FamilyCatalogQueryValidator.Validate(query);
        Assert.Equal("pipe", result.SearchText);
        Assert.Equal("Pipes", result.CategoryFilter);
        Assert.Equal(10, result.Offset);
        Assert.Equal(100, result.Limit);
    }

    [Fact]
    public void Validate_NullQuery_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => FamilyCatalogQueryValidator.Validate(null!));
    }
}
