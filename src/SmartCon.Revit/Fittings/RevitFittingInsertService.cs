using Autodesk.Revit.DB;
using SmartCon.Core.Math;
using SmartCon.Core.Models;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Extensions;
using SmartCon.Revit.Wrappers;
using RevitFamily = Autodesk.Revit.DB.Family;

namespace SmartCon.Revit.Fittings;

/// <summary>
/// Вставка и позиционирование фитингов через Revit API (Phase 5 + 8).
/// Вызывать только внутри Transaction (I-03).
/// </summary>
public sealed class RevitFittingInsertService : IFittingInsertService
{
    public ElementId? InsertFitting(Document doc, string familyName, string symbolName, XYZ position)
    {
        var symbol = FindFamilySymbol(doc, familyName, symbolName);
        if (symbol is null) return null;

        if (!symbol.IsActive) symbol.Activate();

        var level = GetNearestLevel(doc, position);

        var instance = doc.Create.NewFamilyInstance(
            position,
            symbol,
            level,
            Autodesk.Revit.DB.Structure.StructuralType.NonStructural);

        return instance?.Id;
    }

    public ConnectorProxy? AlignFittingToStatic(
        Document doc,
        ElementId fittingId,
        ConnectorProxy staticProxy,
        ITransformService transformSvc,
        IConnectorService connSvc)
    {
        var fitting = doc.GetElement(fittingId);
        if (fitting is null) return null;

        var cm = fitting.GetConnectorManager();
        if (cm is null) return null;

        // Получаем все свободные коннекторы фитинга
        var fittingConns = cm.Connectors
            .Cast<Connector>()
            .Where(c => c.ConnectorType != ConnectorType.Curve)
            .OrderBy(c => c.Origin.DistanceTo(staticProxy.Origin))
            .ToList();

        if (fittingConns.Count < 2) return null;

        var fitConn1 = fittingConns[0]; // ближайший к static
        var fitConn2 = fittingConns[1]; // дальний

        var fitConn1Proxy = fitConn1.ToProxy();
        if (fitConn1Proxy is null) return null;

        // Вычислить выравнивание фитинга к static коннектору
        var alignResult = ConnectorAligner.ComputeAlignment(
            staticProxy.OriginVec3, staticProxy.BasisZVec3, staticProxy.BasisXVec3,
            fitConn1Proxy.OriginVec3, fitConn1Proxy.BasisZVec3, fitConn1Proxy.BasisXVec3);

        // Шаг 1: перемещение
        if (!VectorUtils.IsZero(alignResult.InitialOffset))
            transformSvc.MoveElement(doc, fittingId, alignResult.InitialOffset);

        // Шаг 2: поворот BasisZ
        if (alignResult.BasisZRotation is { } bzRot)
            transformSvc.RotateElement(doc, fittingId,
                alignResult.RotationCenter, bzRot.Axis, bzRot.AngleRadians);

        // Шаг 3: снэп BasisX
        if (alignResult.BasisXSnap is { } bxSnap)
            transformSvc.RotateElement(doc, fittingId,
                alignResult.RotationCenter, bxSnap.Axis, bxSnap.AngleRadians);

        // Шаг 4: коррекция позиции
        doc.Regenerate();
        var refreshedFitConn1 = connSvc.RefreshConnector(doc, fittingId, fitConn1Proxy.ConnectorIndex);
        if (refreshedFitConn1 is not null)
        {
            var correction = staticProxy.OriginVec3 - refreshedFitConn1.OriginVec3;
            if (!VectorUtils.IsZero(correction))
                transformSvc.MoveElement(doc, fittingId, correction);
        }

        // Регенерация для получения актуального положения fitConn2
        doc.Regenerate();

        // Возвращаем ConnectorProxy второго коннектора фитинга после выравнивания
        return connSvc.RefreshConnector(doc, fittingId, fitConn2.ToProxy()?.ConnectorIndex ?? -1)
               ?? fitConn2.ToProxy();
    }

    public void DeleteElement(Document doc, ElementId elementId)
    {
        doc.Delete(elementId);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static FamilySymbol? FindFamilySymbol(Document doc, string familyName, string symbolName)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(FamilySymbol))
            .Cast<FamilySymbol>()
            .FirstOrDefault(s =>
                string.Equals(s.Family.Name, familyName, StringComparison.OrdinalIgnoreCase) &&
                (symbolName == "*" || string.Equals(s.Name, symbolName, StringComparison.OrdinalIgnoreCase)));
    }

    private static Level GetNearestLevel(Document doc, XYZ point)
    {
        var levels = new FilteredElementCollector(doc)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .OrderBy(l => System.Math.Abs(l.Elevation - point.Z))
            .ToList();

        return levels.FirstOrDefault()
               ?? new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().First();
    }
}
