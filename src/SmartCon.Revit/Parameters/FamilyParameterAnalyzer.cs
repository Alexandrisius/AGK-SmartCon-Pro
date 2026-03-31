using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.Revit.DB;
using SmartCon.Core.Math;
using SmartCon.Revit.Extensions;
using RevitTransform = Autodesk.Revit.DB.Transform;

namespace SmartCon.Revit.Parameters;

/// <summary>
/// Вспомогательный класс для анализа FamilyParameter управляющего CONNECTOR_RADIUS.
/// Используется RevitParameterResolver и RevitLookupTableService (исключает дублирование EditFamily).
/// Вызывающий отвечает за открытие/закрытие familyDoc.
/// </summary>
internal static class FamilyParameterAnalyzer
{
    /// <summary>
    /// Анализирует FamilyDocument и определяет:
    ///  — какой FamilyParameter непосредственно связан с CONNECTOR_RADIUS у указанного коннектора,
    ///  — есть ли у него формула и корневой параметр.
    ///
    /// Коннектор в семействе ищется по ближайшему origin (instanceTransform + targetOriginGlobal).
    /// familyDoc НЕ закрывается здесь — закрывает вызывающий.
    /// </summary>
    /// <returns>
    /// Кортеж (DirectParamName, RootParamName, Formula, IsInstance) или
    /// (null, null, null, false) если параметр не найден.
    /// </returns>
    internal static (string? DirectParamName, string? RootParamName,
                     string? Formula, bool IsInstance)
        AnalyzeConnectorRadiusParam(Document familyDoc,
                                    RevitTransform instanceTransform,
                                    XYZ targetOriginGlobal)
    {
        // 1. Найти ConnectorElement по ближайшему origin (аналогично RevitFamilyConnectorService)
        var connElems = new FilteredElementCollector(familyDoc)
            .OfCategory(BuiltInCategory.OST_ConnectorElem)
            .WhereElementIsNotElementType()
            .Cast<ConnectorElement>()
            .ToList();

        if (connElems.Count == 0) return default;

        ConnectorElement? targetConnElem = null;
        double minDist = double.MaxValue;

        foreach (var ce in connElems)
        {
            var globalOrigin = instanceTransform.OfPoint(ce.Origin);
            var dist         = globalOrigin.DistanceTo(targetOriginGlobal);
            if (dist < minDist)
            {
                minDist       = dist;
                targetConnElem = ce;
            }
        }

        // Допуск 0.1 фут (~30 мм)
        if (targetConnElem is null || minDist > 0.1)
        {
            Debug.WriteLine($"[SmartCon][FPA] Target connector not found or dist too large ({minDist:F4} ft)");
            return default;
        }

        // 2. Получить Parameter CONNECTOR_RADIUS на ConnectorElement
        var radiusParam = targetConnElem.get_Parameter(BuiltInParameter.CONNECTOR_RADIUS);
        if (radiusParam is null)
        {
            Debug.WriteLine("[SmartCon][FPA] CONNECTOR_RADIUS param not found");
            return default;
        }

        // 3. Найти FamilyParameter чьи AssociatedParameters включают radiusParam на targetConnElem
        var fm = familyDoc.FamilyManager;
        FamilyParameter? directFp = null;

        foreach (FamilyParameter fp in fm.Parameters)
        {
            try
            {
                // AssociatedParameters возвращает параметры elements в семействе,
                // ассоциированные с этим FamilyParameter
                foreach (Parameter assoc in fp.AssociatedParameters)
                {
                    if (assoc.Id == radiusParam.Id &&
                        assoc.Element?.Id == targetConnElem.Id)
                    {
                        directFp = fp;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SmartCon][FPA] AssociatedParameters error for {fp.Definition?.Name}: {ex.Message}");
            }

            if (directFp is not null) break;
        }

        if (directFp is null)
        {
            Debug.WriteLine("[SmartCon][FPA] No FamilyParameter found for CONNECTOR_RADIUS");
            return default;
        }

        var directName   = directFp.Definition?.Name ?? string.Empty;
        var directIsInst = directFp.IsInstance;
        var formula      = directFp.Formula;

        if (string.IsNullOrEmpty(directName)) return default;

        Debug.WriteLine($"[SmartCon][FPA] directParam={directName}, isInstance={directIsInst}, formula={formula}");

        // 4. Нет формулы → прямой параметр
        if (string.IsNullOrWhiteSpace(formula))
            return (directName, null, null, directIsInst);

        // 5. Есть формула → найти корневой параметр
        var varNames = MiniFormulaSolver.ExtractVariables(formula);
        string? rootParamName = null;
        bool    rootIsInst    = directIsInst;

        foreach (var varName in varNames)
        {
            var rootFp = FindFamilyParameter(fm, varName);
            if (rootFp is not null)
            {
                rootParamName = rootFp.Definition.Name;
                rootIsInst    = rootFp.IsInstance;
                Debug.WriteLine($"[SmartCon][FPA] rootParam={rootParamName}, isInstance={rootIsInst}");
                break; // Phase 4: поддерживаем только 1 уровень глубины
            }
        }

        return (directName, rootParamName, formula, rootIsInst);
    }

    // ── Вспомогательные ───────────────────────────────────────────────────

    private static FamilyParameter? FindFamilyParameter(FamilyManager fm, string name)
    {
        foreach (FamilyParameter fp in fm.Parameters)
        {
            if (string.Equals(fp.Definition?.Name, name,
                StringComparison.OrdinalIgnoreCase))
                return fp;
        }
        return null;
    }
}
