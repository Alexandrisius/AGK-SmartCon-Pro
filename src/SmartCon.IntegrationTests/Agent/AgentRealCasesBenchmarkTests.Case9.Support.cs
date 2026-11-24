using System.IO;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Structure;
using SmartCon.Core.Compatibility;
using SmartCon.IntegrationTests.Agent.Support;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Seed support and the post-run truth of case 9: the nested shared flanges
/// (2 pump hosts, best-effort — the file-load mechanics proven in
/// AgentSystemFillJobTests; honest degradation with a probe note when the
/// test host does not materialize them), the seed verification (5 systems,
/// partially filled parameter, single 3D template with the «Фильтры» section
/// detached), the system member/parameter locators used by both the
/// verification and the verdict, and the state read-back. Partial of
/// <see cref="AgentRealCasesBenchmarkTests"/>.
/// </summary>
public sealed partial class AgentRealCasesBenchmarkTests
{
    private static string[] Case9AllSystemNames()
    {
        var names = new List<string>(Case9CleanSystems) { Case9SuspectNear, Case9SuspectFar };
        return names.ToArray();
    }

    /// <summary>
    /// Имя системы упомянуто в тексте ЦЕЛЫМ токеном (P3-4 рецензии r1: «Т11» —
    /// НЕ подстрока «Т111»; упоминание одного «Т111» пару не закрывает).
    /// Единое правило для кириллицы и латиницы: латинские омоглифы (T→Т и др.)
    /// нормализуются до кириллицы до сравнения — «T11» считается упоминанием
    /// «Т11». Границы: не буква/цифра; точка — граница, только если за ней НЕ
    /// цифра (иначе это продолжение цифрового суффикса «Т1.11»).
    /// </summary>
    internal static bool Case9MentionsName(string text, string name)
    {
        var canonicalText = Case9CanonicalLookalikes(text);
        var canonicalName = Case9CanonicalLookalikes(name);
        var index = canonicalText.IndexOf(canonicalName, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            if (Case9IsNameBoundary(canonicalText, index + canonicalName.Length) &&
                (index == 0 || !char.IsLetterOrDigit(canonicalText[index - 1])))
                return true;

            index = canonicalText.IndexOf(
                canonicalName, index + 1, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    /// <summary>
    /// Латинские омоглифы кириллицы нормализуются до кириллицы: сравнение имён
    /// систем единообразно для обоих алфавитов (модель может транскрибировать
    /// «Т11» латиницей «T11» — смысл тот же).
    /// </summary>
    private static string Case9CanonicalLookalikes(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            builder.Append(character switch
            {
                'A' => 'А', 'B' => 'В', 'C' => 'С', 'E' => 'Е', 'H' => 'Н',
                'K' => 'К', 'M' => 'М', 'O' => 'О', 'P' => 'Р', 'T' => 'Т',
                'X' => 'Х', 'a' => 'а', 'c' => 'с', 'e' => 'е', 'o' => 'о',
                'p' => 'р', 'x' => 'х', 'y' => 'у',
                _ => character
            });
        }

        return builder.ToString();
    }

    /// <summary>
    /// Сколько РАЗНЫХ имён систем перечислено в промежуточном ответе к утверждению
    /// (C4-05): имя засчитывается только на границе — не внутри более длинного
    /// («Т1» внутри «Т11»/«Т1.11» не считается), правило то же, что и для
    /// упоминаний подозрительной пары (<see cref="Case9MentionsName"/>).
    /// </summary>
    internal static int Case9SystemsListed(string text)
    {
        var listed = 0;
        foreach (var name in Case9AllSystemNames())
        {
            if (Case9MentionsName(text, name))
                listed++;
        }

        return listed;
    }

    private static bool Case9IsNameBoundary(string text, int position)
    {
        if (position >= text.Length)
            return true;

        var next = text[position];
        if (char.IsLetterOrDigit(next))
            return false;

        // «.» — граница, только если за ней не цифра («Т1.11» = продолжение).
        if (next == '.' && position + 1 < text.Length && char.IsDigit(text[position + 1]))
            return false;

        return true;
    }

    private static void Case9Rename(Element element, string name)
    {
        try
        {
            element.Name = name;
        }
        catch (ArgumentException)
        {
            // Отказ переименования (тёзка) не валит сид: VerifyCase9Seed
            // сообщит о фактическом имени системы.
        }
    }

    /// <summary>Best-effort удаление temp-файла (занят — остаётся во %TEMP%).</summary>
    private static void Case9TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
    }

    /// <summary>
    /// Вложенный shared-фланец: общая механика сида кейса 4 —
    /// <see cref="NestedSharedFamilySeed.TrySeed"/> (файловая загрузка хозяина
    /// в проект ВНУТРИ транзакции, C4-04). Вызывать ВНЕ транзакции проекта.
    /// </summary>
    private bool Case9TrySeedFlangeHosts(out string? note)
    {
        var result = NestedSharedFamilySeed.TrySeed(
            Application, Doc, Case9HostFamilyName,
            Path.Combine(Path.GetTempPath(), "SmartConCase9Seeds"), _case9TempFamilyPaths);
        note = result.Note;
        if (result.Seeded)
            _case9HostSymbolId = result.HostSymbolId;
        return result.Seeded;
    }

    /// <summary>2 насоса (Н1/Н2) с вложенными фланцами; фланцы = GetSubComponentIds.</summary>
    private void Case9PlacePumps(Document doc)
    {
        if (!_case9FlangesSeeded || _case9HostSymbolId == 0)
            return;

        if (doc.GetElement(ElementIdCompat.Create(_case9HostSymbolId)) is not FamilySymbol symbol)
        {
            _case9FlangesSeeded = false;
            _case9FlangeNote = "Типоразмер хозяина не найден в проекте";
            return;
        }

        symbol.Activate();
        var marks = new[] { "Н1", "Н2" };
        var offsets = new[] { 5.0, 35.0 };
        for (var index = 0; index < 2; index++)
        {
            var pump = doc.Create.NewFamilyInstance(
                new XYZ(offsets[index], 14, 0), symbol, StructuralType.NonStructural);
            pump.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set(marks[index]);
            _case9PumpIds.Add(pump.Id.GetValue());
        }

        // Субкомпоненты вложенного (shared) семейства материализуются при
        // регенерации: сразу после NewFamilyInstance внутри той же транзакции
        // GetSubComponentIds() возвращает пусто (C4-04).
        doc.Regenerate();
        foreach (var pumpId in _case9PumpIds)
        {
            if (doc.GetElement(ElementIdCompat.Create(pumpId)) is not FamilyInstance pump)
                continue;

            foreach (var subId in pump.GetSubComponentIds())
                _case9FlangeIds.Add(subId.GetValue());
        }

        if (_case9FlangeIds.Count == 0)
        {
            _case9FlangesSeeded = false;
            _case9FlangeNote = "У насосов нет субкомпонентов после размещения";
        }
    }

    // ── Верификация сида и истина для вердикта ──────────────────────────

    private void VerifyCase9Seed()
    {
        foreach (var name in Case9AllSystemNames())
        {
            var system = Case9FindSystem(name);
            if (system is null)
            {
                Skip.Test("Система «" + name + "» не засидирована — кейс 9 невозможен");
                return;
            }

            _case9Systems[name].MemberIds.AddRange(Case9MemberIdsOf(system));
            if (_case9Systems[name].MemberIds.Count < 2)
            {
                Skip.Test("Система «" + name + "» беднее ожидаемого (" +
                          _case9Systems[name].MemberIds.Count + " членов)");
                return;
            }
        }

        var cleanIds = Case9CleanSystems
            .Select(name => Case9FindSystem(name)?.Id.GetValue() ?? 0)
            .ToList();
        if (cleanIds.Distinct().Count() != Case9CleanSystems.Length)
        {
            Skip.Test("Чистые системы слились в одну — сид кейса 9 некорректен");
            return;
        }

        if (_case9ParamElementId == 0 ||
            SharedParameterElement.Lookup(Doc, Case9ParamGuid) is null)
        {
            Skip.Test("Общий параметр «" + Case9ParamName + "» не создан сидом");
            return;
        }

        var pipes = Case9CleanSystems.SelectMany(name => _case9Systems[name].PipeIds).ToList();
        var pipesFilled = pipes.Count(id => !string.IsNullOrEmpty(Case9ParamValueOf(id)));
        if (pipes.Count < 3 || pipesFilled != pipes.Count)
        {
            Skip.Test("Частичное заполнение не вышло: труб " + pipes.Count +
                      ", заполнено " + pipesFilled + " (нужно все трубы чистых систем)");
            return;
        }

        var template = Doc.GetElement(ElementIdCompat.Create(_case9TemplateId)) as View;
        if (template is null || !template.IsTemplate ||
            template.ViewType != ViewType.ThreeD || !Case9TemplateFiltersDetached(template))
        {
            Skip.Test("3D-шаблон с неуправляемой секцией «Фильтры» не создан — кейс 9 невозможен");
        }
    }

    private PipingSystem? Case9FindSystem(string name)
    {
        foreach (var element in new FilteredElementCollector(Doc).OfClass(typeof(PipingSystem)))
        {
            if (element is PipingSystem system &&
                string.Equals(system.Name ?? string.Empty, name, StringComparison.Ordinal))
                return system;
        }

        return null;
    }

    /// <summary>Члены системы тем же способом, что и selector джобы (post-commit).</summary>
    private static List<long> Case9MemberIdsOf(PipingSystem system)
    {
        var ids = new HashSet<long>();
        if (system.Elements is not null)
            foreach (var member in system.Elements)
                if (member is Element element)
                    ids.Add(element.Id.GetValue());
        if (system.BaseEquipment is { } equipment)
            ids.Add(equipment.Id.GetValue());
        if (system.PipingNetwork is not null)
            foreach (var member in system.PipingNetwork)
                if (member is Element networkElement)
                    ids.Add(networkElement.Id.GetValue());
        return ids.ToList();
    }

    private static bool Case9TemplateFiltersDetached(View template)
    {
        var filtersValue = new ElementId(BuiltInParameter.VIS_GRAPHICS_FILTERS).GetValue();
        foreach (var id in template.GetNonControlledTemplateParameterIds())
            if (id.GetValue() == filtersValue)
                return true;
        return false;
    }

    private string? Case9ParamValueOf(long elementId) =>
        Doc.GetElement(ElementIdCompat.Create(elementId))
            ?.get_Parameter(Case9ParamGuid)?.AsString();

    /// <summary>Виды с «Т11» в имени: «Т111» содержит «Т11», «Т1.11» — нет (точка).</summary>
    private List<string> Case9SuspiciousViewNames()
    {
        var result = new List<string>();
        foreach (var view in new FilteredElementCollector(Doc)
                     .OfClass(typeof(View)).Cast<View>().Where(view => !view.IsTemplate))
        {
            if (view.Name.IndexOf(Case9SuspectNear, StringComparison.OrdinalIgnoreCase) >= 0)
                result.Add(view.Name);
        }

        return result;
    }

    // ── State read-back ─────────────────────────────────────────────────

    private string DumpCase9State()
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("| Система | Члены | «" + Case9ParamName + "» заполнено | 3D-вид |");
        builder.AppendLine("|---|---|---|---|");
        foreach (var name in Case9AllSystemNames())
        {
            var members = _case9Systems[name].MemberIds;
            var filled = members.Count(id => Case9ParamValueOf(id) == name);
            var view = Case9FindView3D(name);
            var viewText = view is null
                ? "нет"
                : "id " + view.Id.GetValue() + ", шаблон " +
                  (view.ViewTemplateId.GetValue() == _case9TemplateId ? "да" : "НЕТ") +
                  ", фильтр " + (Case9ViewHasSystemFilter(view, name) ? "да" : "НЕТ");
            builder.AppendLine("| " + name + " | " + members.Count + " | " + filled + "/" +
                              members.Count + " | " + viewText + " |");
        }

        if (_case9FlangesSeeded)
        {
            var pumpsFilled = _case9PumpIds.Count(id => !string.IsNullOrEmpty(Case9ParamValueOf(id)));
            var flangesFilled = _case9FlangeIds.Count(id => !string.IsNullOrEmpty(Case9ParamValueOf(id)));
            builder.AppendLine();
            builder.AppendLine("Насосы: " + _case9PumpIds.Count + ", с параметром " + pumpsFilled +
                              "; фланцы (вложенные shared): " + _case9FlangeIds.Count +
                              ", с параметром " + flangesFilled);
        }

        var suspiciousViews = Case9SuspiciousViewNames();
        builder.AppendLine();
        builder.AppendLine("Виды для подозрительных имён: " +
                          (suspiciousViews.Count == 0 ? "нет" : string.Join(", ", suspiciousViews)));
        return builder.ToString();
    }
}
