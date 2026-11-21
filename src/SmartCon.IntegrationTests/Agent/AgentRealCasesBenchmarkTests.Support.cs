using System.IO;
using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Revit.Agent.Tools.Read;
using SmartCon.Revit.Agent.Tools.Write;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Shared helpers of the real-cases benchmark partials: call-argument JSON
/// parsing (dryRun flag, data.changed size) and the case 1 view/folder
/// enumeration used by both the seed verification and the verdict.
/// Partial of <see cref="AgentRealCasesBenchmarkTests"/>.
/// </summary>
public sealed partial class AgentRealCasesBenchmarkTests
{
    /// <summary>dryRun из аргументов вызова (default true).</summary>
    private static bool IsDryRunCall(BenchCall call)
    {
        try
        {
            using var document = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            return !document.RootElement.TryGetProperty("dryRun", out var value) ||
                   value.ValueKind != JsonValueKind.False;
        }
        catch (JsonException)
        {
            return true;
        }
    }

    /// <summary>Число элементов в data.changed[] результата.</summary>
    private static int DataChangedCount(string resultJson)
    {
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            return document.RootElement.TryGetProperty("data", out var data) &&
                   data.TryGetProperty("changed", out var changed) &&
                   changed.ValueKind == JsonValueKind.Array
                ? changed.GetArrayLength()
                : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    /// <summary>Текущая папка вида = значение параметра группировки организации.</summary>
    private static string Case1FolderOf(Document doc, BrowserOrganization organization, View view)
    {
        if (!SetBrowserFolderTool.TryGetGroupingParameter(
                doc, organization, view, out var parameter, out _))
            return "(не резолвится)";
        return parameter.AsString() ?? string.Empty;
    }

    /// <summary>
    /// Все виды, которые видит list_views и которые должны лечь в папки разделов:
    /// supported + не шаблон + не спецификация + не легенда.
    /// </summary>
    private List<View> Case1OrganizableViews()
    {
        return new FilteredElementCollector(Doc)
            .OfClass(typeof(View))
            .Cast<View>()
            .Where(view => ListViewsTool.IsSupportedView(view)
                           && !view.IsTemplate
                           && view.ViewType != ViewType.Schedule
                           && view.ViewType != ViewType.Legend)
            .ToList();
    }

    /// <summary>
    /// Записываем ли параметр группировки текущей организации браузера видов:
    /// дефолтный шаблон машины группирует по read-only свойству, и кейс 1 тогда
    /// не сеется — сид переключается на русский шаблон с организацией по параметру.
    /// </summary>
    /// <summary>
    /// Открывает документ для сида кейса 1: дефолтный шаблон, а при read-only
    /// группировке — первый шаблон из кандидатов с записываемым параметром
    /// группировки. Если таких нет — Skip.Test с инструкцией (подготовить
    /// шаблон с организацией по параметру и указать его через
    /// SMARTCON_CASE1_TEMPLATE или TestContent/Case1BrowserOrg.rte).
    /// </summary>
    private void OpenCase1DocumentOrSkip()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
        if (Case1OrganizationWritable())
            return;

        var version = Doc.Application.VersionNumber;
        _document.Close(false);
        var tried = new List<string>();
        foreach (var template in Case1TemplateCandidates(version))
        {
            if (!File.Exists(template))
            {
                tried.Add(Path.GetFileName(template) + ": нет файла");
                continue;
            }

            string? openError = null;
            try
            {
                _document = Application.NewProjectDocument(template);
            }
            catch (Exception ex)
            {
                _document = null;
                openError = ex.GetType().Name;
            }

            var diagnostic = string.Empty;
            var writable = _document is not null && Case1OrganizationWritable(out diagnostic);
            tried.Add(Path.GetFileName(template) + ": " +
                      (_document is null
                          ? "не открылся" + (openError is null ? string.Empty : " (" + openError + ")")
                          : writable ? "ok" : diagnostic));
            if (writable)
                return;

            _document?.Close(false);
            _document = null;
        }

        Skip.Test("Ни один доступный шаблон не даёт записываемый параметр группировки " +
                  "браузера: API не умеет создавать/переключать организацию, а складские " +
                  "шаблоны группируют по read-only «Family and Type». Подготовь шаблон с " +
                  "организацией по параметру (например «Раздел»: Manage → Browser " +
                  "Organization, group by параметру, current) и укажи его в " +
                  "SMARTCON_CASE1_TEMPLATE или положи как TestContent/Case1BrowserOrg.rte " +
                  "в корень репозитория. Пробовано: " + string.Join("; ", tried));
    }

    private bool Case1OrganizationWritable()
    {
        return Case1OrganizationWritable(out _);
    }

    private bool Case1OrganizationWritable(out string diagnostic)
    {
        diagnostic = string.Empty;
        BrowserOrganization? organization = null;
        try
        {
            organization = BrowserOrganization.GetCurrentBrowserOrganizationForViews(Doc);
        }
        catch (Exception ex)
        {
            diagnostic = "организация не читается: " + ex.GetType().Name;
            return false;
        }

        if (organization is null)
        {
            diagnostic = "нет текущей организации";
            return false;
        }

        var probe = new FilteredElementCollector(Doc)
            .OfClass(typeof(View))
            .Cast<View>()
            .FirstOrDefault(view => ListViewsTool.IsSupportedView(view) && !view.IsTemplate);
        if (probe is null)
        {
            diagnostic = "нет пробного вида";
            return false;
        }

        // Все сохранённые организации: вдруг среди них есть параметрическая
        // (не текущая) — тогда подготовленный шаблон имеет смысл.
        foreach (var element in new FilteredElementCollector(Doc)
                     .OfClass(typeof(BrowserOrganization)))
        {
            if (element is not BrowserOrganization saved)
                continue;
            try
            {
                if (SetBrowserFolderTool.TryGetGroupingParameter(
                        Doc, saved, probe, out var savedParameter, out _) &&
                    !savedParameter.IsReadOnly)
                {
                    diagnostic += " [сохранённая org «" + saved.Name + "»: лист «" +
                                  (savedParameter.Definition?.Name ?? "?") + "» записываемый]";
                }
            }
            catch (Exception)
            {
                // Чужая организация может не группировать этот вид — не важно.
            }
        }

        if (!SetBrowserFolderTool.TryGetGroupingParameter(
                Doc, organization, probe, out var parameter, out var reason))
        {
            diagnostic = "org «" + organization.Name + "»: параметр листа не резолвится (" +
                         (reason ?? "?") + ")";
            return false;
        }

        var definition = parameter.Definition;
        diagnostic = "org «" + organization.Name + "»: лист = «" +
                     (definition?.Name ?? ("id " + parameter.Id.GetValue())) +
                     "» (id " + parameter.Id.GetValue() + "), read-only " + parameter.IsReadOnly;
        return !parameter.IsReadOnly;
    }

    /// <summary>
    /// Шаблоны с организацией браузера по параметру (по убыванию приоритета):
    /// явная переменная окружения, репо-шаблон, затем складские шаблоны
    /// (живой прогон 2026-11-21 показал: все складские группируют по read-only
    /// «Family and Type» — параметрические организации бывают только в офисных
    /// шаблонах, API создать/переключить организацию не может).
    /// </summary>
    private static List<string> Case1TemplateCandidates(string version)
    {
        var candidates = new List<string>();
        var explicitTemplate = Environment.GetEnvironmentVariable("SMARTCON_CASE1_TEMPLATE");
        if (!string.IsNullOrWhiteSpace(explicitTemplate))
            candidates.Add(explicitTemplate!);

        var repository = AppContext.BaseDirectory;
        for (var depth = 0; depth < 6 && repository is not null && repository.Length > 3; depth++)
        {
            var content = Path.Combine(repository, "TestContent", "Case1BrowserOrg.rte");
            if (File.Exists(content))
            {
                candidates.Add(content);
                break;
            }

            repository = Path.GetDirectoryName(repository);
        }

        var root = Path.Combine(@"C:\ProgramData\Autodesk\RVT " + version, "Templates");
        candidates.Add(Path.Combine(root, "Russian", "DefaultRUSRUS.rte"));
        candidates.Add(Path.Combine(root, "Russian", "Construction-DefaultRUSRUS.rte"));
        candidates.Add(Path.Combine(root, "Russian", "Systems-DefaultRUSRUS.rte"));
        candidates.Add(Path.Combine(root, "Default_M_RUS.rte"));
        candidates.Add(Path.Combine(root, "Default_M_DEU_ADSK.rte"));
        // Соседние установленные версии: вдруг их складские шаблоны параметрические.
        foreach (var other in new[] { "2023", "2024", "2026", "2027" })
        {
            if (other == version)
                continue;
            candidates.Add(Path.Combine(
                @"C:\ProgramData\Autodesk\RVT " + other, "Templates", "Russian",
                "DefaultRUSRUS.rte"));
        }

        return candidates;
    }

    /// <summary>Спецификации и легенды проекта («не трогать» из промпта кейса 1).</summary>
    private List<(string Name, long Id, string Kind)> EnumerateCase1Untouchables()
    {
        var result = new List<(string, long, string)>();
        foreach (var view in new FilteredElementCollector(Doc)
                     .OfClass(typeof(View))
                     .Cast<View>()
                     .Where(view => !view.IsTemplate))
        {
            if (view is ViewSchedule)
                result.Add((view.Name, view.Id.GetValue(), "schedule"));
            else if (view.ViewType == ViewType.Legend)
                result.Add((view.Name, view.Id.GetValue(), "legend"));
        }

        return result;
    }
}
