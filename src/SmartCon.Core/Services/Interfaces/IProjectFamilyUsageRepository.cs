using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Tracks family usage in Revit projects.
/// Not dependent on Revit API — writes to local SQLite.
/// </summary>
public interface IProjectFamilyUsageRepository
{
    /// <summary>Record that a family was loaded into a project.</summary>
    Task RecordUsageAsync(ProjectFamilyUsage usage, CancellationToken ct = default);

    /// <summary>Get usage history for a catalog item.</summary>
    Task<IReadOnlyList<ProjectFamilyUsage>> GetUsageForItemAsync(string catalogItemId, CancellationToken ct = default);

    /// <summary>Get usage history for a project.</summary>
    Task<IReadOnlyList<ProjectFamilyUsage>> GetUsageForProjectAsync(string projectFingerprint, CancellationToken ct = default);
}
