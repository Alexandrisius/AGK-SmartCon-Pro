using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Structure;
using SmartCon.Core.Compatibility;
using SmartCon.IntegrationTests.Support;

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

    private static void Case9Rename(Element element, string name)
    {
        try
        {
            element.Name = name;
        }
        catch (ArgumentException ex)
        {
            Console.WriteLine("PROBE case 9: rename to '" + name + "' rejected: " + ex.Message);
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
    /// Вложенный shared-фланец: FAMILY_SHARED=1 → SaveAs → LoadFamily в
    /// семейство-хозяин (это и есть вложение) → хозяин в проект ФАЙЛОМ
    /// (open-doc load не материализует вложенный shared-инстанс как
    /// субкомпонент — механика AgentSystemFillJobTests). Вызывать ВНЕ
    /// транзакции проекта.
    /// </summary>
    private bool Case9TrySeedFlangeHosts(out string? note)
    {
        note = null;
        var template = SampleFiles.FindFamilyTemplate(Application);
        if (template is null)
        {
            note = "Нет family-шаблона (.rft) во встроенных шаблонах Revit";
            return false;
        }

        var directory = Path.Combine(
            Path.GetTempPath(), "SmartConCase9Seeds", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var flangePath = Path.Combine(directory, "Фланец.rfa");
        var hostPath = Path.Combine(directory, Case9HostFamilyName + ".rfa");
        _case9TempFamilyPaths.Add(flangePath);
        _case9TempFamilyPaths.Add(hostPath);

        Document? flangeDoc = null;
        Document? hostDoc = null;
        try
        {
            flangeDoc = Application.NewFamilyDocument(template);
            using (var transaction = new Transaction(flangeDoc, "Shared flange"))
            {
                transaction.Start();
                flangeDoc.OwnerFamily.get_Parameter(BuiltInParameter.FAMILY_SHARED)?.Set(1);
                if (flangeDoc.FamilyManager.CurrentType is null)
                    flangeDoc.FamilyManager.NewType("Тип1");
                if (transaction.Commit() != TransactionStatus.Committed)
                {
                    note = "Транзакция фланца не завершилась";
                    return false;
                }
            }

            flangeDoc.SaveAs(flangePath, new SaveAsOptions { OverwriteExistingFile = true });

            hostDoc = Application.NewFamilyDocument(template);
            using (var transaction = new Transaction(hostDoc, "Name host"))
            {
                transaction.Start();
                hostDoc.OwnerFamily.Name = Case9HostFamilyName;
                transaction.Commit();
            }

            var flangeFamily = flangeDoc.LoadFamily(hostDoc, new Case9NestLoadOptions());
            if (flangeFamily is null)
            {
                note = "Фланец не загрузился в семейство-хозяин";
                return false;
            }

            var symbolId = flangeFamily.GetFamilySymbolIds().FirstOrDefault();
            if (symbolId is null || hostDoc.GetElement(symbolId) is not FamilySymbol flangeSymbol)
            {
                note = "У фланца нет типоразмера в семействе-хозяине";
                return false;
            }

            using (var transaction = new Transaction(hostDoc, "Nest shared flange"))
            {
                transaction.Start();
                flangeSymbol.Activate();
                hostDoc.FamilyCreate.NewFamilyInstance(
                    XYZ.Zero, flangeSymbol, StructuralType.NonStructural);
                transaction.Commit();
            }

            hostDoc.SaveAs(hostPath, new SaveAsOptions { OverwriteExistingFile = true });
            hostDoc.Close(false);
            hostDoc = null;

            if (!Doc.LoadFamily(hostPath, new Case9NestLoadOptions(), out var hostFamily) ||
                hostFamily is null)
            {
                note = "Семейство-хозяин не загрузилось в проект из файла";
                return false;
            }

            var hostSymbolId = hostFamily.GetFamilySymbolIds().FirstOrDefault();
            if (hostSymbolId is null)
            {
                note = "У хозяина нет типоразмера в проекте";
                return false;
            }

            _case9HostSymbolId = hostSymbolId.GetValue();
            flangeDoc.Close(false);
            flangeDoc = null;
            return true;
        }
        catch (Exception ex)
        {
            note = "Сид вложенного семейства упал: " + ex.GetType().Name + ": " + ex.Message;
            return false;
        }
        finally
        {
            foreach (var open in new[] { flangeDoc, hostDoc })
            {
                if (open is null || !open.IsValidObject)
                    continue;

                try
                {
                    open.Close(false);
                }
                catch (Exception)
                {
                    // Уже закрыт или невалиден — ничего не делать.
                }
            }
        }
    }

    /// <summary>Nesting load of a shared family: FamilySource.Project keeps the
    /// shared behaviour (the nested instance is exposed as a subcomponent).</summary>
    private sealed class Case9NestLoadOptions : IFamilyLoadOptions
    {
        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
        {
            overwriteParameterValues = true;
            return true;
        }

        public bool OnSharedFamilyFound(
            Family sharedFamily,
            bool familyInUse,
            out FamilySource source,
            out bool overwriteParameterValues)
        {
            source = FamilySource.Project;
            overwriteParameterValues = true;
            return true;
        }
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
