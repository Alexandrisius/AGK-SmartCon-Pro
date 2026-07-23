using SmartCon.Core.Services.Implementation;
using Xunit;

namespace SmartCon.Tests.Core.Services;

/// <summary>
/// Unit tests for <see cref="UnitSymbolFixup"/> — Autodesk RU localization
/// renders the bar unit as "бары", correct Russian display is "бар".
/// </summary>
public sealed class UnitSymbolFixupTests
{
    [Fact]
    public void Correct_TrailingБары_ReplacedWithБар()
    {
        Assert.Equal("16 бар", UnitSymbolFixup.Correct("16 бары"));
    }

    [Fact]
    public void Correct_TrailingБарыWithDecimal_ReplacedWithБар()
    {
        Assert.Equal("16,0 бар", UnitSymbolFixup.Correct("16,0 бары"));
    }

    [Fact]
    public void Correct_AlreadyCorrect_Unchanged()
    {
        Assert.Equal("16 бар", UnitSymbolFixup.Correct("16 бар"));
    }

    [Fact]
    public void Correct_EnglishBar_Unchanged()
    {
        Assert.Equal("16 bar", UnitSymbolFixup.Correct("16 bar"));
    }

    [Fact]
    public void Correct_OtherUnits_Unchanged()
    {
        Assert.Equal("300 мм", UnitSymbolFixup.Correct("300 мм"));
        Assert.Equal("50 mm", UnitSymbolFixup.Correct("50 mm"));
        Assert.Equal("0,00 л/с", UnitSymbolFixup.Correct("0,00 л/с"));
        Assert.Equal("30,00°", UnitSymbolFixup.Correct("30,00°"));
    }

    [Fact]
    public void Correct_БарыWithoutLeadingSpace_Unchanged()
    {
        Assert.Equal("бары", UnitSymbolFixup.Correct("бары"));
    }

    [Fact]
    public void Correct_БарыInTheMiddle_Unchanged()
    {
        Assert.Equal("бары 16", UnitSymbolFixup.Correct("бары 16"));
    }

    [Fact]
    public void Correct_Null_ReturnsNull()
    {
        Assert.Null(UnitSymbolFixup.Correct(null));
    }

    [Fact]
    public void Correct_Empty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, UnitSymbolFixup.Correct(string.Empty));
    }
}
