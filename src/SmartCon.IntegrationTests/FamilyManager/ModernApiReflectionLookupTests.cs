using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using TUnit.Core.Executors;

namespace SmartCon.IntegrationTests.FamilyManager;

/// <summary>
/// Ground truth for the 2023 staging failure (stress test 2026-08-05):
/// the R21 binary resolves Floor.Create / Ceiling.Create via reflection
/// (no REVIT2022_OR_GREATER symbol). On a 2023 runtime the exact-typed
/// lookup MUST find the modern API — the staged NewFloor fallback fired
/// instead, so this probe pins the runtime contract.
/// </summary>
public sealed class ModernApiReflectionLookupTests : RevitApiTest
{
    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task FloorCreate_ExactTypedReflectionLookup_Found()
    {
        if (int.TryParse(Application.VersionNumber, out var revitYear) && revitYear < 2022)
        {
            Skip.Test("Floor.Create(IList<CurveLoop>) exists on 2022+ runtimes only");
            return;
        }

        var createMethod = typeof(Floor).GetMethod(
            "Create",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
            null,
            new[] { typeof(Document), typeof(IList<CurveLoop>), typeof(ElementId), typeof(ElementId) },
            null);

        await Assert.That(createMethod).IsNotNull();
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task CeilingCreate_ExactTypedReflectionLookup_Found()
    {
        if (int.TryParse(Application.VersionNumber, out var revitYear) && revitYear < 2022)
        {
            Skip.Test("Ceiling.Create(IList<CurveLoop>) exists on 2022+ runtimes only");
            return;
        }

        var createMethod = typeof(Ceiling).GetMethod(
            "Create",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
            null,
            new[] { typeof(Document), typeof(IList<CurveLoop>), typeof(ElementId), typeof(ElementId) },
            null);

        await Assert.That(createMethod).IsNotNull();
    }
}
