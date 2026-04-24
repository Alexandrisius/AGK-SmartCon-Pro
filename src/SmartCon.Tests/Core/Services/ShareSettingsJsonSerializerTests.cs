using SmartCon.Core.Models;
using SmartCon.Core.Services.Storage;
using Xunit;

namespace SmartCon.Tests.Core.Services;

public sealed class ShareSettingsJsonSerializerTests
{
    private static ShareProjectSettings CreateTestSettings()
    {
        return new ShareProjectSettings
        {
            ShareFolderPath = @"C:\Shared",
            SyncBeforeShare = true,
            FieldLibrary =
            [
                new FieldDefinition
                {
                    Name = "status",
                    DisplayName = "Status",
                    Description = "Project status",
                    ValidationMode = ValidationMode.AllowedValues,
                    AllowedValues = ["S0", "S1"],
                    MinLength = null,
                    MaxLength = null
                }
            ],
            FileNameTemplate = new FileNameTemplate
            {
                Blocks =
                [
                    new FileBlockDefinition
                    {
                        Index = 0,
                        Field = "project",
                        ParseRule = ParseRule.DefaultDelimiter("-", 0)
                    },
                    new FileBlockDefinition
                    {
                        Index = 1,
                        Field = "status",
                        ParseRule = ParseRule.DefaultDelimiter("-", 1)
                    }
                ],
                ExportMappings =
                [
                    new ExportMapping { Field = "status", SourceValue = "S0", TargetValue = "S1" }
                ]
            },
            PurgeOptions = new PurgeOptions(),
            KeepViewNames = ["View1"]
        };
    }

    [Fact]
    public void Roundtrip_PreservesAllFields()
    {
        var settings = CreateTestSettings();
        var json = ShareSettingsJsonSerializer.Serialize(settings);
        var deserialized = ShareSettingsJsonSerializer.Deserialize(json);

        Assert.Equal(settings.ShareFolderPath, deserialized.ShareFolderPath);
        Assert.Equal(settings.SyncBeforeShare, deserialized.SyncBeforeShare);
        Assert.Equal(2, deserialized.FileNameTemplate.Blocks.Count);
        Assert.Equal("project", deserialized.FileNameTemplate.Blocks[0].Field);
        Assert.Equal("status", deserialized.FileNameTemplate.Blocks[1].Field);
        Assert.Single(deserialized.FileNameTemplate.ExportMappings);
        Assert.Equal("S0", deserialized.FileNameTemplate.ExportMappings[0].SourceValue);
        Assert.Equal("S1", deserialized.FileNameTemplate.ExportMappings[0].TargetValue);
        Assert.Single(deserialized.FieldLibrary);
        Assert.Equal("status", deserialized.FieldLibrary[0].Name);
        Assert.Equal(ValidationMode.AllowedValues, deserialized.FieldLibrary[0].ValidationMode);
        Assert.Equal(["S0", "S1"], deserialized.FieldLibrary[0].AllowedValues);
    }

    [Fact]
    public void Deserialize_NullJson_ReturnsEmpty()
    {
        var result = ShareSettingsJsonSerializer.Deserialize(null);
        Assert.Equal(string.Empty, result.ShareFolderPath);
        Assert.Empty(result.FileNameTemplate.Blocks);
    }

    [Fact]
    public void Deserialize_EmptyJson_ReturnsEmpty()
    {
        var result = ShareSettingsJsonSerializer.Deserialize("   ");
        Assert.Empty(result.FileNameTemplate.Blocks);
    }

    [Fact]
    public void Deserialize_LegacyRoleJson_MigratesToField()
    {
        var legacyJson = @"{
            ""shareFolderPath"": ""C:\\Shared"",
            ""syncBeforeShare"": true,
            ""fieldLibrary"": [],
            ""fileNameTemplate"": {
                ""delimiter"": ""-"",
                ""blocks"": [
                    { ""index"": 0, ""role"": ""project"", ""label"": ""Project"" },
                    { ""index"": 1, ""role"": ""status"", ""label"": ""Status"" }
                ],
                ""statusMappings"": [
                    { ""wipValue"": ""S0"", ""sharedValue"": ""S1"" }
                ]
            },
            ""purgeOptions"": {},
            ""keepViewNames"": []
        }";

        var result = ShareSettingsJsonSerializer.Deserialize(legacyJson);

        Assert.Equal(2, result.FileNameTemplate.Blocks.Count);
    }

    [Fact]
    public void Roundtrip_FieldLibraryWithCharCount()
    {
        var settings = new ShareProjectSettings
        {
            FieldLibrary =
            [
                new FieldDefinition
                {
                    Name = "code",
                    DisplayName = "Code",
                    Description = "",
                    ValidationMode = ValidationMode.CharCount,
                    AllowedValues = [],
                    MinLength = 2,
                    MaxLength = 5
                }
            ],
            FileNameTemplate = new FileNameTemplate
            {
                Blocks = [new FileBlockDefinition { Index = 0, Field = "code", ParseRule = ParseRule.DefaultDelimiter() }]
            }
        };

        var json = ShareSettingsJsonSerializer.Serialize(settings);
        var deserialized = ShareSettingsJsonSerializer.Deserialize(json);

        Assert.Equal(ValidationMode.CharCount, deserialized.FieldLibrary[0].ValidationMode);
        Assert.Equal(2, deserialized.FieldLibrary[0].MinLength);
        Assert.Equal(5, deserialized.FieldLibrary[0].MaxLength);
    }

    [Fact]
    public void Roundtrip_ParseRuleBetweenMarkers()
    {
        var settings = new ShareProjectSettings
        {
            FileNameTemplate = new FileNameTemplate
            {
                Blocks =
                [
                    new FileBlockDefinition
                    {
                        Index = 0,
                        Field = "status",
                        ParseRule = new ParseRule
                        {
                            Mode = ParseMode.BetweenMarkers,
                            OpenMarker = "[",
                            CloseMarker = "]"
                        }
                    }
                ]
            }
        };

        var json = ShareSettingsJsonSerializer.Serialize(settings);
        var deserialized = ShareSettingsJsonSerializer.Deserialize(json);

        Assert.Equal(ParseMode.BetweenMarkers, deserialized.FileNameTemplate.Blocks[0].ParseRule.Mode);
        Assert.Equal("[", deserialized.FileNameTemplate.Blocks[0].ParseRule.OpenMarker);
        Assert.Equal("]", deserialized.FileNameTemplate.Blocks[0].ParseRule.CloseMarker);
    }

    [Fact]
    public void Roundtrip_MultipleExportMappings()
    {
        var settings = new ShareProjectSettings
        {
            FileNameTemplate = new FileNameTemplate
            {
                Blocks =
                [
                    new FileBlockDefinition { Index = 0, Field = "status", ParseRule = ParseRule.DefaultDelimiter() }
                ],
                ExportMappings =
                [
                    new ExportMapping { Field = "status", SourceValue = "S1", TargetValue = "S2" },
                    new ExportMapping { Field = "status", SourceValue = "S2", TargetValue = "S3" }
                ]
            }
        };

        var json = ShareSettingsJsonSerializer.Serialize(settings);
        var deserialized = ShareSettingsJsonSerializer.Deserialize(json);

        Assert.Equal(2, deserialized.FileNameTemplate.ExportMappings.Count);
        Assert.Equal("S2", deserialized.FileNameTemplate.ExportMappings[0].TargetValue);
        Assert.Equal("S3", deserialized.FileNameTemplate.ExportMappings[1].TargetValue);
    }
}
