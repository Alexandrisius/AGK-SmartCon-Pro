using SmartCon.Core.Services.FamilyManager;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Core;

public sealed class FamilySearchNormalizerTests
{
    [Fact]
    public void Normalize_NullOrWhitespace_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, FamilySearchNormalizer.Normalize(null!));
        Assert.Equal(string.Empty, FamilySearchNormalizer.Normalize(""));
        Assert.Equal(string.Empty, FamilySearchNormalizer.Normalize("   "));
    }

    [Fact]
    public void Normalize_LowercasesInput()
    {
        Assert.Equal("hello", FamilySearchNormalizer.Normalize("HELLO"));
        Assert.Equal("world", FamilySearchNormalizer.Normalize("World"));
    }

    [Fact]
    public void Normalize_RemovesDiacritics()
    {
        Assert.Equal("cafe", FamilySearchNormalizer.Normalize("Caf\u00e9"));
        Assert.Equal("ubungs", FamilySearchNormalizer.Normalize("\u00dcbungs"));
    }

    [Fact]
    public void Normalize_TrimsWhitespace()
    {
        Assert.Equal("test", FamilySearchNormalizer.Normalize("  test  "));
    }

    [Fact]
    public void Normalize_CyrillicPreserved()
    {
        Assert.Equal("\u0442\u0440\u0443\u0431\u0430", FamilySearchNormalizer.Normalize("\u0422\u0440\u0443\u0431\u0430"));
    }

    [Fact]
    public void Tokenize_SplitsOnSpaces()
    {
        var tokens = FamilySearchNormalizer.Tokenize("hello world foo");
        Assert.Equal(3, tokens.Count);
        Assert.Equal("hello", tokens[0]);
        Assert.Equal("world", tokens[1]);
        Assert.Equal("foo", tokens[2]);
    }

    [Fact]
    public void Tokenize_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Empty(FamilySearchNormalizer.Tokenize(null!));
        Assert.Empty(FamilySearchNormalizer.Tokenize(""));
        Assert.Empty(FamilySearchNormalizer.Tokenize("   "));
    }

    [Fact]
    public void Tokenize_MultipleSpaces_ReturnsNonEmptyTokensOnly()
    {
        var tokens = FamilySearchNormalizer.Tokenize("a   b    c");
        Assert.Equal(3, tokens.Count);
        Assert.Equal("a", tokens[0]);
        Assert.Equal("b", tokens[1]);
        Assert.Equal("c", tokens[2]);
    }
}
