using System.Diagnostics.CodeAnalysis;
#pragma warning disable CA1512 // ArgumentOutOfRangeException.ThrowIfNegative not available on net48
using System.Text.RegularExpressions;

namespace SmartCon.Core.Models;

/// <summary>
/// Lightweight semantic version parser and comparer (SemVer 2.0.0 subset).
/// No external dependencies. Supports major.minor.patch and pre-release labels.
/// </summary>
public sealed class SemVersion : IComparable<SemVersion>, IEquatable<SemVersion>
{
    private static readonly Regex s_versionRegex = new(
        @"^(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)(?:-(?<prerelease>[A-Za-z0-9\-\.]+))?(?:\+(?<metadata>[A-Za-z0-9\-\.]+))?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public string? Prerelease { get; }
    public string? Metadata { get; }

    public bool IsPrerelease => !string.IsNullOrEmpty(Prerelease);

    public SemVersion(int major, int minor, int patch, string? prerelease = null, string? metadata = null)
    {
        if (major < 0) throw new ArgumentOutOfRangeException(nameof(major));
        if (minor < 0) throw new ArgumentOutOfRangeException(nameof(minor));
        if (patch < 0) throw new ArgumentOutOfRangeException(nameof(patch));

        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease;
        Metadata = metadata;
    }

    public static SemVersion Parse(string version)
    {
        if (!TryParse(version, out var result))
            throw new FormatException($"Invalid semantic version: '{version}'");
        return result;
    }

    public static bool TryParse(string version, [NotNullWhen(true)] out SemVersion? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(version))
            return false;

        version = version.TrimStart('v', 'V');
        var match = s_versionRegex.Match(version);
        if (!match.Success)
            return false;

        var major = int.Parse(match.Groups["major"].Value);
        var minor = int.Parse(match.Groups["minor"].Value);
        var patch = int.Parse(match.Groups["patch"].Value);
        var prerelease = match.Groups["prerelease"].Success ? match.Groups["prerelease"].Value : null;
        var metadata = match.Groups["metadata"].Success ? match.Groups["metadata"].Value : null;

        result = new SemVersion(major, minor, patch, prerelease, metadata);
        return true;
    }

    public int CompareTo(SemVersion? other)
    {
        if (other is null) return 1;

        var cmp = Major.CompareTo(other.Major);
        if (cmp != 0) return cmp;

        cmp = Minor.CompareTo(other.Minor);
        if (cmp != 0) return cmp;

        cmp = Patch.CompareTo(other.Patch);
        if (cmp != 0) return cmp;

        // Stable version has higher precedence than pre-release of same M.m.p
        if (IsPrerelease && !other.IsPrerelease) return -1;
        if (!IsPrerelease && other.IsPrerelease) return 1;
        if (!IsPrerelease && !other.IsPrerelease) return 0;

        // Both are pre-release - compare identifiers
        var thisParts = Prerelease!.Split('.');
        var otherParts = other.Prerelease!.Split('.');
        var maxLen = thisParts.Length > otherParts.Length ? thisParts.Length : otherParts.Length;

        for (var i = 0; i < maxLen; i++)
        {
            if (i >= thisParts.Length) return -1;
            if (i >= otherParts.Length) return 1;

            var thisIsNumeric = int.TryParse(thisParts[i], out var thisNum);
            var otherIsNumeric = int.TryParse(otherParts[i], out var otherNum);

            if (thisIsNumeric && otherIsNumeric)
            {
                cmp = thisNum.CompareTo(otherNum);
                if (cmp != 0) return cmp;
            }
            else if (thisIsNumeric)
            {
                return -1; // numeric < alphanumeric
            }
            else if (otherIsNumeric)
            {
                return 1; // alphanumeric > numeric
            }
            else
            {
                cmp = string.Compare(thisParts[i], otherParts[i], StringComparison.Ordinal);
                if (cmp != 0) return cmp;
            }
        }

        return 0;
    }

    public bool Equals(SemVersion? other)
    {
        if (other is null) return false;
        return CompareTo(other) == 0;
    }

    public override bool Equals(object? obj) => obj is SemVersion other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = 17;
            hash = hash * 31 + Major;
            hash = hash * 31 + Minor;
            hash = hash * 31 + Patch;
            hash = hash * 31 + (Prerelease?.GetHashCode() ?? 0);
            return hash;
        }
    }

    public static bool operator ==(SemVersion? left, SemVersion? right)
    {
        if (left is null) return right is null;
        return left.Equals(right);
    }

    public static bool operator !=(SemVersion? left, SemVersion? right) => !(left == right);

    public static bool operator >(SemVersion left, SemVersion right) => left.CompareTo(right) > 0;
    public static bool operator <(SemVersion left, SemVersion right) => left.CompareTo(right) < 0;
    public static bool operator >=(SemVersion left, SemVersion right) => left.CompareTo(right) >= 0;
    public static bool operator <=(SemVersion left, SemVersion right) => left.CompareTo(right) <= 0;

    public override string ToString()
    {
        var result = $"{Major}.{Minor}.{Patch}";
        if (!string.IsNullOrEmpty(Prerelease))
            result += $"-{Prerelease}";
        if (!string.IsNullOrEmpty(Metadata))
            result += $"+{Metadata}";
        return result;
    }
}
