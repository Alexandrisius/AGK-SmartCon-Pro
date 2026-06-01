using System.IO;
using System.Security.Cryptography;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

internal sealed class Sha256FileHasher
{
    private const int MaxRetries = 3;
    private static readonly int[] RetryDelaysMs = [100, 300, 900];

    public Task<string> ComputeHashAsync(string filePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.Run(() =>
        {
            for (var attempt = 0; attempt < MaxRetries; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    using var stream = new FileStream(
                        filePath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite);
                    using var sha = SHA256.Create();
                    var hash = sha.ComputeHash(stream);
                    return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                }
                catch (IOException) when (attempt < MaxRetries - 1)
                {
                    Thread.Sleep(RetryDelaysMs[attempt]);
                }
            }

            // Final attempt - let exception propagate if it fails
            using var finalStream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);
            using var finalSha = SHA256.Create();
            var finalHash = finalSha.ComputeHash(finalStream);
            return BitConverter.ToString(finalHash).Replace("-", "").ToLowerInvariant();
        }, ct);
    }
}
