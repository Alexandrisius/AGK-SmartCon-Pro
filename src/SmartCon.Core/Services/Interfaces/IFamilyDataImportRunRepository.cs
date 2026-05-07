using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

public interface IFamilyDataImportRunRepository
{
    Task<FamilyDataImportRun?> GetLatestRunAsync(string catalogItemId, CancellationToken ct = default);
    Task<IReadOnlyList<FamilyDataImportRun>> GetRunsForItemAsync(string catalogItemId, CancellationToken ct = default);
    Task<FamilyDataImportRun> CreateRunAsync(FamilyDataImportRun run, CancellationToken ct = default);
    Task<FamilyDataImportRun> UpdateRunAsync(string runId, FamilyDataImportStatus status, int typesCount, DateTimeOffset completedAtUtc, string? errorMessage, CancellationToken ct = default);
}
