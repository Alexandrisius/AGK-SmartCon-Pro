using System.IO;
using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services;

/// <summary>
/// Adapter that promotes the Revit-side
/// <see cref="ISystemFamilyRevitOperations.CreateCleanProjectWithTypesAndInstances"/>
/// to the Core-abstracted <see cref="ISystemFamilyIsolationProjectService"/> contract.
///
/// Why an adapter and not direct DI wiring?
/// <list type="bullet">
///   <item>the <c>FamilyManager</c> project must not depend on
///         <c>SmartCon.Revit</c> (I-09 dependency rule), but
///         <see cref="ISystemFamilyRevitOperations"/> lives in <c>SmartCon.Revit</c>;</item>
///   <item>the adapter lives in <c>SmartCon.FamilyManager</c> which is
///         already wired with both Core and Revit in the DI graph;</item>
///   <item>callers in <c>FamilyManager</c> get a single abstraction
///         (<see cref="ISystemFamilyIsolationProjectService"/>) with
///         the production signature, keeping the VM free of Revit
///         implementation details while still passing a <see cref="Document"/>
///         (which is the universal "active project" handle the caller
///         already resolves inside the awaitable event).</item>
/// </list>
/// </summary>
internal sealed class SystemFamilyIsolationProjectAdapter : ISystemFamilyIsolationProjectService
{
    private readonly ISystemFamilyRevitOperations _revitOps;

    public SystemFamilyIsolationProjectAdapter(ISystemFamilyRevitOperations revitOps)
    {
        _revitOps = revitOps;
    }

    public CreateCleanProjectResult CreateCleanProjectWithTypesAndInstances(
        Document sourceDoc,
        IReadOnlyList<string> typeUniqueIds,
        BuiltInCategory category,
        string displayName,
        string managedRvtPath,
        string? catalogItemId = null)
    {
        using var _scope = SmartConLogger.BeginScope("CreateCleanProject",
            ("Method", "CreateCleanProjectWithTypesAndInstances"),
            ("DisplayName", displayName),
            ("File", Path.GetFileName(managedRvtPath)));
        if (sourceDoc is null)
        {
            return new CreateCleanProjectResult(false, null, "sourceDoc is null", 0);
        }

        SmartConLogger.Info(
            $"[SystemImport.Create] {displayName} ({category}): staging {typeUniqueIds.Count} type(s) -> {managedRvtPath}");

        var result = _revitOps.CreateCleanProjectWithTypesAndInstances(
            sourceDoc, typeUniqueIds, category, displayName, managedRvtPath, catalogItemId);

        if (result.Success)
        {
            SmartConLogger.Info(
                $"[SystemImport.Create] ✓ {displayName}: " +
                $"copied={result.CopiedElementsCount}, placed={result.PlacedInstancesCount}, " +
                $"file='{result.FilePath}'");
        }
        else
        {
            SmartConLogger.Warn(
                $"[SystemImport.Create] ✗ {displayName}: {result.Error} [Action: см. журнал Revit (Journal) для деталей; batch продолжит с другими категориями]");
        }

        return result;
    }
}
