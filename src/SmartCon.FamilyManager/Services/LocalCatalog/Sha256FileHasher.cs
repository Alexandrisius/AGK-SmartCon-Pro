using System.IO;
using System.Security.Cryptography;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

internal sealed class Sha256FileHasher
{
    public Task<string> ComputeHashAsync(string filePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var stream = File.OpenRead(filePath);
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(stream);
        return Task.FromResult(BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant());
    }
}
