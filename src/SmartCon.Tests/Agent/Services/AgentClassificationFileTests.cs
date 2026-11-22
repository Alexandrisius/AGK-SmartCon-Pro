using System.Text;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

public sealed class AgentClassificationFileTests
{
    private const string Table = "A10\tFoundations\t1\t\r\nA1010\tStandard Foundations\t2\t-2001300\n";

    [Fact]
    public void Parse_FourTabColumns_InEitherLineBreakStyle_ReadsEveryColumn()
    {
        var parsed = AgentClassificationFile.Parse(Table);

        Assert.False(parsed.IsWrongFormat);
        Assert.Equal(2, parsed.Rows.Count);
        Assert.Equal(0, parsed.MalformedLines);
        Assert.Empty(parsed.Errors);
        Assert.Equal("A10", parsed.Rows[0].Code);
        Assert.Equal("Foundations", parsed.Rows[0].Description);
        Assert.Equal(1, parsed.Rows[0].Level);
        Assert.Null(parsed.Rows[0].CategoryId);
        Assert.Equal("A1010", parsed.Rows[1].Code);
        Assert.Equal(2, parsed.Rows[1].Level);
        Assert.Equal(-2001300L, parsed.Rows[1].CategoryId);
    }

    [Fact]
    public void Parse_EmptyText_HasNoRowsAndNoErrors()
    {
        foreach (var text in new string?[] { null, string.Empty, "\r\n  \n" })
        {
            var parsed = AgentClassificationFile.Parse(text);

            Assert.Empty(parsed.Rows);
            Assert.Empty(parsed.Errors);
            Assert.Equal(0, parsed.MalformedLines);
        }
    }

    [Fact]
    public void Parse_SpacesInsteadOfTabs_IsAWrongFormatThatNamesTheCause()
    {
        var parsed = AgentClassificationFile.Parse("A10 Foundations 1\r\nA1010 Standard Foundations 2");

        Assert.True(parsed.IsWrongFormat);
        Assert.Equal(2, parsed.MalformedLines);
        Assert.Equal(2, parsed.Errors.Count);
        Assert.Contains("separated by spaces, not tabs", parsed.Errors[0]);
        Assert.StartsWith("line 1:", parsed.Errors[0]);
    }

    [Theory]
    [InlineData("6")]
    [InlineData("0")]
    [InlineData("two")]
    public void Parse_LevelOutsideOneToFive_IsReportedByLineAndSkipped(string level)
    {
        var parsed = AgentClassificationFile.Parse("A10\tFoundations\t" + level + "\t\nA1010\tStandard Foundations\t2\t");

        Assert.False(parsed.IsWrongFormat);
        Assert.Single(parsed.Rows);
        Assert.Equal("A1010", parsed.Rows[0].Code);
        Assert.Equal(1, parsed.MalformedLines);
        var error = Assert.Single(parsed.Errors);
        Assert.StartsWith("line 1:", error);
        Assert.Contains("'" + level + "'", error);
    }

    [Fact]
    public void Parse_MoreBadLinesThanRows_IsAWrongFormat()
    {
        var parsed = AgentClassificationFile.Parse("A10\tFoundations\t6\t\nA1010\tStandard Foundations\t0\t\nA20\tWalls\t1\t");

        Assert.True(parsed.IsWrongFormat);
        Assert.Equal(2, parsed.MalformedLines);
        Assert.Single(parsed.Rows);
    }

    [Fact]
    public void Parse_EmptyCode_IsReportedByLine()
    {
        var parsed = AgentClassificationFile.Parse("\tFoundations\t1\t");

        Assert.True(parsed.IsWrongFormat);
        Assert.Contains(parsed.Errors, error => error.StartsWith("line 1:") && error.Contains("the code is empty"));
    }

    [Fact]
    public void Parse_FewBadLinesAmongManyRows_AreLeftToRevit()
    {
        var parsed = AgentClassificationFile.Parse("header\r\nA10\tFoundations\t1\t\nA1010\tStandard Foundations\t2\t");

        Assert.False(parsed.IsWrongFormat);
        Assert.Equal(2, parsed.Rows.Count);
        Assert.Equal(1, parsed.MalformedLines);
    }

    [Fact]
    public void Parse_ColumnsAfterTheFourth_AreIgnored()
    {
        var parsed = AgentClassificationFile.Parse("A10\tFoundations\t1\t-2001300\textra\tmore");

        Assert.False(parsed.IsWrongFormat);
        Assert.Single(parsed.Rows);
        Assert.Equal("Foundations", parsed.Rows[0].Description);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Decode_Utf16WithByteOrderMark_ReadsCyrillicInEitherEndianness(bool bigEndian)
    {
        var text = "A10\tФундаменты\t1\t";
        var encoding = new UnicodeEncoding(bigEndian, byteOrderMark: true);
        var decoded = AgentClassificationFile.Decode(encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray(), out var error);

        Assert.Null(error);
        Assert.Equal(text, decoded);
    }

    [Fact]
    public void Decode_Utf8WithoutByteOrderMark_ReadsCyrillic()
    {
        var bytes = Encoding.UTF8.GetBytes("A10\tФундаменты\t1\t");
        var decoded = AgentClassificationFile.Decode(bytes, out var error);

        Assert.Null(error);
        Assert.Equal("A10\tФундаменты\t1\t", decoded);
    }

    [Fact]
    public void Decode_LegacySingleByteEncoding_IsRefusedInsteadOfGuessed()
    {
        // "Кран" in windows-1251: not a valid UTF-8 sequence.
        var decoded = AgentClassificationFile.Decode(new byte[] { 0xCA, 0xF0, 0xE0, 0xED }, out var error);

        Assert.Null(decoded);
        Assert.Contains("UTF-16", error);
        Assert.Contains("single-byte", error);
    }

    [Fact]
    public void Decode_Utf16WithOddNumberOfBytes_IsRefusedNotThrown()
    {
        var decoded = AgentClassificationFile.Decode(new byte[] { 0xFF, 0xFE, 0x41, 0x10, 0x00 }, out var error);

        Assert.Null(decoded);
        Assert.NotNull(error);
    }

    [Fact]
    public void EncodeAndDecode_Utf16_RoundTripsCyrillic()
    {
        var decoded = AgentClassificationFile.Decode(AgentClassificationFile.EncodeUtf16("A10\tФундаменты\t1\t"), out var error);

        Assert.Null(error);
        Assert.Equal("A10\tФундаменты\t1\t", decoded);
        Assert.True(AgentClassificationFile.HasUtf16Bom(AgentClassificationFile.EncodeUtf16("A")));
    }

    [Fact]
    public void Format_ThenParse_RoundTripsRows()
    {
        var rows = new[]
        {
            new AgentClassificationEntry { Code = "A10", Description = "Foundations", Level = 1 },
            new AgentClassificationEntry { Code = "A1010", Description = "Standard\tFoundations", Level = 2, CategoryId = -2001300L }
        };

        var text = AgentClassificationFile.Format(rows);
        var parsed = AgentClassificationFile.Parse(text);

        Assert.Equal(2, parsed.Rows.Count);
        Assert.Equal("A10", parsed.Rows[0].Code);
        Assert.Equal(1, parsed.Rows[0].Level);
        Assert.Null(parsed.Rows[0].CategoryId);
        Assert.Equal("A1010", parsed.Rows[1].Code);
        Assert.Equal("Standard Foundations", parsed.Rows[1].Description);
        Assert.Equal(2, parsed.Rows[1].Level);
        Assert.Equal(-2001300L, parsed.Rows[1].CategoryId);
    }

    [Fact]
    public void DescriptionsMatch_ChangedNonAsciiDescription_ReportsTheCodeAndBothTexts()
    {
        var rows = new[] { new AgentClassificationEntry { Code = "A10", Description = "Фундаменты", Level = 1 } };
        var table = AgentClassificationTable.Build(
            new[] { new AgentClassificationEntry { Code = "A10", Description = "Ôóíäàìåíòû", Level = 1 } },
            null);

        var match = AgentClassificationFile.DescriptionsMatch(rows, table, out var example);

        Assert.False(match);
        Assert.Contains("A10", example);
        Assert.Contains("Фундаменты", example);
        Assert.Contains("Ôóíäàìåíòû", example);
    }

    [Fact]
    public void DescriptionsMatch_SameNonAsciiDescription_Matches()
    {
        var entry = new AgentClassificationEntry { Code = "A10", Description = "Фундаменты", Level = 1 };

        var match = AgentClassificationFile.DescriptionsMatch(
            new[] { entry },
            AgentClassificationTable.Build(new[] { entry }, null),
            out var example);

        Assert.True(match);
        Assert.Null(example);
    }

    [Fact]
    public void DescriptionsMatch_AsciiDescription_MatchesWhateverRevitNormalizedTo()
    {
        var rows = new[] { new AgentClassificationEntry { Code = "A10", Description = "Foundations", Level = 1 } };
        var table = AgentClassificationTable.Build(
            new[] { new AgentClassificationEntry { Code = "A10", Description = "FOUNDATIONS", Level = 1 } },
            null);

        Assert.True(AgentClassificationFile.DescriptionsMatch(rows, table, out _));
    }
}
