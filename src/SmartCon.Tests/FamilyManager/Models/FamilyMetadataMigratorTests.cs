using System.Text.Json;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Json;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Models;

public sealed class FamilyMetadataMigratorTests
{
    [Fact]
    public void Migrate_CurrentVersion_ReturnsSameInstance()
    {
        var package = new FamilyMetadataPackage
        {
            Format = FamilyMetadataFormat.Id,
            Version = FamilyMetadataFormat.CurrentVersion
        };

        var migrated = FamilyMetadataMigrator.Migrate(package);

        Assert.Same(package, migrated);
    }

    [Fact]
    public void Migrate_UnknownFormat_ThrowsNotSupported()
    {
        var package = new FamilyMetadataPackage
        {
            Format = "smartcon.other-format",
            Version = FamilyMetadataFormat.CurrentVersion
        };

        var ex = Assert.Throws<NotSupportedException>(() => FamilyMetadataMigrator.Migrate(package));
        Assert.Contains("smartcon.other-format", ex.Message);
        Assert.Contains(FamilyMetadataFormat.Id, ex.Message);
    }

    [Fact]
    public void Migrate_OldVersion_ThrowsNotSupported()
    {
        var package = new FamilyMetadataPackage
        {
            Format = FamilyMetadataFormat.Id,
            Version = 1
        };

        var ex = Assert.Throws<NotSupportedException>(() => FamilyMetadataMigrator.Migrate(package));
        Assert.Contains("v1", ex.Message);
    }

    [Fact]
    public void Migrate_NewerVersion_ThrowsNotSupported()
    {
        var package = new FamilyMetadataPackage
        {
            Format = FamilyMetadataFormat.Id,
            Version = FamilyMetadataFormat.CurrentVersion + 1
        };

        Assert.Throws<NotSupportedException>(() => FamilyMetadataMigrator.Migrate(package));
    }

    [Fact]
    public void Migrate_NullPackage_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => FamilyMetadataMigrator.Migrate(null!));
    }
}

public sealed class FamilyMetadataFormatTests
{
    [Fact]
    public void IsRecognized_CurrentFormatAndVersion_ReturnsTrue()
    {
        Assert.True(FamilyMetadataFormat.IsRecognized(
            FamilyMetadataFormat.Id,
            FamilyMetadataFormat.CurrentVersion));
    }

    [Theory]
    [InlineData("smartcon.other", 2)]
    [InlineData("smartcon.familymanager.metadata-package", 1)]
    [InlineData("smartcon.familymanager.metadata-package", 3)]
    [InlineData(null, 2)]
    [InlineData("", 2)]
    public void IsRecognized_MismatchedValues_ReturnsFalse(string? format, int version)
    {
        Assert.False(FamilyMetadataFormat.IsRecognized(format, version));
    }
}

public sealed class JsonOptionsTests
{
    [Fact]
    public void Default_IsCaseInsensitive_AndTolerant()
    {
        Assert.True(JsonOptions.Default.PropertyNameCaseInsensitive);
        Assert.True(JsonOptions.Default.AllowTrailingCommas);
        Assert.Equal(JsonCommentHandling.Skip, JsonOptions.Default.ReadCommentHandling);
    }

    [Fact]
    public void WriteIndented_IsIndented_AndTolerant()
    {
        Assert.True(JsonOptions.WriteIndented.WriteIndented);
        Assert.True(JsonOptions.WriteIndented.PropertyNameCaseInsensitive);
    }

    [Fact]
    public void RelaxedWriteIndented_DoesNotEscapeNonAscii()
    {
        var json = JsonSerializer.Serialize("Труба", JsonOptions.RelaxedWriteIndented);

        Assert.Contains("Труба", json);
    }

    [Fact]
    public void Default_RoundTripsCyrillic()
    {
        var original = new FamilyMetadataPackage
        {
            Sections = new FamilyMetadataPackageSections { Categories = true },
            Categories = [new FamilyMetadataCategoryNode { Name = "Трубы" }]
        };

        var json = JsonSerializer.Serialize(original, JsonOptions.Default);
        var deserialized = JsonSerializer.Deserialize<FamilyMetadataPackage>(json, JsonOptions.Default);

        Assert.NotNull(deserialized);
        Assert.Single(deserialized!.Categories);
        Assert.Equal("Трубы", deserialized.Categories[0].Name);
    }

    [Fact]
    public void WriteIndented_SameOptionsInstanceAcrossCalls()
    {
        Assert.Same(JsonOptions.Default, JsonOptions.Default);
        Assert.Same(JsonOptions.WriteIndented, JsonOptions.WriteIndented);
        Assert.Same(JsonOptions.RelaxedWriteIndented, JsonOptions.RelaxedWriteIndented);
    }
}
