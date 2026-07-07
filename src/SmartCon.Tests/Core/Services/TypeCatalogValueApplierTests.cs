using System.Globalization;
using SmartCon.Core.Services.Implementation;
using SmartCon.Core.Services.Interfaces;
using Xunit;

namespace SmartCon.Tests.Core.Services;

public sealed class TypeCatalogValueApplierTests
{
    private readonly TypeCatalogValueApplier _sut = new();

    [Fact]
    public void Apply_Text_ValidString_ReturnsAsIs()
    {
        var result = _sut.Apply("Hello", StorageTypeCode.StgText);
        Assert.Equal(TypeCatalogValueApplyStatus.Success, result.Status);
        Assert.Equal("Hello", result.Value);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Apply_Text_EmptyString_ReturnsAsIs()
    {
        var result = _sut.Apply("", StorageTypeCode.StgText);
        Assert.Equal(TypeCatalogValueApplyStatus.Success, result.Status);
        Assert.Equal("", result.Value);
    }

    [Fact]
    public void Apply_Text_NullValue_ReturnsInvalidFormat()
    {
        var result = _sut.Apply(null, StorageTypeCode.StgText);
        Assert.Equal(TypeCatalogValueApplyStatus.InvalidFormat, result.Status);
        Assert.Null(result.Value);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Apply_StgInt_ValidInteger_ReturnsSuccess()
    {
        var result = _sut.Apply("42", StorageTypeCode.StgInt);
        Assert.Equal(TypeCatalogValueApplyStatus.Success, result.Status);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void Apply_StgInt_NegativeNumber_Parses()
    {
        var result = _sut.Apply("-7", StorageTypeCode.StgInt);
        Assert.Equal(TypeCatalogValueApplyStatus.Success, result.Status);
        Assert.Equal(-7, result.Value);
    }

    [Fact]
    public void Apply_StgInt_NonNumeric_ReturnsInvalidFormat()
    {
        var result = _sut.Apply("abc", StorageTypeCode.StgInt);
        Assert.Equal(TypeCatalogValueApplyStatus.InvalidFormat, result.Status);
        Assert.Null(result.Value);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Apply_StgInt_EmptyString_ReturnsInvalidFormat()
    {
        var result = _sut.Apply("", StorageTypeCode.StgInt);
        Assert.Equal(TypeCatalogValueApplyStatus.InvalidFormat, result.Status);
    }

    [Fact]
    public void Apply_StgNumber_InvariantCulture_Parses()
    {
        var result = _sut.Apply("1.5", StorageTypeCode.StgNumber);
        Assert.Equal(TypeCatalogValueApplyStatus.Success, result.Status);
        Assert.Equal(1.5, result.Value);
    }

    [Fact]
    public void Apply_StgNumber_CurrentCulture_FallsBackForCommaDecimal()
    {
        var prev = CultureInfo.DefaultThreadCurrentCulture;
        try
        {
            CultureInfo.DefaultThreadCurrentCulture = new CultureInfo("ru-RU");
            var result = _sut.Apply("1,5", StorageTypeCode.StgNumber);
            Assert.Equal(TypeCatalogValueApplyStatus.Success, result.Status);
            Assert.Equal(1.5, result.Value);
        }
        finally
        {
            CultureInfo.DefaultThreadCurrentCulture = prev;
        }
    }

    [Fact]
    public void Apply_StgNumber_ScientificNotation_Parses()
    {
        var result = _sut.Apply("1.5e2", StorageTypeCode.StgNumber);
        Assert.Equal(TypeCatalogValueApplyStatus.Success, result.Status);
        Assert.Equal(150.0, result.Value);
    }

    [Fact]
    public void Apply_StgNumber_InvalidString_ReturnsInvalidFormat()
    {
        var result = _sut.Apply("not-a-number", StorageTypeCode.StgNumber);
        Assert.Equal(TypeCatalogValueApplyStatus.InvalidFormat, result.Status);
        Assert.Null(result.Value);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Apply_StgElementId_ValidLong_ReturnsSuccess()
    {
        var result = _sut.Apply("12345", StorageTypeCode.StgElementId);
        Assert.Equal(TypeCatalogValueApplyStatus.Success, result.Status);
        Assert.Equal(12345L, result.Value);
    }

    [Fact]
    public void Apply_StgElementId_InvalidString_ReturnsInvalidFormat()
    {
        var result = _sut.Apply("abc", StorageTypeCode.StgElementId);
        Assert.Equal(TypeCatalogValueApplyStatus.InvalidFormat, result.Status);
    }

    [Fact]
    public void Apply_StgNone_ReturnsUnsupportedStorageType()
    {
        var result = _sut.Apply("anything", StorageTypeCode.StgNone);
        Assert.Equal(TypeCatalogValueApplyStatus.UnsupportedStorageType, result.Status);
        Assert.Null(result.Value);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Apply_UnknownStorageTypeCode_ReturnsUnsupportedStorageType()
    {
        var result = _sut.Apply("anything", (StorageTypeCode)999);
        Assert.Equal(TypeCatalogValueApplyStatus.UnsupportedStorageType, result.Status);
        Assert.Contains("999", result.Error);
    }

    [Fact]
    public void Apply_StgInt_NullValue_ReturnsInvalidFormat()
    {
        var result = _sut.Apply(null, StorageTypeCode.StgInt);
        Assert.Equal(TypeCatalogValueApplyStatus.InvalidFormat, result.Status);
    }

    [Fact]
    public void Apply_StgNumber_NullValue_ReturnsInvalidFormat()
    {
        var result = _sut.Apply(null, StorageTypeCode.StgNumber);
        Assert.Equal(TypeCatalogValueApplyStatus.InvalidFormat, result.Status);
    }

    [Fact]
    public void Apply_StgElementId_NullValue_ReturnsInvalidFormat()
    {
        var result = _sut.Apply(null, StorageTypeCode.StgElementId);
        Assert.Equal(TypeCatalogValueApplyStatus.InvalidFormat, result.Status);
    }

    [Fact]
    public void Apply_DoesNotThrow_ForAnyInput()
    {
        var ex = Record.Exception(() =>
        {
            _sut.Apply(null, StorageTypeCode.StgNone);
            _sut.Apply("", StorageTypeCode.StgInt);
            _sut.Apply("garbage", StorageTypeCode.StgNumber);
            _sut.Apply("garbage", StorageTypeCode.StgElementId);
        });
        Assert.Null(ex);
    }
}
