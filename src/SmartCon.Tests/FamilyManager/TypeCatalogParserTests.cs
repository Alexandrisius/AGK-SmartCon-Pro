using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;
using Xunit;

namespace SmartCon.Tests.FamilyManager;

public sealed class TypeCatalogParserTests
{
    [Fact]
    public void Parse_EmptyContent_ReturnsEmptyResult()
    {
        var result = TypeCatalogParser.Parse("");
        Assert.NotNull(result);
        Assert.Empty(result.ParameterNames);
        Assert.Empty(result.Entries);
    }

    [Fact]
    public void Parse_SingleType_ReturnsCorrectEntry()
    {
        var content = ",Manufacturer##other##,Length##length##centimeters\nMA36x30,Revit,36.5";
        var result = TypeCatalogParser.Parse(content);

        Assert.Equal(2, result.ParameterNames.Count);
        Assert.Equal("Manufacturer", result.ParameterNames[0]);
        Assert.Equal("Length", result.ParameterNames[1]);

        Assert.Single(result.Entries);
        Assert.Equal("MA36x30", result.Entries[0].TypeName);
        Assert.Equal("Revit", result.Entries[0].ParameterValues["Manufacturer"]);
        Assert.Equal("36.5", result.Entries[0].ParameterValues["Length"]);
    }

    [Fact]
    public void Parse_MultipleTypes_ReturnsAllEntries()
    {
        var content = """
            ,Width##length##millimeters,Height##length##millimeters
            TypeA,100,200
            TypeB,150,250
            TypeC,200,300
            """;

        var result = TypeCatalogParser.Parse(content);

        Assert.Equal(3, result.Entries.Count);
        Assert.Equal("TypeA", result.Entries[0].TypeName);
        Assert.Equal("TypeB", result.Entries[1].TypeName);
        Assert.Equal("TypeC", result.Entries[2].TypeName);
    }

    [Fact]
    public void Parse_QuotedFields_HandlesQuotesCorrectly()
    {
        var content = ",Description##other##\n\"Type \"\"A\"\"\",\"Value with \"\"quotes\"\"\"";

        var result = TypeCatalogParser.Parse(content);

        Assert.Single(result.Entries);
        Assert.Equal("Type \"A\"", result.Entries[0].TypeName);
        Assert.Equal("Value with \"quotes\"", result.Entries[0].ParameterValues["Description"]);
    }

    [Fact]
    public void Parse_EmptyLines_SkipsEmptyLines()
    {
        var content = """
            ,Param##other##

            Type1,Value1

            Type2,Value2
            """;

        var result = TypeCatalogParser.Parse(content);

        Assert.Equal(2, result.Entries.Count);
    }

    [Fact]
    public void Parse_SemicolonDelimiter_UsesSemicolon()
    {
        var content = ";Param1##other##;Param2##other##\nType1;Val1;Val2";
        var result = TypeCatalogParser.Parse(content);

        Assert.Equal(2, result.ParameterNames.Count);
        Assert.Equal("Param1", result.ParameterNames[0]);
        Assert.Equal("Param2", result.ParameterNames[1]);
        Assert.Single(result.Entries);
        Assert.Equal("Val1", result.Entries[0].ParameterValues["Param1"]);
        Assert.Equal("Val2", result.Entries[0].ParameterValues["Param2"]);
    }

    [Fact]
    public void Parse_TypeNameWithSpaces_PreservesSpaces()
    {
        var content = ",Name##other##\nMy Type Name,MyValue";
        var result = TypeCatalogParser.Parse(content);

        Assert.Single(result.Entries);
        Assert.Equal("My Type Name", result.Entries[0].TypeName);
    }

    [Fact]
    public void Parse_HeaderWithUnits_ParsesParameterNameOnly()
    {
        var content = ",Length##length##feet,Width##length##millimeters\nT1,10,20";
        var result = TypeCatalogParser.Parse(content);

        Assert.Equal("Length", result.ParameterNames[0]);
        Assert.Equal("Width", result.ParameterNames[1]);
    }

    [Fact]
    public void Parse_MoreValuesThanParameters_MapsOnlyAvailableParameters()
    {
        var content = ",Param1##other##\nType1,Val1,Val2,Val3";
        var result = TypeCatalogParser.Parse(content);

        Assert.Single(result.Entries);
        Assert.Single(result.Entries[0].ParameterValues);
        Assert.Equal("Val1", result.Entries[0].ParameterValues["Param1"]);
    }

    [Fact]
    public void Parse_FewerValuesThanParameters_MapsAvailableValues()
    {
        var content = ",P1##other##,P2##other##,P3##other##\nType1,Val1";
        var result = TypeCatalogParser.Parse(content);

        Assert.Single(result.Entries);
        Assert.Single(result.Entries[0].ParameterValues);
        Assert.Equal("Val1", result.Entries[0].ParameterValues["P1"]);
    }
}
