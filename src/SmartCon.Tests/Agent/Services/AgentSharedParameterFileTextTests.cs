using System.IO;
using SmartCon.Core.Services.Agent;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

/// <summary>
/// The text parser behind list_shared_parameters(path): Revit's API only
/// reads the ACTIVE shared parameter file, so any other standard is parsed
/// from the tab-separated .txt (live feedback v17, 1.4).
/// </summary>
public sealed class AgentSharedParameterFileTextTests
{
    private const string Sample =
        "# This is a Revit shared parameter file.\r\n" +
        "*META\tVERSION\tMINVERSION\r\n" +
        "META\t2\t1\r\n" +
        "*GROUP\tID\tNAME\r\n" +
        "GROUP\t1\tIdentity Data\r\n" +
        "GROUP\t2\tМиграция\r\n" +
        "*PARAM\tGUID\tNAME\tDATATYPE\tDATACATEGORY\tGROUP\tVISIBLE\tDESCRIPTION\tUSERMODIFIABLE\r\n" +
        "PARAM\tbb7f0005-9692-4b76-8fa3-30cec8aecf74\tPrice\tINTEGER\t\t2\t1\tEnter price in USD\t1\r\n" +
        "PARAM\tb7ea2654-b206-4694-a087-756359b52e7f\tareaTags\tFAMILYTYPE\t-2005020\t1\t1\t\t1\r\n" +
        "PARAM\td1a5439d-c8d8-4053-99fa-2f33804bae0e\tМойПараметр\tTEXT\t\t1\t0\t\t0\r\n";

    [Fact]
    public void TryParse_ReadsGroupsAndParameters()
    {
        var ok = AgentSharedParameterFileText.TryParse(
            Sample, out var entries, out var groupCount, out var error);

        Assert.True(ok, error);
        Assert.Equal(2, groupCount);
        Assert.Equal(3, entries.Count);

        var price = Assert.Single(entries, entry => entry.Name == "Price");
        Assert.Equal("Миграция", price.Group);
        Assert.Equal("bb7f0005-9692-4b76-8fa3-30cec8aecf74", price.Guid);
        Assert.Equal("integer", price.DataType.ToLowerInvariant());
        Assert.True(price.Visible);
        Assert.True(price.UserModifiable);
        Assert.Equal("Enter price in USD", price.Description);

        var hidden = Assert.Single(entries, entry => entry.Name == "МойПараметр");
        Assert.False(hidden.Visible);
        Assert.False(hidden.UserModifiable);
        Assert.Null(hidden.Description);

        // FAMILYTYPE rows carry the category id in the DATACATEGORY slot: the
        // group column still resolves by the preceding GROUP blocks.
        var familyType = Assert.Single(entries, entry => entry.Name == "areaTags");
        Assert.Equal("Identity Data", familyType.Group);
    }

    [Fact]
    public void TryParse_RejectsFilesWithoutEntries()
    {
        var ok = AgentSharedParameterFileText.TryParse(
            "just some text\nwith no blocks\n", out _, out _, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryParseFile_DetectsUtf16AndReadsDiskFile()
    {
        var path = Path.Combine(
            Path.GetTempPath(), "sc-shared-params-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllBytes(
                path,
                System.Text.Encoding.Unicode.GetPreamble()
                    .Concat(System.Text.Encoding.Unicode.GetBytes(Sample))
                    .ToArray());

            var ok = AgentSharedParameterFileText.TryParseFile(
                path, out var entries, out var groupCount, out var error);

            Assert.True(ok, error);
            Assert.Equal(2, groupCount);
            Assert.Equal(3, entries.Count);
            Assert.Contains(entries, entry => entry.Name == "МойПараметр");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TryParseFile_MissingFile_IsAnHonestError()
    {
        var ok = AgentSharedParameterFileText.TryParseFile(
            "Z:\\no\\such\\file.txt", out _, out _, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
    }
}
