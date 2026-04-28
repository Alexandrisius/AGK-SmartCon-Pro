using SmartCon.Core.Services.FamilyManager;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Core;

public sealed class FamilyNameNormalizerTests
{
    [Fact]
    public void Normalize_NullOrWhitespace_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, FamilyNameNormalizer.Normalize(null!));
        Assert.Equal(string.Empty, FamilyNameNormalizer.Normalize(""));
        Assert.Equal(string.Empty, FamilyNameNormalizer.Normalize("   "));
    }

    [Fact]
    public void Normalize_LowercasesInput()
    {
        Assert.Equal("pipe", FamilyNameNormalizer.Normalize("Pipe"));
        Assert.Equal("valve", FamilyNameNormalizer.Normalize("VALVE"));
    }

    [Fact]
    public void Normalize_RemovesDiacritics()
    {
        Assert.Equal("cafe", FamilyNameNormalizer.Normalize("Caf\u00e9"));
        Assert.Equal("element", FamilyNameNormalizer.Normalize("\u00c9l\u00e9ment"));
    }

    [Fact]
    public void Normalize_TrimsWhitespace()
    {
        Assert.Equal("test", FamilyNameNormalizer.Normalize("  test  "));
    }

    [Fact]
    public void Normalize_CyrillicPreserved()
    {
        Assert.Equal("\u0442\u0440\u0443\u0431\u0430", FamilyNameNormalizer.Normalize("\u0422\u0440\u0443\u0431\u0430"));
    }
}
