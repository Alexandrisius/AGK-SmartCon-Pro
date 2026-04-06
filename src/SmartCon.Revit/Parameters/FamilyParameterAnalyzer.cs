using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using SmartCon.Core.Math.FormulaEngine.Solver;
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
    /// Кортеж (DirectParamName, RootParamName, Formula, IsInstance, IsDiameter) или
    /// default если параметр не найден. IsDiameter=true означает что FP управляет
    /// CONNECTOR_DIAMETER (а не CONNECTOR_RADIUS) → таблица хранит диаметры.
    /// </returns>
    internal static (string? DirectParamName, string? RootParamName,
                     string? Formula, bool IsInstance, bool IsDiameter)
        AnalyzeConnectorRadiusParam(Document familyDoc,
                                    RevitTransform instanceTransform,
                                    XYZ targetOriginGlobal)
    {
        // 1. Найти ConnectorElement по ближайшему origin (аналогично RevitFamilyConnectorService)
        SmartConLogger.LookupSection("FamilyParameterAnalyzer.AnalyzeConnectorRadiusParam");
        SmartConLogger.Lookup($"  targetOriginGlobal=({targetOriginGlobal.X:F4}, {targetOriginGlobal.Y:F4}, {targetOriginGlobal.Z:F4})");

        var connElems = new FilteredElementCollector(familyDoc)
            .OfCategory(BuiltInCategory.OST_ConnectorElem)
            .WhereElementIsNotElementType()
            .Cast<ConnectorElement>()
            .ToList();

        SmartConLogger.Lookup($"  ConnectorElement в семействе: {connElems.Count}");

        if (connElems.Count == 0)
        {
            SmartConLogger.Lookup("  Нет ConnectorElement → return default");
            return default;
        }

        // Score-based алгоритм (аналог RevitFamilyConnectorService):
        // Score=2.0  → точное совпадение позиции (distLocal<0.001 ft)
        // Score≈1.0  → совпадение направления (параметрическое семейство другого размера)
        // Score<0.99 → нет совпадения
        var targetOriginLocal = instanceTransform.Inverse.OfPoint(targetOriginGlobal);
        var targetLen = targetOriginLocal.GetLength();
        var targetDir = targetLen > 1e-6 ? targetOriginLocal.Divide(targetLen) : null;

        ConnectorElement? targetConnElem = null;
        double bestScore = double.MinValue;

        foreach (var ce in connElems)
        {
            var globalOrigin = instanceTransform.OfPoint(ce.Origin);
            var distLocal    = (ce.Origin - targetOriginLocal).GetLength();
            double score;
            if (distLocal < 0.001)
            {
                score = 2.0;
            }
            else if (targetDir is not null && ce.Origin.GetLength() > 1e-6)
            {
                score = ce.Origin.Normalize().DotProduct(targetDir);
            }
            else
            {
                score = -distLocal;
            }
            SmartConLogger.Lookup($"  ConnElem id={ce.Id.Value}: localOrigin=({ce.Origin.X:F4},{ce.Origin.Y:F4},{ce.Origin.Z:F4}), globalOrigin=({globalOrigin.X:F4},{globalOrigin.Y:F4},{globalOrigin.Z:F4}), distLocal={distLocal:F4} score={score:F4}");
            if (score > bestScore) { bestScore = score; targetConnElem = ce; }
        }

        SmartConLogger.Lookup($"  Лучший ConnectorElement: id={targetConnElem?.Id.Value}, bestScore={bestScore:F4} (мин. 0.99)");

        if (targetConnElem is null || bestScore < 0.99)
        {
            SmartConLogger.Lookup($"  ВНИМАНИЕ: коннектор не найден или score слишком мал ({bestScore:F4} < 0.99) → return default");
            SmartConLogger.Warn($"[FPA] Target connector not found or score too low ({bestScore:F4})");
            return default;
        }

        // 2. Получить Parameter CONNECTOR_RADIUS на ConnectorElement
        var radiusParam = targetConnElem.get_Parameter(BuiltInParameter.CONNECTOR_RADIUS);
        if (radiusParam is null)
        {
            SmartConLogger.Lookup($"  ВНИМАНИЕ: CONNECTOR_RADIUS param не найден на ConnectorElement id={targetConnElem.Id.Value} → return default");
            return default;
        }

        SmartConLogger.Lookup($"  CONNECTOR_RADIUS: Id={radiusParam.Id.Value}, Value={radiusParam.AsDouble():F6} ft");

        // Также принимаем CONNECTOR_DIAMETER — FamilyParameter 'DN' обычно управляет диаметром
        var diamParam = targetConnElem.get_Parameter(BuiltInParameter.CONNECTOR_DIAMETER);
        SmartConLogger.Lookup($"  CONNECTOR_DIAMETER: Id={diamParam?.Id.Value.ToString() ?? "null"}, Value={diamParam?.AsDouble():F6} ft");

        // 3. Найти FamilyParameter чьи AssociatedParameters включают radiusParam/diamParam на targetConnElem
        var fm = familyDoc.FamilyManager;
        FamilyParameter? directFp   = null;
        bool foundViaDiameter = false;

        SmartConLogger.Lookup($"  Перебор FamilyParameter (кол-во: {fm.Parameters.Size}) для поиска AssociatedParameters:");

        foreach (FamilyParameter fp in fm.Parameters)
        {
            try
            {
                // AssociatedParameters возвращает параметры elements в семействе,
                // ассоциированные с этим FamilyParameter
                var assocParams = fp.AssociatedParameters;
                int assocCount  = 0;
                try { assocCount = assocParams.Size; } catch { }

                if (assocCount > 0)
                    SmartConLogger.Lookup($"    FP '{fp.Definition?.Name}': formula='{fp.Formula}', isInstance={fp.IsInstance}, associatedCount={assocCount}");

                foreach (Parameter assoc in assocParams)
                {
                    bool idMatch   = assoc.Id == radiusParam.Id
                                   || (diamParam is not null && assoc.Id == diamParam.Id);
                    bool elemMatch = assoc.Element?.Id == targetConnElem.Id;

                    if (assocCount > 0)
                        SmartConLogger.Lookup($"      assoc.Id={assoc.Id.Value}, radiusParam.Id={radiusParam.Id.Value}, diamParam.Id={diamParam?.Id.Value.ToString() ?? "null"}, idMatch={idMatch} | assoc.Element.Id={assoc.Element?.Id.Value}, targetConn.Id={targetConnElem.Id.Value}, elemMatch={elemMatch}");

                    if (idMatch && elemMatch)
                    {
                        directFp         = fp;
                        foundViaDiameter = (diamParam is not null && assoc.Id == diamParam.Id);
                        SmartConLogger.Lookup($"      ✓ НАЙДЕНО! directFp='{fp.Definition?.Name}', foundViaDiameter={foundViaDiameter}");
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                SmartConLogger.Lookup($"    FP '{fp.Definition?.Name}': AssociatedParameters ИСКЛЮЧЕНИЕ: {ex.GetType().Name}: {ex.Message}");
                SmartConLogger.Warn($"[FPA] AssociatedParameters error for '{fp.Definition?.Name}': {ex.Message}");
            }

            if (directFp is not null) break;
        }

        if (directFp is null)
        {
            SmartConLogger.Lookup("  ВНИМАНИЕ: FamilyParameter для CONNECTOR_RADIUS/DIAMETER не найден в AssociatedParameters → return default");
            SmartConLogger.Warn("[FPA] No FamilyParameter found for CONNECTOR_RADIUS/DIAMETER");
            return default;
        }

        var directName   = directFp.Definition?.Name ?? string.Empty;
        var directIsInst = directFp.IsInstance;
        var formula      = directFp.Formula;

        if (string.IsNullOrEmpty(directName))
        {
            SmartConLogger.Lookup("  directName пустой → return default");
            return default;
        }

        SmartConLogger.Lookup($"  directParam='{directName}', isInstance={directIsInst}, formula='{formula}'");

        // 4. Нет формулы → прямой параметр
        if (string.IsNullOrWhiteSpace(formula))
        {
            SmartConLogger.Lookup($"  → Нет формулы → return ('{directName}', null, null, {directIsInst}, isDiameter={foundViaDiameter})");
            return (directName, null, null, directIsInst, foundViaDiameter);
        }

        // 5. Есть формула → найти корневой параметр

        // 5a. Если формула содержит size_lookup → корневой параметр = первый query-параметр.
        //     Без этого generic search ниже выбирает BP_LookupTable (имя таблицы, longest-first)
        //     вместо реального query-параметра (DN_test_2, BP_NominalDiameter и т.д.).
        string? rootParamName = null;
        bool    rootIsInst    = directIsInst;

        try
        {
            var sizeLookup = FormulaSolver.ParseSizeLookupStatic(formula);
            if (sizeLookup is not null && sizeLookup.Value.QueryParameters.Count > 0)
            {
                var queryName = sizeLookup.Value.QueryParameters[0];
                var queryFp   = FindFamilyParameter(fm, queryName);
                if (queryFp is not null)
                {
                    rootParamName = queryName;
                    rootIsInst    = queryFp.IsInstance;
                    SmartConLogger.Lookup($"    → rootParam из size_lookup query[0]: '{rootParamName}', isInstance={rootIsInst}");
                }
                else
                {
                    SmartConLogger.Lookup($"    → size_lookup query[0]='{queryName}' но FamilyParameter не найден, fallback на generic search");
                }
            }
        }
        catch
        {
            // Формула не парсится (спецсимволы и т.д.) → fallback на generic search
        }

        // 5b. Generic fallback: найти корневой параметр по реальным именам FP (longest-first)
        //     Это нужно для имён с пробелами, например 'ADSK_Диаметр условный',
        //     которые ExtractVariables ошибочно разбивал на отдельные токены.
        if (rootParamName is null)
        {

        var candidates = new List<(string Name, FamilyParameter Fp)>();
        foreach (FamilyParameter candidate in fm.Parameters)
        {
            var candidateName = candidate.Definition?.Name;
            if (!string.IsNullOrEmpty(candidateName) &&
                !string.Equals(candidateName, directName, StringComparison.OrdinalIgnoreCase))
                candidates.Add((candidateName, candidate));
        }
        candidates.Sort((a, b) => b.Name.Length.CompareTo(a.Name.Length));

        SmartConLogger.Lookup($"  Поиск rootParam в формуле '{formula}' (кандидатов: {candidates.Count}):");

        foreach (var (name, candidateFp) in candidates)
        {
            if (!ContainsParamReference(formula, name)) continue;
            rootParamName = name;
            rootIsInst    = candidateFp.IsInstance;
            SmartConLogger.Lookup($"    → rootParam='{rootParamName}', isInstance={rootIsInst}");
            break;
        }

        if (rootParamName is null)
            SmartConLogger.Lookup("    → rootParam не найден");

        } // end if (rootParamName is null) — generic fallback block

        SmartConLogger.Lookup($"  → return ('{directName}', '{rootParamName}', '{formula}', {rootIsInst}, isDiameter={foundViaDiameter})");
        return (directName, rootParamName, formula, rootIsInst, foundViaDiameter);
    }

    // ── Вспомогательные ───────────────────────────────────────────────────

    /// <summary>
    /// Проверяет, встречается ли <paramref name="paramName"/> в <paramref name="formula"/>
    /// как самостоятельное имя параметра (не часть другого идентификатора).
    /// </summary>
    private static bool ContainsParamReference(string formula, string paramName)
    {
        int idx = 0;
        while ((idx = formula.IndexOf(paramName, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            bool leftOk  = idx == 0 || !IsIdentChar(formula[idx - 1]);
            bool rightOk = idx + paramName.Length >= formula.Length
                        || !IsIdentChar(formula[idx + paramName.Length]);
            if (leftOk && rightOk) return true;
            idx += paramName.Length;
        }
        return false;
    }

    private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';

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
