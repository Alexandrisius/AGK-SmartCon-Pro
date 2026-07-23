using System.IO;
using SmartCon.Core.Services.Implementation;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Core;

public sealed class SharedParameterFileParserTests
{
    private const string SampleHeader = """
        # This is a Revit shared parameter file.
        # Do not edit manually.
        *META	VERSION	MINVERSION
        META	2	1
        *GROUP	ID	NAME
        GROUP	1	01 Обязательные ОБЩИЕ
        GROUP	8	08 Необязательные ИНЖЕНЕРИЯ
        *PARAM	GUID	NAME	DATATYPE	DATACATEGORY	GROUP	VISIBLE	DESCRIPTION	USERMODIFIABLE	HIDEWHENNOVALUE
        """;

    [Fact]
    public void Parse_FullFile_ExtractsParamsWithGroupNames()
    {
        var content = SampleHeader + "\n" +
            "PARAM\t70ca0400-7261-47f1-9e82-e213c212474d\tADSK_Температура обратной линии\tPIPING_TEMPERATURE\t\t8\t1\tТемпература обратной линии\t1\t0\n" +
            "PARAM\t32989501-0d17-4916-8777-da950841c6d7\tADSK_Масса\tNUMBER\t\t1\t1\tМасса единицы\t1\t0";

        var entries = new SharedParameterFileParser().ParseContent(content);

        Assert.Equal(2, entries.Count);

        var temp = Assert.Single(entries, e => e.Name == "ADSK_Температура обратной линии");
        Assert.Equal("PIPING_TEMPERATURE", temp.DataType);
        Assert.Equal("08 Необязательные ИНЖЕНЕРИЯ", temp.GroupName);
        Assert.Equal("Температура обратной линии", temp.Description);
        Assert.Equal(Guid.Parse("70ca0400-7261-47f1-9e82-e213c212474d"), temp.ParameterGuid);
        Assert.Null(temp.DataCategory);

        var mass = Assert.Single(entries, e => e.Name == "ADSK_Масса");
        Assert.Equal("01 Обязательные ОБЩИЕ", mass.GroupName);
    }

    [Fact]
    public void Parse_ParamWithoutDescription_NullDescription()
    {
        var content = SampleHeader + "\n" +
            "PARAM\t9c98831b-9450-412d-b072-7d69b39f4029\tADSK_Обозначение\tTEXT\t\t1\t1\t\t1\t0";

        var entry = Assert.Single(new SharedParameterFileParser().ParseContent(content));

        Assert.Null(entry.Description);
    }

    [Fact]
    public void Parse_FamilyTypeParam_KeepsDataCategory()
    {
        var content = SampleHeader + "\n" +
            "PARAM\t5d46c406-eb6c-4b12-8d06-e6592d7fbdf8\tADSK_Типоразмер элемента узла\tFAMILYTYPE\t-2002000\t8\t1\tВыбор типоразмера\t1\t0";

        var entry = Assert.Single(new SharedParameterFileParser().ParseContent(content));

        Assert.Equal("FAMILYTYPE", entry.DataType);
        Assert.Equal("-2002000", entry.DataCategory);
    }

    [Fact]
    public void Parse_ParamWithUnknownGroupId_NullGroupName()
    {
        var content = SampleHeader + "\n" +
            "PARAM\t32989501-0d17-4916-8777-da950841c6d7\tADSK_Масса\tNUMBER\t\t99\t1\t\t1\t0";

        var entry = Assert.Single(new SharedParameterFileParser().ParseContent(content));

        Assert.Null(entry.GroupName);
    }

    [Fact]
    public void Parse_NoParamSection_ThrowsInvalidData()
    {
        var content = "# just a text file\nhello\tworld\n";

        Assert.Throws<InvalidDataException>(() => new SharedParameterFileParser().ParseContent(content));
    }

    [Fact]
    public void Parse_ShuffledColumns_UsesHeaderNames()
    {
        var content =
            "*GROUP\tNAME\tID\n" +
            "GROUP\tMyGroup\t3\n" +
            "*PARAM\tNAME\tGUID\tGROUP\tDATATYPE\n" +
            "PARAM\tMyParam\t32989501-0d17-4916-8777-da950841c6d7\t3\tTEXT\n";

        var entry = Assert.Single(new SharedParameterFileParser().ParseContent(content));

        Assert.Equal("MyParam", entry.Name);
        Assert.Equal("TEXT", entry.DataType);
        Assert.Equal("MyGroup", entry.GroupName);
    }

    [Fact]
    public void Parse_NoHeaderLines_FallsBackToFixedOrder()
    {
        var content =
            "GROUP\t1\tSomeGroup\n" +
            "PARAM\t32989501-0d17-4916-8777-da950841c6d7\tADSK_Масса\tNUMBER\t\t1\t1\tОписание\t1\t0";

        var entry = Assert.Single(new SharedParameterFileParser().ParseContent(content));

        Assert.Equal("ADSK_Масса", entry.Name);
        Assert.Equal("NUMBER", entry.DataType);
        Assert.Equal("SomeGroup", entry.GroupName);
        Assert.Equal("Описание", entry.Description);
    }

    [Fact]
    public void Parse_InvalidGuidOrEmptyName_SkipsLine()
    {
        var content = SampleHeader + "\n" +
            "PARAM\tnot-a-guid\tBadParam\tTEXT\t\t1\t1\t\t1\t0\n" +
            "PARAM\t32989501-0d17-4916-8777-da950841c6d7\t\tTEXT\t\t1\t1\t\t1\t0\n" +
            "PARAM\t70ca0400-7261-47f1-9e82-e213c212474d\tGoodParam\tTEXT\t\t1\t1\t\t1\t0";

        var entries = new SharedParameterFileParser().ParseContent(content);

        var entry = Assert.Single(entries);
        Assert.Equal("GoodParam", entry.Name);
    }

    [Fact]
    public void ParseFile_Utf16LeWithBom_Parses()
    {
        var content = SampleHeader + "\n" +
            "PARAM\t32989501-0d17-4916-8777-da950841c6d7\tADSK_Масса\tNUMBER\t\t1\t1\t\t1\t0";
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
        try
        {
            File.WriteAllText(path, content, new System.Text.UnicodeEncoding(false, true));

            var entries = new SharedParameterFileParser().ParseFile(path);

            var entry = Assert.Single(entries);
            Assert.Equal("ADSK_Масса", entry.Name);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ParseFile_Utf8WithoutBom_Parses()
    {
        var content = SampleHeader + "\n" +
            "PARAM\t32989501-0d17-4916-8777-da950841c6d7\tADSK_Масса\tNUMBER\t\t1\t1\t\t1\t0";
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
        try
        {
            File.WriteAllText(path, content, new System.Text.UTF8Encoding(false));

            var entries = new SharedParameterFileParser().ParseFile(path);

            var entry = Assert.Single(entries);
            Assert.Equal("ADSK_Масса", entry.Name);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
