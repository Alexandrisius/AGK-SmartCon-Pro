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
        Assert.Empty(result.Columns);
        Assert.Empty(result.Entries);
        Assert.Empty(result.ParameterNames);
    }

    [Fact]
    public void Parse_SingleType_ReturnsCorrectEntry()
    {
        var content = ",Manufacturer##other##,Length##length##centimeters\nMA36x30,Revit,36.5";
        var result = TypeCatalogParser.Parse(content);

        Assert.Equal(2, result.Columns.Count);
        Assert.Equal("Manufacturer", result.Columns[0].Name);
        Assert.Equal("Length", result.Columns[1].Name);

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

        Assert.Equal(2, result.Columns.Count);
        Assert.Equal("Param1", result.Columns[0].Name);
        Assert.Equal("Param2", result.Columns[1].Name);
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

        Assert.Equal("Length", result.Columns[0].Name);
        Assert.Equal("Width", result.Columns[1].Name);
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

    // ── ##TYPE##UNITS annotation tests (issue: bake-in unit conversion) ──

    [Fact]
    public void Parse_HeaderWithLengthMillimeters_PreservesTypeAndUnitAnnotations()
    {
        var content = ",Width##length##millimeters,Height##length##millimeters\nT1,100,200";
        var result = TypeCatalogParser.Parse(content);

        Assert.Equal(2, result.Columns.Count);
        var width = result.Columns[0];
        Assert.Equal("Width", width.Name);
        Assert.Equal("length", width.TypeAnnotation);
        Assert.Equal("millimeters", width.UnitAnnotation);
        Assert.True(width.HasUnitAnnotation);

        var height = result.Columns[1];
        Assert.Equal("Height", height.Name);
        Assert.Equal("length", height.TypeAnnotation);
        Assert.Equal("millimeters", height.UnitAnnotation);
    }

    [Fact]
    public void Parse_HeaderWithAngleDegrees_PreservesAngleAnnotation()
    {
        var content = ",Rotation##angle##degrees,Slant##angle##radians\nT1,45.5,0.785";
        var result = TypeCatalogParser.Parse(content);

        Assert.Equal("Rotation", result.Columns[0].Name);
        Assert.Equal("angle", result.Columns[0].TypeAnnotation);
        Assert.Equal("degrees", result.Columns[0].UnitAnnotation);

        Assert.Equal("Slant", result.Columns[1].Name);
        Assert.Equal("angle", result.Columns[1].TypeAnnotation);
        Assert.Equal("radians", result.Columns[1].UnitAnnotation);
    }

    [Fact]
    public void Parse_HeaderWithoutAnnotation_HasNullAnnotations()
    {
        var content = ",Width,Height\nT1,100,200";
        var result = TypeCatalogParser.Parse(content);

        Assert.Equal("Width", result.Columns[0].Name);
        Assert.Null(result.Columns[0].TypeAnnotation);
        Assert.Null(result.Columns[0].UnitAnnotation);
        Assert.False(result.Columns[0].HasUnitAnnotation);
    }

    [Fact]
    public void Parse_HeaderWithEmptyUnitPart_HasNullUnitAnnotation()
    {
        // "Manufacturer##other##" → TYPE="other", UNIT=null (пустая строка после второго ##)
        var content = ",Manufacturer##other##\nT1,Acme";
        var result = TypeCatalogParser.Parse(content);

        Assert.Equal("Manufacturer", result.Columns[0].Name);
        Assert.Equal("other", result.Columns[0].TypeAnnotation);
        Assert.Null(result.Columns[0].UnitAnnotation);
    }

    [Fact]
    public void Parse_HeaderWithUppercaseAnnotations_PreservesOriginalCase()
    {
        var content = ",Width##LENGTH##MILLIMETERS,Height##LENGTH##CENTIMETERS\nT1,100,200";
        var result = TypeCatalogParser.Parse(content);

        Assert.Equal("LENGTH", result.Columns[0].TypeAnnotation);
        Assert.Equal("MILLIMETERS", result.Columns[0].UnitAnnotation);
        Assert.Equal("LENGTH", result.Columns[1].TypeAnnotation);
        Assert.Equal("CENTIMETERS", result.Columns[1].UnitAnnotation);
    }

    [Fact]
    public void Parse_HeaderWithMixedAnnotations_CorrectPerColumn()
    {
        var content = ",Name##other##,Width##length##millimeters,Cost##other##,Depth##length##inches\nT1,A,100,5,12";
        var result = TypeCatalogParser.Parse(content);

        Assert.Equal(4, result.Columns.Count);
        Assert.Equal("Name", result.Columns[0].Name);
        Assert.Equal("other", result.Columns[0].TypeAnnotation);
        Assert.Null(result.Columns[0].UnitAnnotation);

        Assert.Equal("Width", result.Columns[1].Name);
        Assert.Equal("length", result.Columns[1].TypeAnnotation);
        Assert.Equal("millimeters", result.Columns[1].UnitAnnotation);

        Assert.Equal("Cost", result.Columns[2].Name);
        Assert.Equal("other", result.Columns[2].TypeAnnotation);
        Assert.Null(result.Columns[2].UnitAnnotation);

        Assert.Equal("Depth", result.Columns[3].Name);
        Assert.Equal("length", result.Columns[3].TypeAnnotation);
        Assert.Equal("inches", result.Columns[3].UnitAnnotation);
    }

    [Fact]
    public void Parse_FindColumn_ReturnsColumnByNameCaseInsensitive()
    {
        var content = ",Width##length##millimeters\nT1,100";
        var result = TypeCatalogParser.Parse(content);

        var column = result.FindColumn("width");
        Assert.NotNull(column);
        Assert.Equal("Width", column.Name);
        Assert.Equal("millimeters", column.UnitAnnotation);
    }

    [Fact]
    public void Parse_FindColumn_ReturnsNullForUnknownName()
    {
        var content = ",Width##length##millimeters\nT1,100";
        var result = TypeCatalogParser.Parse(content);

        var column = result.FindColumn("Height");
        Assert.Null(column);
    }

    [Fact]
    public void Parse_ParameterNames_ReturnsColumnNamesInOrder()
    {
        var content = ",Width##length##mm,Height##length##cm,Depth##length##in\nT1,100,200,12";
        var result = TypeCatalogParser.Parse(content);

        Assert.Equal(new[] { "Width", "Height", "Depth" }, result.ParameterNames);
    }

    [Fact]
    public void Parse_RealWorldRevitExample_ParsesCorrectly()
    {
        // Пример из Autodesk docs:
        // ,Manufacturer##other##,Length##length##centimeters,Width##length##centimeters,Height##length##centimeters
        // MA36x30,Revit,36.5,2.75,30
        var content = ",Manufacturer##other##,Length##length##centimeters,Width##length##centimeters,Height##length##centimeters\nMA36x30,Revit,36.5,2.75,30";

        var result = TypeCatalogParser.Parse(content);

        Assert.Single(result.Entries);
        var entry = result.Entries[0];
        Assert.Equal("MA36x30", entry.TypeName);
        Assert.Equal("Revit", entry.ParameterValues["Manufacturer"]);
        Assert.Equal("36.5", entry.ParameterValues["Length"]);
        Assert.Equal("2.75", entry.ParameterValues["Width"]);
        Assert.Equal("30", entry.ParameterValues["Height"]);

        var lengthColumn = result.FindColumn("Length");
        Assert.NotNull(lengthColumn);
        Assert.Equal("length", lengthColumn.TypeAnnotation);
        Assert.Equal("centimeters", lengthColumn.UnitAnnotation);
    }

    [Fact]
    public void Parse_HeaderWithTypeOnlyAnnotation_HasNullUnitAnnotation()
    {
        // Header "Width##length##" — TYPE specified but UNITS missing (just trailing ##).
        // Per ADR-033 BAKE-009: column without ##UNITS → raw value (project display units).
        var content = ",Width##length##,Height##length##\nT1,100,200";
        var result = TypeCatalogParser.Parse(content);

        Assert.Equal(2, result.Columns.Count);

        var width = result.Columns[0];
        Assert.Equal("Width", width.Name);
        Assert.Equal("length", width.TypeAnnotation);
        Assert.Null(width.UnitAnnotation);
        Assert.False(width.HasUnitAnnotation);

        var height = result.Columns[1];
        Assert.Equal("Height", height.Name);
        Assert.Equal("length", height.TypeAnnotation);
        Assert.Null(height.UnitAnnotation);
    }

    [Fact]
    public void Parse_AnnotationWithExtraFields_IgnoresExcessTokens()
    {
        // Header "Width##length##millimeters##extra" — extra ## and value ignored.
        // Split with StringSplitOptions.None keeps all parts; we read parts[0..2].
        var content = ",Width##length##millimeters##extra\nT1,100";
        var result = TypeCatalogParser.Parse(content);

        Assert.Single(result.Columns);
        var width = result.Columns[0];
        Assert.Equal("Width", width.Name);
        Assert.Equal("length", width.TypeAnnotation);
        Assert.Equal("millimeters", width.UnitAnnotation);
        // "extra" intentionally dropped — parser reads first 3 tokens only.
    }
}
