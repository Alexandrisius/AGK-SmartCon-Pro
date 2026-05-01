using System.IO;
using System.Security.Cryptography;
using SmartCon.FamilyManager.Services.LocalCatalog;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Repository;

public sealed class Sha256FileHasherTests : IDisposable
{
    private readonly string _tempDir;
    private readonly Sha256FileHasher _hasher = new();

    public Sha256FileHasherTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"Sha256Test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task ComputeHash_KnownContent_ReturnsExpectedHash()
    {
        var path = Path.Combine(_tempDir, "test.rfa");
        var content = "Hello, World!"u8.ToArray();
        await File.WriteAllBytesAsync(path, content);

        var expected = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var result = await _hasher.ComputeHashAsync(path);

        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task ComputeHash_DifferentContent_DifferentHash()
    {
        var path1 = Path.Combine(_tempDir, "a.rfa");
        var path2 = Path.Combine(_tempDir, "b.rfa");
        await File.WriteAllBytesAsync(path1, "content1"u8.ToArray());
        await File.WriteAllBytesAsync(path2, "content2"u8.ToArray());

        var hash1 = await _hasher.ComputeHashAsync(path1);
        var hash2 = await _hasher.ComputeHashAsync(path2);

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public async Task ComputeHash_NonExistentFile_Throws()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            _hasher.ComputeHashAsync(Path.Combine(_tempDir, "nonexistent.rfa")));
    }

    [Fact]
    public async Task ComputeHash_SameContentTwice_SameHash()
    {
        var path = Path.Combine(_tempDir, "same.rfa");
        await File.WriteAllBytesAsync(path, "identical content"u8.ToArray());

        var hash1 = await _hasher.ComputeHashAsync(path);
        var hash2 = await _hasher.ComputeHashAsync(path);

        Assert.Equal(hash1, hash2);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, true);
        }
        catch
        {
        }
    }
}
