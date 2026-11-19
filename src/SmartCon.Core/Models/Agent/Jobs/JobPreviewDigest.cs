using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SmartCon.Core.Models.Agent.Jobs;

/// <summary>
/// Canonical, process-independent hashing of a frozen preview. The digest binds
/// the apply call to: action, document, frozen unit selection, spec and expiry.
/// </summary>
public static class JobPreviewDigest
{
    public const string Algorithm = "sha256";

    private const string Version = "job-preview-v1";

    public static string Compute(
        string action,
        string documentId,
        string selectorHash,
        string specHash,
        int count,
        int willChangeCount,
        DateTime expiresAtUtc)
    {
        var canonical = new StringBuilder();
        canonical.Append(Version).Append('\n')
            .Append(action ?? string.Empty).Append('\n')
            .Append(documentId ?? string.Empty).Append('\n')
            .Append(selectorHash ?? string.Empty).Append('\n')
            .Append(specHash ?? string.Empty).Append('\n')
            .Append(count.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append(willChangeCount.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append(expiresAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        return Sha256Hex(canonical.ToString());
    }

    /// <summary>Hash of the sorted frozen units: any selection drift changes the digest.</summary>
    public static string ComputeSelectorHash(IEnumerable<JobUnit> units)
    {
        var entries = units
            .Select(unit => unit.UnitKey + "\u001f" + unit.ArgsJson)
            .OrderBy(text => text, StringComparer.Ordinal)
            .ToArray();
        return Sha256Hex(string.Join("\n", entries));
    }

    public static string ComputeSpecHash(string? specJson) =>
        Sha256Hex((specJson ?? "{}").Trim());

    public static string Sha256Hex(string text)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
        return ToHex(bytes);
    }

    private static string ToHex(byte[] bytes)
    {
#if NET8_0_OR_GREATER
        return Convert.ToHexString(bytes).ToLowerInvariant();
#else
        var chars = new char[bytes.Length * 2];
        const string alphabet = "0123456789abcdef";
        for (var i = 0; i < bytes.Length; i++)
        {
            chars[i * 2] = alphabet[bytes[i] >> 4];
            chars[i * 2 + 1] = alphabet[bytes[i] & 0xF];
        }

        return new string(chars);
#endif
    }
}

/// <summary>Pure save-policy decisions, so they can be unit-tested without Revit.</summary>
public static class JobSavePolicyDecider
{
    public static bool RequiresSaveableDocument(JobSavePolicy policy) =>
        policy != JobSavePolicy.Never;

    public static bool ShouldSaveAfterBatch(JobSavePolicy policy, bool batchHasChanges) =>
        policy == JobSavePolicy.AfterEachBatch && batchHasChanges;

    public static bool ShouldSaveOnTimer(
        JobSavePolicy policy,
        DateTime? lastSavedAtUtc,
        DateTime utcNow,
        int everyMinutes)
    {
        if (policy != JobSavePolicy.EveryNMinutes)
        {
            return false;
        }

        if (everyMinutes <= 0)
        {
            everyMinutes = 5;
        }

        var anchor = lastSavedAtUtc ?? DateTime.MinValue;
        return utcNow - anchor >= TimeSpan.FromMinutes(everyMinutes);
    }
}