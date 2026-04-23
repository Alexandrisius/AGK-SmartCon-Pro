using SmartCon.Core.Models;
using SmartCon.Core.Services.Storage;
using Xunit;

namespace SmartCon.Tests.Core.Services;

public sealed class ShareSettingsJsonSerializerTests
{
    [Fact]
    public void Roundtrip_FullSettings_PreservesAllData()
    {
        var settings = new ShareProjectSettings
        {
            ShareFolderPath = @"\\server\Shared",
            SyncBeforeShare = true,
            PurgeOptions = new PurgeOptions
            {
                PurgeRvtLinks = true,
                PurgeCadImports = false,
                PurgeImages = true,
                PurgePointClouds = false,
                PurgeGroups = true,
                PurgeAssemblies = false,
                PurgeSpaces = true,
                PurgeRebar = false,
                PurgeFabricReinforcement = true,
                PurgeUnused = true
            },
            KeepViewNames = ["View1", "View2"],
            FileNameTemplate = new FileNameTemplate
            {
                Delimiter = "-",
                Blocks =
                [
                    new FileBlockDefinition { Index = 0, Role = "project", Label = "Project code" },
                    new FileBlockDefinition { Index = 1, Role = "status", Label = "Status" }
                ],
                StatusMappings = [new StatusMapping { WipValue = "S0", SharedValue = "S1" }]
            }
        };

        var json = ShareSettingsJsonSerializer.Serialize(settings);
        var deserialized = ShareSettingsJsonSerializer.Deserialize(json);

        Assert.Equal(settings.ShareFolderPath, deserialized.ShareFolderPath);
        Assert.Equal(settings.SyncBeforeShare, deserialized.SyncBeforeShare);
        Assert.Equal(settings.KeepViewNames, deserialized.KeepViewNames);
        Assert.Equal(settings.FileNameTemplate.Delimiter, deserialized.FileNameTemplate.Delimiter);
        Assert.Equal(settings.FileNameTemplate.Blocks.Count, deserialized.FileNameTemplate.Blocks.Count);
        Assert.Equal(settings.FileNameTemplate.StatusMappings.Count, deserialized.FileNameTemplate.StatusMappings.Count);
    }

    [Fact]
    public void Deserialize_NullJson_ReturnsDefault()
    {
        var result = ShareSettingsJsonSerializer.Deserialize(null);
        Assert.Empty(result.ShareFolderPath);
        Assert.Empty(result.KeepViewNames);
        Assert.Empty(result.FileNameTemplate.Blocks);
        Assert.Empty(result.FileNameTemplate.StatusMappings);
        Assert.True(result.SyncBeforeShare);
    }

    [Fact]
    public void Deserialize_EmptyJson_ReturnsDefault()
    {
        var result = ShareSettingsJsonSerializer.Deserialize("");
        Assert.Empty(result.ShareFolderPath);
        Assert.Empty(result.KeepViewNames);
        Assert.Empty(result.FileNameTemplate.Blocks);
        Assert.Empty(result.FileNameTemplate.StatusMappings);
        Assert.True(result.SyncBeforeShare);
    }

    [Fact]
    public void Serialize_ContainsExpectedFields()
    {
        var settings = new ShareProjectSettings
        {
            ShareFolderPath = @"C:\Shared",
            PurgeOptions = new PurgeOptions()
        };
        var json = ShareSettingsJsonSerializer.Serialize(settings);
        Assert.Contains("shareFolderPath", json);
        Assert.Contains("purgeOptions", json);
        Assert.Contains("purgeRvtLinks", json);
    }

    [Fact]
    public void Roundtrip_DefaultPurgeOptions_AllTrue()
    {
        var settings = new ShareProjectSettings { PurgeOptions = new PurgeOptions() };
        var json = ShareSettingsJsonSerializer.Serialize(settings);
        var deserialized = ShareSettingsJsonSerializer.Deserialize(json);

        Assert.True(deserialized.PurgeOptions.PurgeRvtLinks);
        Assert.True(deserialized.PurgeOptions.PurgeCadImports);
        Assert.True(deserialized.PurgeOptions.PurgeImages);
        Assert.True(deserialized.PurgeOptions.PurgePointClouds);
        Assert.True(deserialized.PurgeOptions.PurgeGroups);
        Assert.True(deserialized.PurgeOptions.PurgeAssemblies);
        Assert.True(deserialized.PurgeOptions.PurgeSpaces);
        Assert.True(deserialized.PurgeOptions.PurgeRebar);
        Assert.True(deserialized.PurgeOptions.PurgeFabricReinforcement);
        Assert.True(deserialized.PurgeOptions.PurgeUnused);
    }
}
