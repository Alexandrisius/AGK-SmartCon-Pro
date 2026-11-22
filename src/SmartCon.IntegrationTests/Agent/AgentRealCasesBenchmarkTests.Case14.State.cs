using System.IO;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent;
using SmartCon.Revit.Agent.Tools.Read;
using SmartCon.Revit.Agent.Tools.Write;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Case 14 state block (v2 case 9): the donor cleanup ritual (a leftover
/// background donor is closed by path before the registry reset, the temp
/// directory removed), the seed summary and post-run dump (views vs the donor
/// pattern, marks vs the donor truth) and the case toolset — the reads wrapped
/// in <see cref="Case14RoutedTool"/> so the harness dispatches them through
/// the production ExecuteAsync path with documentId routing (the base
/// ExecuteForTests bypasses it), plus open/close_model and the active-model
/// writes. Partial of <see cref="AgentRealCasesBenchmarkTests"/>.
/// </summary>
public sealed partial class AgentRealCasesBenchmarkTests
{
    // ── Чистка, сводка сида, дамп, тулы ─────────────────────────────────
    // Вердикт — в Case14.Evaluation.cs (EvaluateCase14).

    private void CleanupCase14()
    {
        // Зависший донор закрывается ДО снятия реестра (Documents меняется при Close).
        try
        {
            var toClose = new List<Document>();
            foreach (Document openDoc in Application.Documents)
            {
                if (!string.IsNullOrEmpty(openDoc.PathName) &&
                    string.Equals(openDoc.PathName, _case14DonorPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    toClose.Add(openDoc);
                }
            }

            foreach (var openDoc in toClose)
                openDoc.Close(false);
        }
        catch (Exception)
        {
            // Чистка best-effort.
        }

        AgentBackgroundDocuments.ClearForTests();
        _case14SharedFile?.Dispose();
        try
        {
            if (_case14Directory is not null && Directory.Exists(_case14Directory))
                Directory.Delete(_case14Directory, recursive: true);
        }
        catch (Exception)
        {
            // Занят или не пуст — останется во %TEMP%, не критично.
        }
    }

    private List<string> Case14SeedSummary()
    {
        var lines = new List<string>
        {
            "основная модель: 4 вида с кривыми именами («Копия 1 — 3D», «Вид 1», " +
            "«Вид 2», «Черновик»; по два на уровнях «" + Case14Level1 + "»/«" + Case14Level2 +
            "») + семейства с ПУСТОЙ маркой:",
        };
        foreach (var typeSeed in _case14Types)
        {
            lines.Add("  - «" + typeSeed.FamilyName + "» / «" + typeSeed.TypeName +
                      "»: марка «" + (Case14MarkOf(typeSeed.TypeId) ?? "(пусто)") + "»");
        }

        lines.Add("модель-эталон (донор): " + _case14DonorPath);
        lines.Add("  - виды: " + string.Join(", ", Case14DonorViews));
        lines.Add("  - марки: " + string.Join("; ", Case14FamilyTruth()
            .Select(truth => truth.Family + " = «" + truth.Mark + "»")));
        lines.Add("донор открыт только по явной просьбе (путь назван в задаче), " +
                  "файл эталона менять нельзя");
        return lines;
    }

    private string DumpCase14State()
    {
        var builder = new StringBuilder();
        builder.AppendLine("| Вид | Уровень | Имя сейчас | Имя донора |");
        builder.AppendLine("|---|---|---|---|");
        foreach (var viewSeed in _case14Views)
        {
            var view = Doc.GetElement(ElementIdCompat.Create(viewSeed.ViewId)) as View;
            builder.AppendLine("| " + viewSeed.OriginalName + " | " + viewSeed.LevelName +
                              " | " + (view?.Name ?? "(вид исчез)") + " | (паттерн «В_Этаж N_Система») |");
        }

        builder.AppendLine();
        builder.AppendLine("| Семейство | Марка сейчас | Марка донора |");
        builder.AppendLine("|---|---|---|");
        foreach (var typeSeed in _case14Types)
        {
            builder.AppendLine("| " + typeSeed.FamilyName + " | " +
                              (Case14MarkOf(typeSeed.TypeId) ?? "(пусто)") + " | " +
                              typeSeed.ExpectedMark + " |");
        }

        if (_case14DonorPath is not null)
        {
            builder.AppendLine();
            builder.AppendLine("Донор: " + _case14DonorPath +
                              (File.Exists(_case14DonorPath) ? ", файл на месте" : ", ФАЙЛА НЕТ"));
        }

        return builder.ToString();
    }

    private List<IAgentTool> CreateCase14Tools()
    {
        var context = new StubRevitContext(Doc);

        // Read-туры — через адаптер: production-путь ExecuteAsync с documentId-роутингом
        // (ExecuteForTests харнесса роутинг обходит — см. кейс 9 v2).
        IAgentTool Routed(IAgentTool readTool) => new Case14RoutedTool(readTool);

        var tools = new List<IAgentTool>
        {
            Routed(new ListViewsTool(context)),
            Routed(new ListFamiliesTool(context)),
            Routed(new ListFamilyTypesTool(context)),
            Routed(new GetElementDetailsTool(context)),
            Routed(new ListLevelsTool(context)),
            Routed(new GetProjectOverviewTool(context)),
            Routed(new ListCategoriesTool(context)),
            new OpenModelBackgroundTool(context),
            new CloseModelTool(context),
            new SetParametersTool(context, _transactions!, _changeSets!),
            new RenameViewsTool(context, _transactions!, _changeSets!),
            new UndoLastTool(context, _transactions!, _changeSets!)
        };

        AddCaseFindTools(tools);
        return tools;
    }

    /// <summary>
    /// Загружает .rfa семейств в документ ВНУТРИ открытой транзакции. Строчный
    /// LoadFamily(string) без транзакции не бросает исключение — молча
    /// возвращает false (Autodesk forum 11565753 / 11792567, блог Autodesk):
    /// загрузка меняет базу и требует транзакцию, как любая запись.
    /// </summary>
    private bool Case14LoadFamilies(Document doc, List<string> familyPaths)
    {
        foreach (var path in familyPaths)
        {
            try
            {
                var loaded = doc.LoadFamily(path, out var family);
                _case14LoadReport.Add(
                    Path.GetFileName(path) + ": " + (loaded ? "ok" : "FALSE") +
                    " → «" + (family?.Name ?? "?") + "»");
                Console.WriteLine("PROBE case14 LoadFamily '" + path + "' loaded=" +
                                  loaded + " family='" + (family?.Name ?? "?") + "'");
                if (!loaded)
                {
                    _case14SeedError = "LoadFamily молча вернул false (нет транзакции?): " + path;
                    return false;
                }
            }
            catch (Exception ex)
            {
                _case14SeedError = "LoadFamily '" + path + "': " +
                                   ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        return true;
    }

    /// <summary>Диагностика донора для скип-сообщения: загрузки + фактические семейства.</summary>
    private string Case14DonorDiagnostics(Document doc)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append("; загрузка .rfa: [");
        builder.Append(string.Join("; ", _case14LoadReport));
        builder.Append("]; GenericModel-типы в доноре: [");
        var symbols = new List<string>();
        try
        {
            foreach (var element in new FilteredElementCollector(doc)
                         .OfCategory(BuiltInCategory.OST_GenericModel)
                         .WhereElementIsElementType())
            {
                if (element is FamilySymbol symbol)
                    symbols.Add(symbol.FamilyName + "/" + symbol.Name);
            }
        }
        catch (Exception ex)
        {
            symbols.Add("(коллектор: " + ex.GetType().Name + ")");
        }

        builder.Append(string.Join(", ", symbols));
        builder.Append("]");
        return builder.ToString();
    }

    /// <summary>
    /// Адаптер read-тулов кейса 14: AgentReadToolBase.ExecuteForTests зовёт
    /// Execute напрямую и обходит documentId-роутинг, а кейсу нужны чтения из
    /// донора. Через IAgentTool ядро харнесса отправляет тул в plain-ветку
    /// (ExecuteAsync с тем же контекстом), где роутинг работает.
    /// </summary>
    private sealed class Case14RoutedTool : IAgentTool
    {
        private readonly IAgentTool _inner;

        public Case14RoutedTool(IAgentTool inner) => _inner = inner;

        public AgentToolDefinition Definition => _inner.Definition;

        public Task<ToolResult> ExecuteAsync(
            AgentToolContext context,
            JsonElement args,
            CancellationToken ct) => _inner.ExecuteAsync(context, args, ct);
    }
}
