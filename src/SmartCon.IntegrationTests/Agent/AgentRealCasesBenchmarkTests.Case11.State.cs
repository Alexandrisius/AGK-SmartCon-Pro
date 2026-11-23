using System.Globalization;
using System.Text;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools.Read;
using SmartCon.Revit.Agent.Tools.Write;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Case 11 state block (v2 case 6): the seed summary for the transcript, the
/// post-run state dump (album + new sheets with viewport/filter/multiplicity
/// read-back) and the case toolset (sheet assembly: reads of the title block
/// inventory and placement preview, explication cloning/filtering/visibility,
/// the batch build_system_sheets composite). Partial of
/// <see cref="AgentRealCasesBenchmarkTests"/>.
/// </summary>
public sealed partial class AgentRealCasesBenchmarkTests
{
    private List<string> Case11SeedSummary()
    {
        var lines = new List<string>
        {
            "3 трубопроводные системы (наследие кейса 4: «АДСК_ИмяСистемы» заполнен всем элементам, " +
            "включая фитинги-отводы; у каждой — 3D-вид с именем системы):"
        };
        foreach (var system in _case11Systems)
        {
            lines.Add("  - «" + system.Name + "»: вид id " + system.ViewId + ", труб " +
                      system.PipeIds.Count + ", фитингов " + system.FittingIds.Count +
                      ", трасса " +
                      (system.RouteLengthFt * 0.3048).ToString("0.0", CultureInfo.InvariantCulture) +
                      " м");
        }

        lines.Add("общая спецификация «" + Case11MainScheduleName + "» (мультикатегорийная, " +
                  _case11ScheduleColumns + " колонок: Наименование/Артикул/Марка/Ед.изм./Количество/" +
                  "АДСК_ИмяСистемы + служебные) — шаблона экспликации в проекте НЕТ");
        lines.Add("альбом: листы 1–3 («Общие данные», «План 1 этажа», «План 2 этажа») — новые листы " +
                  "должны продолжить нумерацию (4, 5, 6)");
        lines.Add("титульный блок «" + _case11TitleBlockFamilyName + "» с редактируемым параметром «" +
                  Case11TitleblockMultiplicity + "» (Integer, instance); кратность альбомных листов = 1");
        return lines;
    }

    private string DumpCase11State()
    {
        var builder = new StringBuilder();
        builder.AppendLine("| Лист | Номер | Система | Viewport внутри | Экспликация | Кратность |");
        builder.AppendLine("|---|---|---|---|---|---|");
        foreach (var sheet in Case11AllSheets())
        {
            var system = Case11SystemOfText(sheet.Name) ?? "(альбом)";
            builder.AppendLine("| " + sheet.Name + " | " + sheet.SheetNumber + " | " + system +
                              " | " + (Case11ViewportInside(sheet, 1.0) ? "да" : "НЕТ") +
                              " | " + (Case11ScheduleOnSheet(sheet) is null ? "—" : "да") +
                              " | " + Case11MultiplicityOf(sheet) + " |");
        }

        builder.AppendLine();
        if (Doc.GetElement(ElementIdCompat.Create(_case11MainScheduleId)) is ViewSchedule schedule)
        {
            builder.AppendLine("Общая спецификация «" + schedule.Name + "»: " +
                               AgentScheduleReader.ReadRowCount(schedule, skipHeader: true) +
                               " строк, " +
                               AgentScheduleReader.ReadColumns(Doc, schedule, out _).Count +
                               " видимых колонок");
        }
        else
        {
            builder.AppendLine("Общая спецификация (id " + _case11MainScheduleId + ") исчезла");
        }

        return builder.ToString();
    }


    // ── Поддержка сида: определения ФОП, трассы, заполнение, верификация ──

    private Case11Definitions? SeedCase11Definitions()
    {
        try
        {
            var file = Application.OpenSharedParameterFile();
            if (file is null)
            {
                _case11SeedError = "Временный ФОП кейса 11 не открылся";
                return null;
            }

            var group = file.Groups.Create(Case11SharedGroup);

            ExternalDefinition Create(string name, Guid guid) =>
                (ExternalDefinition)group.Definitions.Create(
                    new ExternalDefinitionCreationOptions(name, SpecTypeId.String.Text)
                    {
                        GUID = guid
                    });

            return new Case11Definitions(
                Create("Наименование", Case11NameGuid),
                Create("Артикул", Case11ArticleGuid),
                Create("Марка", Case11MarkGuid),
                Create("Единица измерения", Case11UnitGuid),
                Create("Количество", Case11CountGuid),
                Create(Case11SystemParameterName, Case11SystemNameGuid));
        }
        catch (Exception ex)
        {
            _case11SeedError = "Не удалось засидировать определения ФОП кейса 11: " +
                               ex.GetType().Name + ": " + ex.Message;
            return null;
        }
    }

    /// <summary>Трассы систем (ft): Т1 — 60+25+35 = 120 ft (36.6 м), Т2 — 45 ft, Т1.11 — 32 ft.</summary>
    private static XYZ[][] Case11Routes() => new[]
    {
        new[] { new XYZ(0, 0, -2.0), new XYZ(60, 0, -2.0), new XYZ(60, 25, -2.0), new XYZ(95, 25, -2.0) },
        new[] { new XYZ(0, 40, -2.0), new XYZ(30, 40, -2.0), new XYZ(30, 55, -2.0) },
        new[] { new XYZ(0, 70, -2.0), new XYZ(20, 70, -2.0), new XYZ(20, 82, -2.0) }
    };

    private static void Case11FillElement(
        Document doc,
        Case11Definitions definitions,
        long elementId,
        string systemName,
        bool isFitting)
    {
        if (doc.GetElement(ElementIdCompat.Create(elementId)) is not { } element)
            return;

        element.get_Parameter(definitions.SystemName.GUID)?.Set(systemName);
        element.get_Parameter(definitions.Name.GUID)?.Set(isFitting ? "Отвод 90° Ду50" : "Труба стальная Ду50");
        element.get_Parameter(definitions.Article.GUID)?.Set(isFitting ? "АРТ-ОТВ-50" : "АРТ-ТР-50");
        element.get_Parameter(definitions.Mark.GUID)?.Set(isFitting ? "ОТВ" : "ТР");
        element.get_Parameter(definitions.Unit.GUID)?.Set(isFitting ? "шт" : "м");
        element.get_Parameter(definitions.Count.GUID)?.Set("1");
    }

    private void VerifyCase11Seed()
    {
        if (_case11Systems.Count != 3)
        {
            Skip.Test("Сид кейса 11 некорректен: систем " + _case11Systems.Count);
            return;
        }

        foreach (var system in _case11Systems)
        {
            if (system.ViewId <= 0)
            {
                Skip.Test("Сид кейса 11: у системы «" + system.Name + "» нет 3D-вида");
                return;
            }

            var unfilled = system.PipeIds.Concat(system.FittingIds)
                .Count(id => Doc.GetElement(ElementIdCompat.Create(id))
                    ?.get_Parameter(Case11SystemNameGuid)?.AsString() is not { Length: > 0 });
            if (unfilled > 0)
            {
                Skip.Test("Сид кейса 11: «АДСК_ИмяСистемы» не заполнен у " + unfilled +
                          " элементов системы «" + system.Name + "»");
                return;
            }
        }

        var schedule = Doc.GetElement(ElementIdCompat.Create(_case11MainScheduleId)) as ViewSchedule;
        if (schedule is null || _case11ScheduleColumns < 8 ||
            AgentScheduleReader.ReadRowCount(schedule, skipHeader: true) < 10)
        {
            Skip.Test("Сид кейса 11 некорректен: общая спецификация (колонок " +
                      _case11ScheduleColumns + ", строк " +
                      (schedule is null ? "?" : AgentScheduleReader.ReadRowCount(schedule, true)) + ")");
            return;
        }

        foreach (var album in _case11Album)
        {
            if (Doc.GetElement(ElementIdCompat.Create(album.Id)) is not ViewSheet sheet ||
                !string.Equals(sheet.SheetNumber, album.Number, StringComparison.Ordinal) ||
                Case11MultiplicityOf(sheet) != 1)
            {
                Skip.Test("Сид кейса 11 некорректен: альбомный лист " + album.Number);
                return;
            }
        }
    }
}
