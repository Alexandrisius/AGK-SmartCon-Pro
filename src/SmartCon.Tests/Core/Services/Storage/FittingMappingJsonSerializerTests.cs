using System.IO;
using System.Text.Json;
using SmartCon.Core.Models;
using SmartCon.Core.Services.Storage;
using Xunit;

namespace SmartCon.Tests.Core.Services.Storage;

public sealed class FittingMappingJsonSerializerTests
{
    // ── Serialize ─────────────────────────────────────────────────────────

    [Fact]
    public void Serialize_Empty_ReturnsValidJsonWithCurrentVersion()
    {
        var json = FittingMappingJsonSerializer.Serialize([], []);

        var payload = FittingMappingJsonSerializer.Deserialize(json);
        Assert.Equal(FittingMappingJsonSerializer.CurrentVersion, payload.SchemaVersion);
        Assert.Empty(payload.ConnectorTypes);
        Assert.Empty(payload.MappingRules);
    }

    [Fact]
    public void Serialize_UsesCamelCasePropertyNames()
    {
        var json = FittingMappingJsonSerializer.Serialize(
            [new ConnectorTypeDefinition { Code = 1, Name = "Сварка", Description = "d" }],
            []);

        Assert.Contains("\"schemaVersion\"", json);
        Assert.Contains("\"connectorTypes\"", json);
        Assert.Contains("\"mappingRules\"", json);
    }

    [Fact]
    public void Serialize_OverwritesSchemaVersionToCurrent()
    {
        var payload = new MappingPayload(
            SchemaVersion: 999,
            ConnectorTypes: [],
            MappingRules: []);

        var json = FittingMappingJsonSerializer.Serialize(payload);
        var roundTrip = FittingMappingJsonSerializer.Deserialize(json);

        Assert.Equal(FittingMappingJsonSerializer.CurrentVersion, roundTrip.SchemaVersion);
    }

    // ── Roundtrip ─────────────────────────────────────────────────────────

    [Fact]
    public void Roundtrip_ConnectorTypes_Preserved()
    {
        var types = new List<ConnectorTypeDefinition>
        {
            new() { Code = 1, Name = "Сварка",  Description = "Сварное" },
            new() { Code = 2, Name = "Резьба",  Description = "Резьбовое" },
        };

        var json = FittingMappingJsonSerializer.Serialize(types, []);
        var result = FittingMappingJsonSerializer.Deserialize(json);

        Assert.Equal(2, result.ConnectorTypes.Count);
        Assert.Equal(1, result.ConnectorTypes[0].Code);
        Assert.Equal("Сварка", result.ConnectorTypes[0].Name);
        Assert.Equal("Сварное", result.ConnectorTypes[0].Description);
        Assert.Equal(2, result.ConnectorTypes[1].Code);
        Assert.Equal("Резьба", result.ConnectorTypes[1].Name);
    }

    [Fact]
    public void Roundtrip_MappingRules_Preserved()
    {
        var rules = new List<FittingMappingRule>
        {
            new()
            {
                FromType = new ConnectionTypeCode(1),
                ToType = new ConnectionTypeCode(2),
                IsDirectConnect = false,
                FittingFamilies =
                [
                    new FittingMapping { FamilyName = "Adapter", SymbolName = "*", Priority = 1 },
                    new FittingMapping { FamilyName = "Adapter", SymbolName = "DN50", Priority = 2 },
                ],
                ReducerFamilies =
                [
                    new FittingMapping { FamilyName = "Reducer", SymbolName = "*", Priority = 5 },
                ],
            },
        };

        var json = FittingMappingJsonSerializer.Serialize([], rules);
        var result = FittingMappingJsonSerializer.Deserialize(json);

        var rule = Assert.Single(result.MappingRules);
        Assert.Equal(1, rule.FromType.Value);
        Assert.Equal(2, rule.ToType.Value);
        Assert.False(rule.IsDirectConnect);

        Assert.Equal(2, rule.FittingFamilies.Count);
        Assert.Equal("Adapter", rule.FittingFamilies[0].FamilyName);
        Assert.Equal("*", rule.FittingFamilies[0].SymbolName);
        Assert.Equal(1, rule.FittingFamilies[0].Priority);
        Assert.Equal("DN50", rule.FittingFamilies[1].SymbolName);

        Assert.Single(rule.ReducerFamilies);
        Assert.Equal("Reducer", rule.ReducerFamilies[0].FamilyName);
        Assert.Equal(5, rule.ReducerFamilies[0].Priority);
    }

    [Fact]
    public void Roundtrip_IsDirectConnect_Preserved()
    {
        var rules = new List<FittingMappingRule>
        {
            new() { FromType = new ConnectionTypeCode(1), ToType = new ConnectionTypeCode(1), IsDirectConnect = true },
        };

        var json = FittingMappingJsonSerializer.Serialize([], rules);
        var result = FittingMappingJsonSerializer.Deserialize(json);

        Assert.True(result.MappingRules[0].IsDirectConnect);
    }

    // ── Legacy format compatibility ───────────────────────────────────────

    [Fact]
    public void Deserialize_LegacyFileWithoutVersion_DefaultsToCurrentVersion()
    {
        // Legacy format (as written by JsonFittingMappingRepository v1.x): no schemaVersion field.
        const string legacyJson = """
        {
          "connectorTypes": [
            { "code": 1, "name": "Сварка", "description": "Сварное" }
          ],
          "mappingRules": [
            {
              "fromType": 1,
              "toType": 2,
              "isDirectConnect": false,
              "fittingFamilies": [
                { "familyName": "X", "symbolName": "*", "priority": 1 }
              ],
              "reducerFamilies": []
            }
          ]
        }
        """;

        var result = FittingMappingJsonSerializer.Deserialize(legacyJson);

        Assert.Equal(FittingMappingJsonSerializer.CurrentVersion, result.SchemaVersion);
        Assert.Single(result.ConnectorTypes);
        Assert.Equal("Сварка", result.ConnectorTypes[0].Name);
        Assert.Single(result.MappingRules);
        Assert.Equal(1, result.MappingRules[0].FromType.Value);
        Assert.Single(result.MappingRules[0].FittingFamilies);
        Assert.Equal("X", result.MappingRules[0].FittingFamilies[0].FamilyName);
    }

    [Fact]
    public void Deserialize_PascalCasePropertyNames_Accepted()
    {
        // PropertyNameCaseInsensitive should accept PascalCase as well.
        const string pascalCaseJson = """
        {
          "SchemaVersion": 1,
          "ConnectorTypes": [ { "Code": 7, "Name": "X", "Description": "Y" } ],
          "MappingRules": []
        }
        """;

        var result = FittingMappingJsonSerializer.Deserialize(pascalCaseJson);

        Assert.Equal(1, result.SchemaVersion);
        Assert.Single(result.ConnectorTypes);
        Assert.Equal(7, result.ConnectorTypes[0].Code);
    }

    [Fact]
    public void Deserialize_MissingCollections_ReturnsEmpty()
    {
        const string minimal = "{ \"schemaVersion\": 1 }";

        var result = FittingMappingJsonSerializer.Deserialize(minimal);

        Assert.Empty(result.ConnectorTypes);
        Assert.Empty(result.MappingRules);
    }

    [Fact]
    public void Deserialize_NullOrWhitespace_ReturnsEmpty()
    {
        Assert.Same(MappingPayload.Empty, FittingMappingJsonSerializer.Deserialize(null));
        Assert.Same(MappingPayload.Empty, FittingMappingJsonSerializer.Deserialize(string.Empty));
        Assert.Same(MappingPayload.Empty, FittingMappingJsonSerializer.Deserialize("   "));
    }

    [Fact]
    public void Deserialize_InvalidJson_Throws()
    {
        Assert.Throws<JsonException>(() =>
            FittingMappingJsonSerializer.Deserialize("{ not valid json"));
    }

    // ── File IO ───────────────────────────────────────────────────────────

    [Fact]
    public void WriteToFile_ReadBack_Roundtrip()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"smartcon-test-{Guid.NewGuid()}.json");
        try
        {
            var payload = new MappingPayload(
                FittingMappingJsonSerializer.CurrentVersion,
                [new ConnectorTypeDefinition { Code = 3, Name = "Раструб", Description = "" }],
                [new FittingMappingRule { FromType = new ConnectionTypeCode(3), ToType = new ConnectionTypeCode(3) }]);

            FittingMappingJsonSerializer.WriteToFile(tempFile, payload);
            var loaded = FittingMappingJsonSerializer.TryReadFromFile(tempFile);

            Assert.NotNull(loaded);
            Assert.Equal("Раструб", loaded!.ConnectorTypes[0].Name);
            Assert.Equal(3, loaded.MappingRules[0].FromType.Value);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void WriteToFile_CreatesMissingDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"smartcon-test-{Guid.NewGuid()}");
        var tempFile = Path.Combine(tempDir, "subdir", "mapping.json");
        try
        {
            FittingMappingJsonSerializer.WriteToFile(tempFile, MappingPayload.Empty);
            Assert.True(File.Exists(tempFile));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void TryReadFromFile_Missing_ReturnsNull()
    {
        var path = Path.Combine(Path.GetTempPath(), $"smartcon-missing-{Guid.NewGuid()}.json");
        Assert.Null(FittingMappingJsonSerializer.TryReadFromFile(path));
    }

    [Fact]
    public void TryReadFromFile_Corrupted_ReturnsNull()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"smartcon-test-{Guid.NewGuid()}.json");
        try
        {
            File.WriteAllText(tempFile, "{ not valid json");
            Assert.Null(FittingMappingJsonSerializer.TryReadFromFile(tempFile));
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
