using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Math.FormulaEngine.Solver;

namespace SmartCon.Revit.Parameters;

/// <summary>
/// Вспомогательный класс для анализа FamilyParameter, управляющего размером коннектора.
/// Используется RevitParameterResolver, RevitLookupTableService и RevitDynamicSizeResolver.
/// Вызывающий отвечает за открытие/закрытие familyDoc.
///
/// Имя связанного параметра (paramName) и isDiameter определяются в ПРОЕКТЕ через
/// <see cref="ConnectorSizeBindingResolver"/> (MEPFamilyConnectorInfo) — поиск
/// ConnectorElement по геометрии удалён: он ломался, когда экземплярные параметры
/// (угол отвода, DN) отличались от шаблона семейства (issue #161).
/// </summary>
internal static class FamilyParameterAnalyzer
{
    /// <summary>
    /// Анализирует FamilyDocument для параметра с именем <paramref name="paramName"/>:
    /// есть ли у него формула и корневой (query) параметр для записи.
    /// familyDoc НЕ закрывается здесь — закрывает вызывающий.
    /// </summary>
    /// <returns>
    /// Кортеж (DirectParamName, RootParamName, Formula, IsInstance, IsDiameter) или
    /// default если параметр не найден. IsInstance — от корневого параметра при наличии
    /// формулы (запись идёт в root), иначе от прямого. IsDiameter пробрасывается из
    /// проектной привязки (CONNECTOR_DIAMETER → таблица хранит диаметры).
    /// </returns>
    internal static (string? DirectParamName, string? RootParamName,
                     string? Formula, bool IsInstance, bool IsDiameter)
        AnalyzeConnectorRadiusParam(Document familyDoc, string paramName, bool isDiameter)
    {
        // NOTE: no BeginScope here — this analyzer is called in hot loops over family
        // parameters (~1.5k calls per operation) and the scope markers produced
        // thousands of INF lines (log audit 2026-07). Caller scopes provide context.
        var fm = familyDoc.FamilyManager;
        if (fm is null)
        {
            SmartConLogger.Debug("  FamilyManager=null → return default");
            return default;
        }

        var directFp = FindFamilyParameter(fm, paramName);
        if (directFp is null)
        {
            SmartConLogger.Debug($"  FamilyParameter '{paramName}' not found in family → return default");
            SmartConLogger.Warn($"FamilyParameter '{paramName}' not found in '{familyDoc.Title}' " +
                $"[Action: проверьте, что параметр существует в семействе и привязан к размеру коннектора]");
            return default;
        }

        var directName = directFp.Definition?.Name ?? string.Empty;
        var directIsInst = directFp.IsInstance;
        var formula = directFp.Formula;

        if (string.IsNullOrEmpty(directName))
        {
            SmartConLogger.Debug("  directName is empty → return default");
            return default;
        }

        SmartConLogger.Debug($"  directParam='{directName}', isInstance={directIsInst}, formula='{formula}'");

        // Нет формулы → прямой параметр
        if (string.IsNullOrWhiteSpace(formula))
        {
            SmartConLogger.Debug($"  → No formula → return ('{directName}', null, null, {directIsInst}, isDiameter={isDiameter})");
            return (directName, null, null, directIsInst, isDiameter);
        }

        // Есть формула → найти корневой параметр

        // 5a. Если формула содержит size_lookup → корневой параметр = первый query-параметр.
        //     Без этого generic search ниже выбирает BP_LookupTable (имя таблицы, longest-first)
        //     вместо реального query-параметра (DN_test_2, BP_NominalDiameter и т.д.).
        string? rootParamName = null;
        bool rootIsInst = directIsInst;

        try
        {
            var sizeLookup = FormulaSolver.ParseSizeLookupStatic(formula);
            if (sizeLookup is not null && sizeLookup.Value.QueryParameters.Count > 0)
            {
                var queryName = sizeLookup.Value.QueryParameters[0];
                var queryFp = FindFamilyParameter(fm, queryName);
                if (queryFp is not null)
                {
                    rootParamName = queryName;
                    rootIsInst = queryFp.IsInstance;
                    SmartConLogger.Debug($"    → rootParam from size_lookup query[0]: '{rootParamName}', isInstance={rootIsInst}");
                }
                else
                {
                    SmartConLogger.Debug($"    → size_lookup query[0]='{queryName}' but FamilyParameter not found, fallback to generic search");
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
                    candidates.Add((candidateName!, candidate));
            }
            candidates.Sort((a, b) => b.Name.Length.CompareTo(a.Name.Length));

            SmartConLogger.Debug($"  Searching rootParam in formula '{formula}' (candidates: {candidates.Count}):");

            foreach (var (name, candidateFp) in candidates)
            {
                if (!SmartCon.Core.Services.FormulaParamMatcher.ContainsParamReference(formula, name)) continue;
                rootParamName = name;
                rootIsInst = candidateFp.IsInstance;
                SmartConLogger.Debug($"    → rootParam='{rootParamName}', isInstance={rootIsInst}");
                break;
            }

            if (rootParamName is null)
                SmartConLogger.Debug("    → rootParam not found");
        }

        SmartConLogger.Debug($"  → return ('{directName}', '{rootParamName}', '{formula}', {rootIsInst}, isDiameter={isDiameter})");
        return (directName, rootParamName, formula, rootIsInst, isDiameter);
    }

    /// <summary>
    /// Overload working on a pre-built <see cref="FamilyParameterSnapshot"/> —
    /// no familyDoc needed (phase 3, #161). Identical semantics to the
    /// familyDoc overload: (DirectParamName, RootParamName, Formula, IsInstance,
    /// IsDiameter); IsInstance from the root parameter when a formula exists.
    /// </summary>
    internal static (string? DirectParamName, string? RootParamName,
                     string? Formula, bool IsInstance, bool IsDiameter)
        AnalyzeConnectorRadiusParam(FamilyParameterSnapshot snapshot, string paramName, bool isDiameter)
    {
        var (formula, directIsInst, found) = FindInSnapshot(snapshot, paramName);
        if (!found)
        {
            SmartConLogger.Debug($"  FamilyParameter '{paramName}' not found in snapshot → return default");
            SmartConLogger.Warn($"FamilyParameter '{paramName}' not found in family snapshot " +
                $"[Action: проверьте, что параметр существует в семействе и привязан к размеру коннектора]");
            return default;
        }

        SmartConLogger.Debug($"  directParam='{paramName}', isInstance={directIsInst}, formula='{formula}'");

        if (string.IsNullOrWhiteSpace(formula))
        {
            SmartConLogger.Debug($"  → No formula → return ('{paramName}', null, null, {directIsInst}, isDiameter={isDiameter})");
            return (paramName, null, null, directIsInst, isDiameter);
        }

        var formulaNonNull = formula!;

        string? rootParamName = null;
        bool rootIsInst = directIsInst;

        try
        {
            var sizeLookup = FormulaSolver.ParseSizeLookupStatic(formulaNonNull);
            if (sizeLookup is not null && sizeLookup.Value.QueryParameters.Count > 0)
            {
                var queryName = sizeLookup.Value.QueryParameters[0];
                var (qFormula, qIsInst, qFound) = FindInSnapshot(snapshot, queryName);
                if (qFound)
                {
                    rootParamName = queryName;
                    rootIsInst = qIsInst;
                    SmartConLogger.Debug($"    → rootParam from size_lookup query[0]: '{rootParamName}', isInstance={rootIsInst}");
                }
                else
                {
                    SmartConLogger.Debug($"    → size_lookup query[0]='{queryName}' but FamilyParameter not found, fallback to generic search");
                }
            }
        }
        catch
        {
            // Формула не парсится (спецсимволы и т.д.) → fallback на generic search
        }

        if (rootParamName is null)
        {
            var candidates = new List<string>();
            foreach (var (name, _) in snapshot.Parameters)
            {
                if (!string.IsNullOrEmpty(name) &&
                    !string.Equals(name, paramName, StringComparison.OrdinalIgnoreCase))
                    candidates.Add(name!);
            }
            candidates.Sort((a, b) => b.Length.CompareTo(a.Length));

            SmartConLogger.Debug($"  Searching rootParam in formula '{formulaNonNull}' (candidates: {candidates.Count}):");

            foreach (var name in candidates)
            {
                if (!SmartCon.Core.Services.FormulaParamMatcher.ContainsParamReference(formulaNonNull, name)) continue;
                rootParamName = name;
                rootIsInst = snapshot.IsInstanceByName.TryGetValue(name, out bool nameInst) ? nameInst : directIsInst;
                SmartConLogger.Debug($"    → rootParam='{rootParamName}', isInstance={rootIsInst}");
                break;
            }

            if (rootParamName is null)
                SmartConLogger.Debug("    → rootParam not found");
        }

        SmartConLogger.Debug($"  → return ('{paramName}', '{rootParamName}', '{formulaNonNull}', {rootIsInst}, isDiameter={isDiameter})");
        return (paramName, rootParamName, formulaNonNull, rootIsInst, isDiameter);
    }

    /// <summary>Find parameter in snapshot (case-insensitive): (formula, isInstance, found).</summary>
    private static (string? Formula, bool IsInstance, bool Found) FindInSnapshot(
        FamilyParameterSnapshot snapshot, string name)
    {
        foreach (var (pName, pFormula) in snapshot.Parameters)
        {
            if (string.Equals(pName, name, StringComparison.OrdinalIgnoreCase))
            {
                var isInst = pName is not null
                    && snapshot.IsInstanceByName.TryGetValue(pName, out bool pInst)
                    && pInst;
                return (pFormula, isInst, true);
            }
        }
        return (null, false, false);
    }

    private static FamilyParameter? FindFamilyParameter(Autodesk.Revit.DB.FamilyManager fm, string name)
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
