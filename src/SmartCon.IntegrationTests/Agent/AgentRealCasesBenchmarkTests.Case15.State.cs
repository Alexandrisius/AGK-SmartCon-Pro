using System.Globalization;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools.Read;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Seed verification and the post-run state read-back of case 15: the truth
/// accessors used by both the verification and the verdict — valve connector
/// neighbours recomputed via ConnectorManager, system names read back after
/// the commit-merge, the chain trace through the real tool (chain length +
/// break pairs) and the office contents grouped the same way
/// get_room_contents groups them. Partial of
/// <see cref="AgentRealCasesBenchmarkTests"/>.
/// </summary>
public sealed partial class AgentRealCasesBenchmarkTests
{
    private sealed record Case15Break(long From, long To);

    private void VerifyCase15Seed()
    {
        var keys = new (string Label, long Id)[]
        {
            ("окно О1", _case15.WindowId),
            ("дверь Д1", _case15.DoorId),
            ("насос Н1", _case15.PumpId),
            ("кран К1", _case15.ValveId),
            ("труба A", _case15.PipeAId),
            ("труба B", _case15.PipeBId),
            ("труба B2", _case15.PipeB2Id),
            ("отвод Ф1", _case15.ElbowId),
            ("труба C", _case15.PipeCId),
            ("труба C2", _case15.PipeC2Id),
            ("южная стена", _case15.SouthWallId),
            ("Офис 1", _case15.OfficeRoomId),
            ("Коридор", _case15.CorridorRoomId)
        };
        foreach (var (label, id) in keys)
        {
            if (id == 0 || Doc.GetElement(ElementIdCompat.Create(id)) is null)
            {
                Skip.Test("Элемент «" + label + "» не засидирован — кейс 15 невозможен");
                return;
            }
        }

        if (Doc.GetElement(ElementIdCompat.Create(_case15.WindowId)) is not FamilyInstance window ||
            window.Host is null || window.Host.Id.GetValue() != _case15.SouthWallId)
        {
            Skip.Test("Окно О1 не в южной стене — сид кейса 15 некорректен");
            return;
        }

        long fromId = 0;
        long toId = 0;
        if (Doc.GetElement(ElementIdCompat.Create(_case15.DoorId)) is FamilyInstance door)
        {
            try
            {
                fromId = door.FromRoom?.Id.GetValue() ?? 0;
                toId = door.ToRoom?.Id.GetValue() ?? 0;
            }
            catch (InvalidOperationException)
            {
                // До 2024 фазы могут бросать — стороны не вычислены.
            }
        }

        var sides = new HashSet<long> { fromId, toId };
        if (fromId == toId || !sides.Contains(_case15.OfficeRoomId) || !sides.Contains(_case15.CorridorRoomId))
        {
            Skip.Test("Дверь Д1 без from/to по «Офис 1»/«Коридор» (from " +
                      fromId + ", to " + toId + ")");
            return;
        }

        var neighbours = Case15ValveNeighbours();
        if (!neighbours.Contains(_case15.PipeAId) || !neighbours.Contains(_case15.PipeBId))
        {
            Skip.Test("Кран К1 соединён не с обеими трубами (соседи: " +
                      string.Join(", ", neighbours) + ")");
            return;
        }

        // C10-06: марки сида уникальны (на базе реального шаблона встречались
        // CenterLine-элементы с той же маркой — вопрос 1 двусмысленный).
        foreach (var mark in new[] { "К1", "О1", "Д1", "Н1" })
        {
            var holders = Case15MarkHolders(Doc, mark);
            if (holders.Count > 1)
            {
                Skip.Test("Сид кейса 15: марка «" + mark + "» не уникальна (" +
                          holders.Count + " носителей) — вопрос кейса двусмысленен");
                return;
            }
        }

        _case15.MainSystemName = Case15SystemNameOf(_case15.PipeB2Id);
        _case15.SplitSystemName = Case15SystemNameOf(_case15.PipeCId);
        if (string.IsNullOrWhiteSpace(_case15.MainSystemName) ||
            string.IsNullOrWhiteSpace(_case15.SplitSystemName) ||
            string.Equals(_case15.MainSystemName, _case15.SplitSystemName, StringComparison.Ordinal))
        {
            Skip.Test("Системы после commit не разделились («" + _case15.MainSystemName +
                      "» / «" + _case15.SplitSystemName + "»)");
            return;
        }

        if (!string.Equals(_case15.MainSystemName, Case15SystemMain, StringComparison.Ordinal) ||
            !string.Equals(_case15.SplitSystemName, Case15SystemSplit, StringComparison.Ordinal))
        {
            Skip.Test("Переименование систем в «Т11»/«Т111» не удалось (фактически: «" +
                      _case15.MainSystemName + "» / «" + _case15.SplitSystemName + "»)");
            return;
        }

        var breaks = Case15TraceBreaks(out var chainLength);
        var expectedPair = new HashSet<long> { _case15.PipeB2Id, _case15.PipeCId };
        if (chainLength < 4 || breaks.Count != 1 ||
            !expectedPair.Contains(breaks[0].From) || !expectedPair.Contains(breaks[0].To))
        {
            Skip.Test("Обход цепи не находит засеянный разрыв (chain " + chainLength +
                      ", breaks " + breaks.Count + ")");
            return;
        }

        _case15OfficeContents = Case15ReadOfficeContents();
        if (_case15OfficeContents.Count < 2)
        {
            Skip.Test("Состав «Офис 1» беднее ожидаемого (" + _case15OfficeContents.Count +
                      " семейств) — вопрос 4 не имеет истины");
        }
    }

    // ── Истина для вердикта ─────────────────────────────────────────────

    /// <summary>Соседи крана по ConnectorManager (AllRefs → Owner, без MEPSystem/Curve).</summary>
    private List<long> Case15ValveNeighbours()
    {
        var result = new List<long>();
        if (Doc.GetElement(ElementIdCompat.Create(_case15.ValveId)) is not FamilyInstance valve)
            return result;

        var manager = valve.MEPModel?.ConnectorManager;
        if (manager is null)
            return result;

        foreach (Connector connector in manager.Connectors)
        {
            if (connector.ConnectorType == ConnectorType.Curve || !connector.IsConnected) // I-08
                continue;
            foreach (Connector reference in connector.AllRefs)
            {
                var owner = reference.Owner;
                if (owner is null || owner is MEPSystem)
                    continue;
                if (owner.Id.GetValue() == _case15.ValveId)
                    continue;
                if (reference.ConnectorType == ConnectorType.Curve)
                    continue;

                var id = owner.Id.GetValue();
                if (!result.Contains(id))
                    result.Add(id);
            }
        }

        return result;
    }

    private string? Case15SystemNameOf(long elementId) =>
        Doc.GetElement(ElementIdCompat.Create(elementId))
            ?.get_Parameter(BuiltInParameter.RBS_SYSTEM_NAME_PARAM)?.AsString();

    /// <summary>Обход цепи самим тулом: длина цепи + пары разрывов (id).</summary>
    private List<Case15Break> Case15TraceBreaks(out int chainLength)
    {
        chainLength = 0;
        var breaks = new List<Case15Break>();
        var tool = new TraceSystemChainTool(new StubRevitContext(Doc));
        var args = Case15Parse("{\"elementId\":" +
                               _case15.PipeAId.ToString(CultureInfo.InvariantCulture) + "}");
        var result = tool.ExecuteForTests(Doc, args, CancellationToken.None);
        if (!result.Success || result.Data is null)
            return breaks;

        var data = result.Data.Value;
        if (data.TryGetProperty("chainLength", out var length) && length.ValueKind == JsonValueKind.Number)
            chainLength = length.GetInt32();
        if (!data.TryGetProperty("breaks", out var array) || array.ValueKind != JsonValueKind.Array)
            return breaks;

        foreach (var item in array.EnumerateArray())
        {
            breaks.Add(new Case15Break(
                item.GetProperty("fromElementId").GetInt64(),
                item.GetProperty("toElementId").GetInt64()));
        }

        return breaks;
    }

    /// <summary>Состав «Офис 1» (семейство → количество) — тем же тулом, что у модели.</summary>
    private Dictionary<string, int> Case15ReadOfficeContents()
    {
        var result = new Dictionary<string, int>();
        var tool = new GetRoomContentsTool(new StubRevitContext(Doc));
        var args = Case15Parse("{\"roomName\":\"" + Case15OfficeName + "\"}");
        var run = tool.ExecuteForTests(Doc, args, CancellationToken.None);
        if (!run.Success || run.Data is null)
            return result;

        var data = run.Data.Value;
        if (!data.TryGetProperty("contents", out var contents) || contents.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var group in contents.EnumerateArray())
        {
            var family = group.GetProperty("family").GetString();
            if (string.IsNullOrEmpty(family))
                continue;

            result.TryGetValue(family!, out var current);
            result[family!] = current + group.GetProperty("count").GetInt32();
        }

        return result;
    }

    private static JsonElement Case15Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    // ── State read-back ─────────────────────────────────────────────────

    private string DumpCase15State()
    {
        var builder = new StringBuilder();
        builder.AppendLine("| Элемент | Id | Связь / истина |");
        builder.AppendLine("|---|---|---|");
        builder.AppendLine("| Кран К1 | " + _case15.ValveId + " | соседи: " +
                          string.Join(", ", Case15ValveNeighbours()) + " |");
        builder.AppendLine("| Окно О1 | " + _case15.WindowId + " | стена " +
                          _case15.SouthWallId + " |");
        builder.AppendLine("| Дверь Д1 | " + _case15.DoorId + " | из/в: " +
                          _case15.OfficeRoomId + " / " + _case15.CorridorRoomId + " |");
        builder.AppendLine("| Насос Н1 | " + _case15.PumpId + " | уровень «" +
                          Case15LevelName + "» (id " + _case15.Level1Id + ") |");
        builder.AppendLine("| Отвод Ф1 | " + _case15.ElbowId + " | система «" +
                          _case15.MainSystemName + "» |");
        builder.AppendLine("| Труба B2 | " + _case15.PipeB2Id + " | сторона разрыва, система «" +
                          _case15.MainSystemName + "» |");
        builder.AppendLine("| Труба C | " + _case15.PipeCId + " | сторона разрыва, система «" +
                          _case15.SplitSystemName + "» |");
        builder.AppendLine();
        builder.AppendLine("Разрыв: между " + _case15.PipeB2Id + " и " + _case15.PipeCId +
                          " (зазор 7 мм), системы «" + _case15.MainSystemName + "» / «" +
                          _case15.SplitSystemName + "».");
        builder.AppendLine("Состав «Офис 1» (истина): " + string.Join("; ",
            _case15OfficeContents.Select(pair => pair.Key + " ×" + pair.Value)));
        return builder.ToString();
    }
}
