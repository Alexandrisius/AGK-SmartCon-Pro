namespace SmartCon.Core.Models;

/// <summary>
/// Алгоритмы автоугадывания CTC для фитингов и reducer-ов.
/// Чистая логика без Revit API — тестируется unit-тестами.
/// </summary>
public static class CtcGuesser
{
    /// <summary>
    /// Проверить, могут ли два CTC соединяться напрямую по IsDirectConnect rules.
    /// </summary>
    public static bool CanDirectConnect(
        ConnectionTypeCode left, ConnectionTypeCode right,
        IReadOnlyList<FittingMappingRule> rules)
    {
        if (!left.IsDefined || !right.IsDefined) return false;

        foreach (var r in rules)
        {
            if (!r.IsDirectConnect) continue;
            if (r.FromType.Value == left.Value && r.ToType.Value == right.Value) return true;
            if (r.ToType.Value == left.Value && r.FromType.Value == right.Value) return true;
        }

        return false;
    }

    /// <summary>
    /// Найти CTC, который имеет прямой коннект (IsDirectConnect=true) с заданным CTC.
    /// Если найдено несколько — возвращает первый. Если не найдено — Undefined.
    /// </summary>
    public static ConnectionTypeCode FindDirectConnectCounterpart(
        ConnectionTypeCode ctc, IReadOnlyList<FittingMappingRule> rules)
    {
        if (!ctc.IsDefined) return ConnectionTypeCode.Undefined;

        foreach (var r in rules)
        {
            if (!r.IsDirectConnect) continue;
            if (r.FromType.Value == ctc.Value) return r.ToType;
            if (r.ToType.Value == ctc.Value) return r.FromType;
        }
        return ConnectionTypeCode.Undefined;
    }

    /// <summary>
    /// Автоугадывание пары CTC для адаптера/фитинга.
    /// Возвращает (counterpartForStatic, counterpartForDynamic).
    /// </summary>
    public static (ConnectionTypeCode ForStatic, ConnectionTypeCode ForDynamic) GuessAdapterCtc(
        ConnectionTypeCode staticCTC, ConnectionTypeCode dynamicCTC,
        IReadOnlyList<FittingMappingRule> rules)
    {
        // Ищем counterpart для каждой стороны через IsDirectConnect rules.
        // ВР↔ВР → counterpart(ВР)=НР → фитинг: НР-НР (ниппель)
        // Сварка↔Сварка → counterpart(Сварка)=Сварка → фитинг: Сварка-Сварка
        // ВР↔Сварка → counterpart(ВР)=НР, counterpart(Сварка)=Сварка → фитинг: НР-Сварка
        var forStatic = FindDirectConnectCounterpart(staticCTC, rules);
        var forDynamic = FindDirectConnectCounterpart(dynamicCTC, rules);

        // Fallback: если counterpart не найден — сам CTC
        if (!forStatic.IsDefined) forStatic = staticCTC;
        if (!forDynamic.IsDefined) forDynamic = dynamicCTC;

        return (forStatic, forDynamic);
    }

    /// <summary>
    /// Автоугадывание пары CTC для reducer-а.
    /// Same-type → оба = тот же CTC.
    /// Cross-type → реверс (conn к static = dynamicCTC, conn к dynamic = staticCTC).
    /// </summary>
    public static (ConnectionTypeCode ForStaticSide, ConnectionTypeCode ForDynamicSide) GuessReducerCtc(
        ConnectionTypeCode staticCTC, ConnectionTypeCode dynamicCTC,
        IReadOnlyList<FittingMappingRule> rules)
    {
        var forStatic = FindDirectConnectCounterpart(staticCTC, rules);
        var forDynamic = FindDirectConnectCounterpart(dynamicCTC, rules);

        if (forStatic.IsDefined && forDynamic.IsDefined)
            return (forStatic, forDynamic);

        if (forStatic.IsDefined)
        {
            forDynamic = staticCTC;
            return (forStatic, forDynamic);
        }

        if (forDynamic.IsDefined)
        {
            forStatic = dynamicCTC;
            return (forStatic, forDynamic);
        }

        return (dynamicCTC, staticCTC);
    }
}
