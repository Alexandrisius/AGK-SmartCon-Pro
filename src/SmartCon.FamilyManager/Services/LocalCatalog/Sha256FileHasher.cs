using System.IO;
using System.Security.Cryptography;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

internal sealed class Sha256FileHasher
{
    public Task<string> ComputeHashAsync(string filePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.Run(() =>
        {
            using var stream = File.OpenRead(filePath);
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(stream);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }, ct);
    }
}
