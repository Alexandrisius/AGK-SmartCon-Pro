using System.Globalization;
using System.IO;
using Autodesk.Revit.DB;
using SmartCon.Agent.Services.Storage;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Jobs;
using SmartCon.Revit.Agent.Jobs.Actions;
using SmartCon.Revit.Agent.Tools.Families;
using SmartCon.Revit.Agent.Tools.Jobs;
using SmartCon.Revit.Agent.Tools.Read;
using SmartCon.Revit.Agent.Tools.Write;
using SmartCon.Revit.Transactions;
using RevitFamily = Autodesk.Revit.DB.Family;
using SmartCon.IntegrationTests.Agent.Support;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Case 5 of the real-cases benchmark (mass write, families): the model migrates
/// values from old shared parameters to new ones across 12 seeded families
/// (СК_Мигр_01…12). The seed covers the four real-world situations: clean
/// families (createTarget migration), a target parameter already driven by a
/// formula (must be cleared and reported), an instance-scope target (must be
/// reported as skipped, scope mismatch) and families without the old parameters
/// (must stay untouched). The uniform part must land in one map_family_parameters
/// job — per-family calls over 10+ families are a PARTIAL. Partial of
/// <see cref="AgentRealCasesBenchmarkTests"/>.
/// </summary>
public sealed partial class AgentRealCasesBenchmarkTests
{
    // Дословно из SSOT (docs/agent/archive/00-strategy/real-cases-benchmark.md, задача 5).
    private const string Case5Text =
        "Переезжаем на новый набор общих параметров. Новый ФОП я подключил как активный. " +
        "Перенеси значения из старых параметров в новые по всем семействам, где они есть, " +
        "и ничего не сломай. Мне нужен отчёт: сколько перенесено, что пропущено из-за формул " +
        "или несовпадения области, и что пришлось пересоздавать.";

    private const string Case5Title = "Миграция общих параметров: старый ФОП → новый";

    private const string Case5FamilyPrefix = "СК_Мигр_";
    private const string Case5SharedGroup = "Миграция";
    private const string Case5OldMarkName = "Старый_Марка";
    private const string Case5OldNoteName = "Старый_Примечание";
    private const string Case5OldLengthName = "Старый_Длина";
    private const string Case5NewMarkName = "Новый_Марка";
    private const string Case5NewNoteName = "Новый_Примечание";
    private const string Case5NewLengthName = "Новый_Длина";
    private const string Case5TargetFormula = "Ширина * 2";
    private const int Case5FamilyCount = 12;
    private const double Case5MmTolerance = 0.5;

    // Формула «Ширина * 2» на типах сида даёт 2000/3000 мм — значения старого
    // параметра (900+30i / 1500+30i мм) не совпадают ни с ними, ни с нулём, так
    // что read-back различает все три состояния нового параметра.
    private const double Case5FormulaDriven1Mm = FamilySeed.Type1WidthMm * 2.0;
    private const double Case5FormulaDriven2Mm = FamilySeed.Type2WidthMm * 2.0;

    private static readonly Guid Case5OldMarkGuid = new("7c1f5a2e-3b64-4d31-8e2a-9f0b1c2d3e01");
    private static readonly Guid Case5OldNoteGuid = new("7c1f5a2e-3b64-4d31-8e2a-9f0b1c2d3e02");
    private static readonly Guid Case5OldLengthGuid = new("7c1f5a2e-3b64-4d31-8e2a-9f0b1c2d3e03");
    private static readonly Guid Case5NewMarkGuid = new("e4d8c6b0-5f79-42a5-b3d1-6a8e0f2b4c04");
    private static readonly Guid Case5NewNoteGuid = new("e4d8c6b0-5f79-42a5-b3d1-6a8e0f2b4c05");
    private static readonly Guid Case5NewLengthGuid = new("e4d8c6b0-5f79-42a5-b3d1-6a8e0f2b4c06");

    private enum Case5FamilyKind
    {
        Clean,
        FormulaTarget,
        ScopeConflict,
        NoOld
    }

    // Ни одного Revit-типа в свойствах: вложенные типы тестового класса с
    // RevitAPI-типами преждевременно грузят RevitAPI и ломают инжектор
    // (rule 1/10, тот же механизм, что generic-хелперы с Revit-типами).
    private sealed class Case5FamilySeed
    {
        public string Name { get; init; } = string.Empty;

        public Case5FamilyKind Kind { get; init; }

        public string Mark1 { get; init; } = string.Empty;

        public string Mark2 { get; init; } = string.Empty;

        public string Note1 { get; init; } = string.Empty;

        public string Note2 { get; init; } = string.Empty;

        public double Length1Mm { get; init; }

        public double Length2Mm { get; init; }

        /// <summary>Формула на «Новый_Длина» зафиксирована сидом (07/08).</summary>
        public bool FormulaSeeded { get; set; }

        /// <summary>Instance-scope «Новый_Примечание» зафиксирован сидом (09/10).</summary>
        public bool InstanceNoteSeeded { get; set; }

        public string KindLabel => Kind switch
        {
            Case5FamilyKind.Clean => "чистое",
            Case5FamilyKind.FormulaTarget => "формула на новом параметре",
            Case5FamilyKind.ScopeConflict => "новый параметр instance-scope",
            _ => "без старых параметров"
        };
    }

    // Создаётся только внутри сида (rule 1: без статической инициализации).
    private sealed record Case5Definitions(
        ExternalDefinition OldMark,
        ExternalDefinition OldNote,
        ExternalDefinition OldLength,
        ExternalDefinition NewMark,
        ExternalDefinition NewNote,
        ExternalDefinition NewLength);

    private readonly List<FamilySeed> _case5Seeds = new();
    private List<Case5FamilySeed> _case5Families = null!;
    private SharedParameterFileScope? _case5SharedFile;
    private string? _case5SeedError;

    [Test]
    public async Task LiveCase5_SharedParameterMigration_BatchJobHonestReport()
    {
        if (!LiveConfigured())
        {
            Skip.Test("SMARTCON_AGENT_LIVE != 1 или не настроен API-ключ");
            return;
        }

        try
        {
            SeedOnBenchBaseLive(5, Case5Title, SeedCase5);
            var run = RunProductionCase(
                5, Case5Title, Case5Text,
                Case5SeedSummary(), EvaluateCase5, DumpCase5State);

            // Мягкий ассерт: прогон вообще состоялся (есть ответ модели или вызовы).
            var runHappened = run.Calls.Count > 0 || !string.IsNullOrWhiteSpace(run.Answer);
            await Assert.That(runHappened).IsTrue();
        }
        finally
        {
            CleanupCase5();
        }
    }

    // ── Сид ─────────────────────────────────────────────────────────────

    private static Case5FamilyKind Case5KindOf(int index) => index switch
    {
        <= 6 => Case5FamilyKind.Clean,
        <= 8 => Case5FamilyKind.FormulaTarget,
        <= 10 => Case5FamilyKind.ScopeConflict,
        _ => Case5FamilyKind.NoOld
    };

    private void SeedCase5()
    {
        _document = BenchDocuments.NewBase(Application);
        _transactions = new RevitTransactionService(new StubRevitContext(Doc));
        _changeSets = new InMemoryAgentChangeSetStore();
        _case5SeedError = null;
        _case5Families = new List<Case5FamilySeed>();


        for (var index = 1; index <= Case5FamilyCount; index++)
        {
            var suffix = index.ToString("00", CultureInfo.InvariantCulture);
            var name = Case5FamilyPrefix + suffix;
            var seed = FamilySeed.Create(Application, Doc, name, out var reason);
            if (seed is null)
            {
                Skip.Test(reason ?? "Не удалось сидировать семейство " + name);
                return;
            }

            _case5Seeds.Add(seed);
            _case5Families.Add(new Case5FamilySeed
            {
                Name = name,
                Kind = Case5KindOf(index),
                Mark1 = "М-" + suffix + "-А",
                Mark2 = "М-" + suffix + "-Б",
                Note1 = "П-" + suffix + "-А",
                Note2 = "П-" + suffix + "-Б",
                Length1Mm = 900.0 + index * 30.0,
                Length2Mm = 1500.0 + index * 30.0
            });
        }

        _case5SharedFile = SharedParameterFileScope.Create(Application);
        var definitions = SeedCase5Definitions();
        if (_case5SeedError is not null)
        {
            Skip.Test(_case5SeedError);
            return;
        }

        foreach (var family in _case5Families)
        {
            SeedCase5Family(family, definitions!);
            if (_case5SeedError is not null)
            {
                Skip.Test(_case5SeedError);
                return;
            }
        }

        VerifyCase5Seed();
    }

    private Case5Definitions? SeedCase5Definitions()
    {
        var file = Application.OpenSharedParameterFile();
        if (file is null)
        {
            _case5SeedError = "Временный ФОП не открылся";
            return null;
        }

        try
        {
            var group = file.Groups.Create(Case5SharedGroup);

            ExternalDefinition Create(string name, ForgeTypeId spec, Guid guid) =>
                (ExternalDefinition)group.Definitions.Create(
                    new ExternalDefinitionCreationOptions(name, spec) { GUID = guid });

            return new Case5Definitions(
                Create(Case5OldMarkName, SpecTypeId.String.Text, Case5OldMarkGuid),
                Create(Case5OldNoteName, SpecTypeId.String.Text, Case5OldNoteGuid),
                Create(Case5OldLengthName, SpecTypeId.Length, Case5OldLengthGuid),
                Create(Case5NewMarkName, SpecTypeId.String.Text, Case5NewMarkGuid),
                Create(Case5NewNoteName, SpecTypeId.String.Text, Case5NewNoteGuid),
                Create(Case5NewLengthName, SpecTypeId.Length, Case5NewLengthGuid));
        }
        catch (Exception ex)
        {
            _case5SeedError = "Не удалось засидировать определения ФОП: " +
                              ex.GetType().Name + ": " + ex.Message;
            return null;
        }
    }

    private void SeedCase5Family(Case5FamilySeed family, Case5Definitions definitions)
    {
        var projectFamily = FindCase5ProjectFamily(family.Name);
        if (projectFamily is null)
        {
            _case5SeedError = "Сид-семейство «" + family.Name + "» не найдено после загрузки";
            return;
        }

        Document? familyDoc = null;
        try
        {
            familyDoc = Doc.EditFamily(projectFamily);
            var manager = familyDoc.FamilyManager;
            using (var transaction = new Transaction(familyDoc, "Seed case 5 old parameters"))
            {
                transaction.Start();
                if (family.Kind != Case5FamilyKind.NoOld)
                {
                    var mark = manager.AddParameter(definitions.OldMark, GroupTypeId.General, false);
                    var note = manager.AddParameter(definitions.OldNote, GroupTypeId.General, false);
                    var length = manager.AddParameter(
                        definitions.OldLength, GroupTypeId.General, false);
                    foreach (FamilyType type in manager.Types)
                    {
                        string markValue;
                        string noteValue;
                        double lengthMm;
                        if (string.Equals(type.Name, FamilySeed.Type1, StringComparison.OrdinalIgnoreCase))
                        {
                            markValue = family.Mark1;
                            noteValue = family.Note1;
                            lengthMm = family.Length1Mm;
                        }
                        else if (string.Equals(
                                     type.Name, FamilySeed.Type2, StringComparison.OrdinalIgnoreCase))
                        {
                            markValue = family.Mark2;
                            noteValue = family.Note2;
                            lengthMm = family.Length2Mm;
                        }
                        else
                        {
                            continue;
                        }

                        manager.CurrentType = type;
                        manager.Set(mark, markValue);
                        manager.Set(note, noteValue);
                        manager.Set(length, Case5MmToFeet(lengthMm));
                    }
                }

                if (family.Kind == Case5FamilyKind.ScopeConflict)
                    manager.AddParameter(definitions.NewNote, GroupTypeId.General, true);

                if (family.Kind == Case5FamilyKind.FormulaTarget)
                    manager.AddParameter(definitions.NewLength, GroupTypeId.General, false);

                if (transaction.Commit() != TransactionStatus.Committed)
                {
                    _case5SeedError = "Не удалось записать параметры сида в «" + family.Name + "»";
                    return;
                }
            }

            // Формула — отдельной транзакцией: SetFormula на общем параметре
            // капризничает, когда в той же транзакции меняли значения (форум
            // Autodesk, FamilyManager.SetFormula).
            if (family.Kind == Case5FamilyKind.FormulaTarget)
            {
                using var transaction = new Transaction(familyDoc, "Seed case 5 target formula");
                transaction.Start();
                var target = Case5FamilyParameter(manager, Case5NewLengthName);
                if (target is null)
                {
                    _case5SeedError = "«" + family.Name + "»: «" + Case5NewLengthName +
                                      "» не добавлен — формула не сеется";
                    return;
                }

                manager.SetFormula(target, Case5TargetFormula);
                if (transaction.Commit() != TransactionStatus.Committed)
                {
                    _case5SeedError = "Не удалось задать формулу в «" + family.Name + "»";
                    return;
                }
            }

            if (!VerifyCase5FamilyDoc(manager, family))
                return;

            familyDoc.LoadFamily(Doc, new OverwriteFamilyLoadOptions());
        }
        catch (Exception ex)
        {
            _case5SeedError = "Сид «" + family.Name + "»: " + ex.GetType().Name + ": " + ex.Message;
        }
        finally
        {
            if (familyDoc is not null && familyDoc.IsValidObject)
                familyDoc.Close(false);
        }
    }

    private static FamilyParameter? Case5FamilyParameter(
        Autodesk.Revit.DB.FamilyManager manager,
        string name) =>
        manager.GetParameters().FirstOrDefault(parameter =>
            string.Equals(parameter.Definition.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>In-document seed checks (scope/formula видны только в редакторе семейства).</summary>
    private bool VerifyCase5FamilyDoc(Autodesk.Revit.DB.FamilyManager manager, Case5FamilySeed family)
    {
        if (family.Kind == Case5FamilyKind.ScopeConflict)
        {
            var note = Case5FamilyParameter(manager, Case5NewNoteName);
            if (note is null || !note.IsInstance)
            {
                _case5SeedError = "«" + family.Name + "»: «" + Case5NewNoteName +
                                  "» не стал instance-параметром";
                return false;
            }

            family.InstanceNoteSeeded = true;
        }

        if (family.Kind == Case5FamilyKind.FormulaTarget)
        {
            var length = Case5FamilyParameter(manager, Case5NewLengthName);
            if (length is null || length.IsInstance || string.IsNullOrWhiteSpace(length.Formula))
            {
                _case5SeedError = "«" + family.Name + "»: «" + Case5NewLengthName +
                                  "» не получил формулу «" + Case5TargetFormula + "»";
                return false;
            }

            family.FormulaSeeded = true;
        }

        if (family.Kind == Case5FamilyKind.NoOld &&
            manager.GetParameters().Any(parameter => parameter.Definition.Name.StartsWith(
                "Старый_", StringComparison.OrdinalIgnoreCase)))
        {
            _case5SeedError = "«" + family.Name + "»: unexpectedly содержит старые параметры";
            return false;
        }

        return true;
    }

    private RevitFamily? FindCase5ProjectFamily(string familyName)
    {
        foreach (var element in new FilteredElementCollector(Doc).OfClass(typeof(RevitFamily)))
        {
            if (element is RevitFamily family &&
                string.Equals(family.Name, familyName, StringComparison.OrdinalIgnoreCase))
                return family;
        }

        return null;
    }

    private FamilySeed? Case5SeedOf(string familyName) =>
        _case5Seeds.FirstOrDefault(seed =>
            string.Equals(seed.FamilyName, familyName, StringComparison.OrdinalIgnoreCase));

    private void CleanupCase5()
    {
        foreach (var seed in _case5Seeds)
            seed.Dispose();

        _case5Seeds.Clear();
        _case5SharedFile?.Dispose();
        _case5SharedFile = null;
    }

    // ── Сводка сида и тулы ──────────────────────────────────────────────

    private List<string> Case5SeedSummary()
    {
        var lines = new List<string>
        {
            Case5FamilyCount + " семейств «" + Case5FamilyPrefix +
            "01…12» (по 2 типа, значения по типам различаются):",
            "- 01–06 чистые: «" + Case5OldMarkName + "»/«" + Case5OldNoteName +
            "» (текст, type) и «" + Case5OldLengthName + "» (длина, type), новых нет",
            "- 07–08: то же + «" + Case5NewLengthName +
            "» уже существует (type) и имеет формулу «" + Case5TargetFormula + "»",
            "- 09–10: то же + «" + Case5NewNoteName + "» уже существует как INSTANCE-scope",
            "- 11–12: без старых параметров вообще",
            "ФОП подменён временным файлом, группа «" + Case5SharedGroup + "»: старые (" +
            Case5OldMarkName + ", " + Case5OldNoteName + ", " + Case5OldLengthName +
            ") и новые (" + Case5NewMarkName + ", " + Case5NewNoteName + ", " +
            Case5NewLengthName + " — те же спеки, другие GUID)"
        };
        foreach (var family in _case5Families)
        {
            lines.Add("  - «" + family.Name + "» (" + family.KindLabel + ")" +
                      (family.Kind == Case5FamilyKind.NoOld
                          ? string.Empty
                          : ": " + family.Mark1 + "/" + family.Mark2 + ", " + family.Note1 +
                            "/" + family.Note2 + ", " + family.Length1Mm.ToString("F0") + "/" +
                            family.Length2Mm.ToString("F0") + " мм"));
        }

        return lines;
    }


    // ── State read-back — см. AgentRealCasesBenchmarkTests.Case5.State.cs ──
}
