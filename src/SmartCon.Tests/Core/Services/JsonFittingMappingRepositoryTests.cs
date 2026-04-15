using System.IO;
using SmartCon.Core.Models;
using SmartCon.Core.Services.Implementation;
using Xunit;

namespace SmartCon.Tests.Core.Services;

public sealed class JsonFittingMappingRepositoryTests : IDisposable
{
    private readonly string _tempFile;
    private readonly JsonFittingMappingRepository _sut;

    public JsonFittingMappingRepositoryTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"smartcon-test-{Guid.NewGuid()}.json");
        _sut = new JsonFittingMappingRepository(_tempFile);
    }

    public void Dispose()
    {
        if (File.Exists(_tempFile))
            File.Delete(_tempFile);
    }

    // ── GetConnectorTypes ─────────────────────────────────────────────────

    [Fact]
    public void GetConnectorTypes_NoFile_ReturnsEmpty()
    {
        var result = _sut.GetConnectorTypes();
        Assert.Empty(result);
    }

    [Fact]
    public void SaveAndGetConnectorTypes_RoundTrip()
    {
        var types = new List<ConnectorTypeDefinition>
        {
            new() { Code = 1, Name = "Сварка",  Description = "Сварное" },
            new() { Code = 2, Name = "Резьба",  Description = "Резьбовое" },
        };

        _sut.SaveConnectorTypes(types);
        var loaded = _sut.GetConnectorTypes();

        Assert.Equal(2, loaded.Count);
        Assert.Equal(1, loaded[0].Code);
        Assert.Equal("Сварка", loaded[0].Name);
        Assert.Equal("Сварное", loaded[0].Description);
        Assert.Equal(2, loaded[1].Code);
        Assert.Equal("Резьба", loaded[1].Name);
    }

    [Fact]
    public void SaveConnectorTypes_OverwritesPreviousTypes()
    {
        _sut.SaveConnectorTypes([new() { Code = 1, Name = "Сварка" }]);
        _sut.SaveConnectorTypes([new() { Code = 2, Name = "Резьба" }]);

        var loaded = _sut.GetConnectorTypes();
        Assert.Single(loaded);
        Assert.Equal(2, loaded[0].Code);
    }

    // ── GetMappingRules ───────────────────────────────────────────────────

    [Fact]
    public void GetMappingRules_NoFile_ReturnsEmpty()
    {
        var result = _sut.GetMappingRules();
        Assert.Empty(result);
    }

    [Fact]
    public void SaveAndGetMappingRules_RoundTrip()
    {
        var rules = new List<FittingMappingRule>
        {
            new()
            {
                FromType = new ConnectionTypeCode(1),
                ToType   = new ConnectionTypeCode(2),
                IsDirectConnect = false,
                FittingFamilies =
                [
                    new FittingMapping { FamilyName = "Переходник", SymbolName = "*", Priority = 1 },
                ],
            },
        };

        _sut.SaveMappingRules(rules);
        var loaded = _sut.GetMappingRules();

        Assert.Single(loaded);
        Assert.Equal(1, loaded[0].FromType.Value);
        Assert.Equal(2, loaded[0].ToType.Value);
        Assert.False(loaded[0].IsDirectConnect);
        Assert.Single(loaded[0].FittingFamilies);
        Assert.Equal("Переходник", loaded[0].FittingFamilies[0].FamilyName);
        Assert.Equal(1, loaded[0].FittingFamilies[0].Priority);
    }

    [Fact]
    public void SaveTypes_PreservesMappingRules()
    {
        var rules = new List<FittingMappingRule>
        {
            new() { FromType = new ConnectionTypeCode(1), ToType = new ConnectionTypeCode(2) },
        };
        _sut.SaveMappingRules(rules);
        _sut.SaveConnectorTypes([new() { Code = 1, Name = "Сварка" }]);

        var loadedRules = _sut.GetMappingRules();
        Assert.Single(loadedRules);
    }

    [Fact]
    public void SaveRules_PreservesConnectorTypes()
    {
        _sut.SaveConnectorTypes([new() { Code = 1, Name = "Сварка" }]);
        _sut.SaveMappingRules([]);

        var loadedTypes = _sut.GetConnectorTypes();
        Assert.Single(loadedTypes);
    }

    [Fact]
    public void GetStoragePath_ReturnsConfiguredPath()
    {
        Assert.Equal(_tempFile, _sut.GetStoragePath());
    }

    [Fact]
    public void GetConnectorTypes_CorruptFile_ReturnsEmpty()
    {
        File.WriteAllText(_tempFile, "{ invalid json }}}");
        var result = _sut.GetConnectorTypes();
        Assert.Empty(result);
    }
}
