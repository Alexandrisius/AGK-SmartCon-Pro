using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
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
        IConnectorService connSvc,
        ConnectionTypeCode dynamicTypeCode = default)
    {
        var fitting = doc.GetElement(fittingId);
        if (fitting is null) return null;

        var cm = fitting.GetConnectorManager();
        if (cm is null) return null;

        var fittingConns = cm.Connectors
            .Cast<Connector>()
            .Where(c => c.ConnectorType != ConnectorType.Curve)
            .ToList();

        if (fittingConns.Count < 2) return null;

        Connector? fitConn1 = null;
        Connector? fitConn2 = null;

        var connCtcMap = new List<(Connector Conn, ConnectionTypeCode Ctc)>();
        foreach (var c in fittingConns)
        {
            var ctc = ConnectionTypeCode.Parse(GetConnectorDescriptionSafe(c));
            connCtcMap.Add((c, ctc));
            SmartConLogger.Info($"[FitAlign] conn[{c.Id}] CTC={ctc.Value} R={c.Radius * 304.8:F1}mm (static CTC={staticProxy.ConnectionTypeCode.Value}, dyn CTC={dynamicTypeCode.Value})");
        }

        if (staticProxy.ConnectionTypeCode.IsDefined)
        {
            // Стратегия 0 (Cross-connect / Reducer):
            // staticCTC ≠ dynamicCTC — коннекторы РАЗНЫХ типов.
            // Reducer conn с CTC==dynamicCTC → fc1 (к static), conn с CTC==staticCTC → fc2 (к dynamic).
            // Перекрёстное назначение: ВР conn к НР элементу, НР conn к ВР элементу.
            // Для СВАРКА↔СВАРКА: staticCTC==dynamicCTC → стратегия пропускается.
            if (dynamicTypeCode.IsDefined
                && staticProxy.ConnectionTypeCode.Value != dynamicTypeCode.Value)
            {
                var staticMatch = connCtcMap.FirstOrDefault(x =>
                    x.Ctc.IsDefined && x.Ctc.Value == staticProxy.ConnectionTypeCode.Value);
                var dynMatch = connCtcMap.FirstOrDefault(x =>
                    x.Ctc.IsDefined && x.Ctc.Value == dynamicTypeCode.Value);

                if (staticMatch.Conn is not null && dynMatch.Conn is not null)
                {
                    fitConn1 = dynMatch.Conn;
                    fitConn2 = staticMatch.Conn;
                    SmartConLogger.Info($"[FitAlign] Стратегия 0 (Cross-connect): staticCTC≠dynCTC " +
                        $"({staticProxy.ConnectionTypeCode.Value}≠{dynamicTypeCode.Value}) → " +
                        $"fc1=conn[{fitConn1.Id}] (CTC={dynMatch.Ctc.Value}→static), " +
                        $"fc2=conn[{fitConn2.Id}] (CTC={staticMatch.Ctc.Value}→dynamic)");
                }
            }

            // Стратегия 1: прямое совпадение CTC фитинга == static CTC
            // Пропускается если Strategy 0 (cross-connect) уже назначила fc1
            if (fitConn1 is null)
            {
                var directMatch = connCtcMap.FirstOrDefault(x =>
                    x.Ctc.IsDefined && x.Ctc.Value == staticProxy.ConnectionTypeCode.Value);
                if (directMatch.Conn is not null)
                {
                    fitConn1 = directMatch.Conn;
                    fitConn2 = fittingConns.FirstOrDefault(c => c.Id != fitConn1.Id);
                    SmartConLogger.Info($"[FitAlign] Стратегия 1 (CTC match): fc1=conn[{fitConn1.Id}], fc2=conn[{fitConn2?.Id}]");
                }
            }

            // Стратегия 2: conn с CTC == dynamicTypeCode → fc2 (к dynamic), другой → fc1
            if (fitConn1 is null && dynamicTypeCode.IsDefined)
            {
                var dynMatch = connCtcMap.FirstOrDefault(x =>
                    x.Ctc.IsDefined && x.Ctc.Value == dynamicTypeCode.Value);
                if (dynMatch.Conn is not null)
                {
                    fitConn2 = dynMatch.Conn;
                    fitConn1 = fittingConns.FirstOrDefault(c => c.Id != fitConn2.Id);
                    SmartConLogger.Info($"[FitAlign] Стратегия 2 (dynamicTypeCode match): fc2=conn[{fitConn2.Id}] (CTC={dynMatch.Ctc.Value}), fc1=conn[{fitConn1?.Id}]");
                }
            }

            // Стратегия 3: исключение — один conn с определённым CTC ≠ static → fc2
            if (fitConn1 is null)
            {
                var definedOther = connCtcMap
                    .Where(x => x.Ctc.IsDefined && x.Ctc.Value != staticProxy.ConnectionTypeCode.Value)
                    .ToList();
                if (definedOther.Count == 1)
                {
                    fitConn2 = definedOther[0].Conn;
                    fitConn1 = fittingConns.FirstOrDefault(c => c.Id != fitConn2.Id);
                    SmartConLogger.Info($"[FitAlign] Стратегия 3 (исключение): fc2=conn[{fitConn2.Id}] (CTC={definedOther[0].Ctc.Value}≠static), fc1=conn[{fitConn1?.Id}]");
                }
            }
        }

        // Стратегия 4: fallback по расстоянию
        if (fitConn1 is null)
        {
            var ordered = fittingConns.OrderBy(c => c.Origin.DistanceTo(staticProxy.Origin)).ToList();
            fitConn1 = ordered[0];
            fitConn2 = ordered[1];
            SmartConLogger.Info($"[FitAlign] Стратегия 4 (distance fallback): fc1=conn[{fitConn1.Id}], fc2=conn[{fitConn2.Id}]");
        }

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
        if (fitConn2 is null) return null;
        return connSvc.RefreshConnector(doc, fittingId, fitConn2.ToProxy()?.ConnectorIndex ?? -1)
               ?? fitConn2.ToProxy();
    }

    public void DeleteElement(Document doc, ElementId elementId)
    {
        doc.Delete(elementId);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static string? GetConnectorDescriptionSafe(Connector connector)
    {
        try { return connector.Description; }
        catch (Autodesk.Revit.Exceptions.InvalidOperationException) { return null; }
    }

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
