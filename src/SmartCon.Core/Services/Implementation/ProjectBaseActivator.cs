using System.Linq;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Core.Services.Implementation;

/// <summary>
/// Default <see cref="IProjectBaseActivator"/>. Picks the most specific
/// database for the currently active Revit document according to decision A2
/// of #119: first project-scoped base whose binding matches becomes active,
/// otherwise the first general base. If neither kind has any candidate, the
/// active database is left untouched (the UI will then show a "project
/// mismatch" lock on the current project base).
/// </summary>
public sealed class ProjectBaseActivator : IProjectBaseActivator
{
    private readonly IDatabaseManager _dbManager;
    private readonly IProjectBaseBindingEvaluator _evaluator;

    public ProjectBaseActivator(IDatabaseManager dbManager, IProjectBaseBindingEvaluator evaluator)
    {
        _dbManager = dbManager;
        _evaluator = evaluator;
    }

    public async Task<string?> ActivateForDocumentAsync(string currentFilePath, CancellationToken ct = default)
    {
        using var _scope = SmartConLogger.BeginScope("FMProjectBase",
            ("Method", nameof(ActivateForDocumentAsync)),
            ("FilePath", System.IO.Path.GetFileName(currentFilePath)));

        if (string.IsNullOrEmpty(currentFilePath))
        {
            SmartConLogger.Debug("Empty file path — skipping activation");
            return null;
        }

        var connections = _dbManager.ListConnections();
        if (connections.Count == 0)
        {
            SmartConLogger.Debug("No connections registered — skipping activation");
            return null;
        }

        var active = _dbManager.GetActiveConnection();

        foreach (var conn in connections)
        {
            if (conn.Kind != BaseType.Project) continue;
            if (conn.ProjectBinding is null) continue;

            var match = _evaluator.Evaluate(conn.ProjectBinding, currentFilePath);
            if (match.Kind == ProjectBaseMatchKind.Match)
            {
                SmartConLogger.Info($"Project base '{conn.Name}' matches the active document");
                if (active is null || active.Id != conn.Id)
                {
                    await _dbManager.SwitchDatabaseAsync(conn.Id, ct);
                }
                return conn.Id;
            }
            SmartConLogger.Debug($"Project base '{conn.Name}' did not match: {match.Reason ?? "no reason"}");
        }

        var general = connections.FirstOrDefault(c => c.Kind == BaseType.General);
        if (general is not null)
        {
            SmartConLogger.Info($"No project base matched — falling back to general '{general.Name}'");
            if (active is null || active.Id != general.Id)
            {
                await _dbManager.SwitchDatabaseAsync(general.Id, ct);
            }
            return general.Id;
        }

        SmartConLogger.Warn(
            "No project base matched and no general base available — active database left unchanged. " +
            "[Action: Register at least one General base, or add a project base whose template matches this file]");
        return null;
    }
}