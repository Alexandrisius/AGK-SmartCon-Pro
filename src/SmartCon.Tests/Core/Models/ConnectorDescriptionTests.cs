using SmartCon.Core.Models;
using Xunit;

namespace SmartCon.Tests.Core.Models;

public sealed class ConnectorDescriptionTests
{
    // ── Parse: полный формат "code.name.description" ──────────────────────

    [Fact]
    public void Parse_FullTriple_ReturnsAllSegments()
    {
        var d = ConnectorDescription.Parse("1.Резьба наружная.ГОСТ 6357-81");

        Assert.Equal(1, d.Code.Value);
        Assert.True(d.IsDefined);
        Assert.Equal("Резьба наружная", d.Name);
        Assert.Equal("ГОСТ 6357-81", d.Description);
    }

    [Fact]
    public void Parse_CodeAndName_NoDescription()
    {
        var d = ConnectorDescription.Parse("42.Муфта ПП");

        Assert.Equal(42, d.Code.Value);
        Assert.Equal("Муфта ПП", d.Name);
        Assert.Equal(string.Empty, d.Description);
    }

    [Fact]
    public void Parse_CodeOnly_EmptyNameAndDescription()
    {
        var d = ConnectorDescription.Parse("999");

        Assert.Equal(999, d.Code.Value);
        Assert.Equal(string.Empty, d.Name);
        Assert.Equal(string.Empty, d.Description);
    }

    [Fact]
    public void Parse_CodeWithTrailingDot_EmptyNameAndDescription()
    {
        var d = ConnectorDescription.Parse("1.");

        Assert.Equal(1, d.Code.Value);
        Assert.Equal(string.Empty, d.Name);
        Assert.Equal(string.Empty, d.Description);
    }

    [Fact]
    public void Parse_DescriptionPreservesInnerDots()
    {
        // Третий сегмент может содержать точки (ГОСТы, разделы, версии).
        var d = ConnectorDescription.Parse("1.Резьба.ГОСТ 6357-81 §4.2.5");

        Assert.Equal(1, d.Code.Value);
        Assert.Equal("Резьба", d.Name);
        Assert.Equal("ГОСТ 6357-81 §4.2.5", d.Description);
    }

    [Fact]
    public void Parse_TrimsWhitespaceOnAllSegments()
    {
        var d = ConnectorDescription.Parse("  1 .  Резьба  .  ГОСТ 6357  ");

        Assert.Equal(1, d.Code.Value);
        Assert.Equal("Резьба", d.Name);
        Assert.Equal("ГОСТ 6357", d.Description);
    }

    // ── Parse: невалидные значения → Undefined ────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_NullOrWhitespace_ReturnsUndefined(string? raw)
    {
        var d = ConnectorDescription.Parse(raw);
        Assert.Equal(ConnectorDescription.Undefined, d);
        Assert.False(d.IsDefined);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("Резьба наружная")]
    [InlineData("0")]
    [InlineData("0.ЛюбоеИмя.ЛюбоеОписание")]
    public void Parse_InvalidOrZeroCode_ReturnsUndefined(string raw)
    {
        var d = ConnectorDescription.Parse(raw);
        Assert.Equal(ConnectorDescription.Undefined, d);
        Assert.False(d.IsDefined);
    }

    // ── ToString: round-trip ──────────────────────────────────────────────

    [Fact]
    public void ToString_AllSegments_ProducesParseableString()
    {
        var original = new ConnectorDescription(
            new ConnectionTypeCode(1), "Резьба", "ГОСТ 6357");
        var roundTrip = ConnectorDescription.Parse(original.ToString());

        Assert.Equal(original.Code.Value, roundTrip.Code.Value);
        Assert.Equal(original.Name, roundTrip.Name);
        Assert.Equal(original.Description, roundTrip.Description);
    }

    [Fact]
    public void ToString_OnlyCode_ProducesParseableString()
    {
        var original = new ConnectorDescription(new ConnectionTypeCode(7), "", "");
        var roundTrip = ConnectorDescription.Parse(original.ToString());

        Assert.Equal(7, roundTrip.Code.Value);
        Assert.Equal(string.Empty, roundTrip.Name);
        Assert.Equal(string.Empty, roundTrip.Description);
    }

    // ── Matches: пустое != непустое (строго) ──────────────────────────────

    [Fact]
    public void Matches_AllFieldsEqual_ReturnsTrue()
    {
        var d = ConnectorDescription.Parse("1.Резьба.ГОСТ 6357");
        var def = new ConnectorTypeDefinition { Code = 1, Name = "Резьба", Description = "ГОСТ 6357" };

        Assert.True(d.Matches(def));
    }

    [Fact]
    public void Matches_CodeDiffers_ReturnsFalse()
    {
        var d = ConnectorDescription.Parse("1.Резьба.ГОСТ 6357");
        var def = new ConnectorTypeDefinition { Code = 2, Name = "Резьба", Description = "ГОСТ 6357" };

        Assert.False(d.Matches(def));
    }

    [Fact]
    public void Matches_NameDiffers_ReturnsFalse()
    {
        // Issue #64: одинаковый code, но разный name → НЕ совпадает → MiniTypeSelector вызывается.
        var d = ConnectorDescription.Parse("1.Резьба наружная.ГОСТ 6357");
        var def = new ConnectorTypeDefinition { Code = 1, Name = "Резьба", Description = "ГОСТ 6357" };

        Assert.False(d.Matches(def));
    }

    [Fact]
    public void Matches_DescriptionDiffers_ReturnsFalse()
    {
        var d = ConnectorDescription.Parse("1.Резьба.ГОСТ 6357");
        var def = new ConnectorTypeDefinition { Code = 1, Name = "Резьба", Description = "ГОСТ 3262" };

        Assert.False(d.Matches(def));
    }

    [Fact]
    public void Matches_EmptyInDefinitionNonEmptyInDescription_ReturnsFalse()
    {
        // Строгое правило: пустое в маппинге != непустое в описании.
        var d = ConnectorDescription.Parse("1.Резьба.ГОСТ 6357");
        var def = new ConnectorTypeDefinition { Code = 1, Name = "Резьба", Description = "" };

        Assert.False(d.Matches(def));
    }

    [Fact]
    public void Matches_EmptyInDescriptionEmptyInDefinition_ReturnsTrue()
    {
        // Оба пустые — совпадают (это валидный edge case: type с только code+name).
        var d = ConnectorDescription.Parse("1.Резьба");
        var def = new ConnectorTypeDefinition { Code = 1, Name = "Резьба", Description = "" };

        Assert.True(d.Matches(def));
    }

    [Fact]
    public void Matches_EmptyInBoth_ReturnsTrue()
    {
        var d = ConnectorDescription.Parse("1");
        var def = new ConnectorTypeDefinition { Code = 1, Name = "", Description = "" };

        Assert.True(d.Matches(def));
    }

    [Fact]
    public void Matches_NonEmptyInDefinitionEmptyInDescription_ReturnsFalse()
    {
        // Симметрично: в маппинге заполнено, в Revit-описании пусто.
        var d = ConnectorDescription.Parse("1.Резьба");
        var def = new ConnectorTypeDefinition { Code = 1, Name = "Резьба", Description = "ГОСТ 6357" };

        Assert.False(d.Matches(def));
    }

    // ── Matches: case-insensitive (OrdinalIgnoreCase) ─────────────────────

    [Fact]
    public void Matches_DifferentCase_ReturnsTrue()
    {
        var d = ConnectorDescription.Parse("1.РЕЗЬБА.гост 6357");
        var def = new ConnectorTypeDefinition { Code = 1, Name = "резьба", Description = "ГОСТ 6357" };

        Assert.True(d.Matches(def));
    }

    [Fact]
    public void Matches_LeadingTrailingWhitespace_ReturnsTrue()
    {
        var d = ConnectorDescription.Parse("1.  Резьба  .  ГОСТ 6357  ");
        var def = new ConnectorTypeDefinition { Code = 1, Name = "Резьба", Description = "ГОСТ 6357" };

        Assert.True(d.Matches(def));
    }

    [Fact]
    public void Matches_NameAndDescriptionInDefinitionTrimmed_ReturnsTrue()
    {
        // Сторона маппинга тоже триммится — устойчивость к случайным пробелам.
        var d = ConnectorDescription.Parse("1.Резьба.ГОСТ 6357");
        var def = new ConnectorTypeDefinition
        {
            Code = 1,
            Name = "  Резьба  ",
            Description = "  ГОСТ 6357  ",
        };

        Assert.True(d.Matches(def));
    }

    // ── IsKnownTypeDefinition: интеграция с mapping ──────────────────────

    [Fact]
    public void IsKnownTypeDefinition_FullMatchInMapping_ReturnsTrue()
    {
        var d = ConnectorDescription.Parse("1.Резьба.ГОСТ 6357");
        var mapping = new[]
        {
            new ConnectorTypeDefinition { Code = 1, Name = "Резьба", Description = "ГОСТ 6357" },
            new ConnectorTypeDefinition { Code = 2, Name = "Сварка", Description = "" },
        };

        Assert.True(ConnectorDescription.IsKnownTypeDefinition(d, mapping));
    }

    [Fact]
    public void IsKnownTypeDefinition_SameCodeDifferentName_ReturnsFalse()
    {
        // Issue #64: одинаковый code, но разный name → НЕ совпадает → MiniTypeSelector сработает.
        var d = ConnectorDescription.Parse("1.Резьба наружная.ГОСТ 6357");
        var mapping = new[]
        {
            new ConnectorTypeDefinition { Code = 1, Name = "Резьба", Description = "ГОСТ 6357" },
        };

        Assert.False(ConnectorDescription.IsKnownTypeDefinition(d, mapping));
    }

    [Fact]
    public void IsKnownTypeDefinition_SameCodeSameNameDifferentDescription_ReturnsFalse()
    {
        var d = ConnectorDescription.Parse("1.Резьба.ГОСТ 3262");
        var mapping = new[]
        {
            new ConnectorTypeDefinition { Code = 1, Name = "Резьба", Description = "ГОСТ 6357" },
        };

        Assert.False(ConnectorDescription.IsKnownTypeDefinition(d, mapping));
    }

    [Fact]
    public void IsKnownTypeDefinition_EmptyMapping_ReturnsFalse()
    {
        var d = ConnectorDescription.Parse("1.Резьба");
        var mapping = Array.Empty<ConnectorTypeDefinition>();

        Assert.False(ConnectorDescription.IsKnownTypeDefinition(d, mapping));
    }

    [Fact]
    public void IsKnownTypeDefinition_MultipleEntriesOneMatches_ReturnsTrue()
    {
        var d = ConnectorDescription.Parse("1.Резьба.ГОСТ 6357");
        var mapping = new[]
        {
            new ConnectorTypeDefinition { Code = 2, Name = "Сварка", Description = "" },
            new ConnectorTypeDefinition { Code = 1, Name = "Резьба", Description = "ГОСТ 6357" },
            new ConnectorTypeDefinition { Code = 3, Name = "Фланец", Description = "" },
        };

        Assert.True(ConnectorDescription.IsKnownTypeDefinition(d, mapping));
    }

    [Fact]
    public void IsKnownTypeDefinition_UndefinedDescription_ReturnsFalse()
    {
        var d = ConnectorDescription.Undefined;
        var mapping = new[]
        {
            new ConnectorTypeDefinition { Code = 1, Name = "Резьба", Description = "" },
        };

        Assert.False(ConnectorDescription.IsKnownTypeDefinition(d, mapping));
    }

    [Fact]
    public void IsKnownTypeDefinition_CaseInsensitiveFullMatch_ReturnsTrue()
    {
        var d = ConnectorDescription.Parse("1.РЕЗЬБА.гост 6357");
        var mapping = new[]
        {
            new ConnectorTypeDefinition { Code = 1, Name = "резьба", Description = "ГОСТ 6357" },
        };

        Assert.True(ConnectorDescription.IsKnownTypeDefinition(d, mapping));
    }

    [Fact]
    public void IsKnownTypeDefinition_WhitespaceTrimmedFullMatch_ReturnsTrue()
    {
        var d = ConnectorDescription.Parse("  1 .  Резьба  .  ГОСТ 6357  ");
        var mapping = new[]
        {
            new ConnectorTypeDefinition { Code = 1, Name = "Резьба", Description = "ГОСТ 6357" },
        };

        Assert.True(ConnectorDescription.IsKnownTypeDefinition(d, mapping));
    }
}