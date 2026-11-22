using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Structure;
using Nice3point.TUnit.Revit;
using SmartCon.Agent.Services.Storage;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent.Jobs;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent;
using SmartCon.Revit.Agent.Jobs;
using SmartCon.Revit.Agent.Jobs.Actions;
using SmartCon.Revit.Transactions;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Case 4 v2 core machinery: the set_parameters job selector systemName (MEP
/// system members: Elements + BaseEquipment + PipingNetwork) and
/// includeSubComponents (nested shared families via GetSubComponentIds).
/// The receiver is a project-bound shared parameter — the RBS system name
/// parameter itself is read-only and is never written.
/// <para>
/// The includeSubComponents test builds a shared nested family
/// (FAMILY_SHARED=1 + flangeDoc.LoadFamily(hostDoc) nesting) but skips
/// honestly in this test host: file loads of session-written .rfa return
/// false and open-doc loads do not materialize the nested shared instance as
/// a project subcomponent (probe chain in the skip reason). The nested case
/// stays covered by the live dogfood of benchmark case 4.
/// </para>
/// </summary>
public sealed class AgentSystemFillJobTests : RevitApiTest
{
    private const string SystemName = "SC_Т1";
    private const string ParamName = "SC_ИмяСистемы";
    private const string SharedGroup = "SmartConTests";
    private const string HostFamilyName = "SC_HostEquipment";

    private Document? _document;
    private RevitTransactionService? _transactions;
    private SharedParameterFileScope? _sharedFile;
    private AgentDatabase? _database;
    private string? _databasePath;
    private SqliteAgentJobStore? _store;
    private JobRunner? _runner;
    private readonly List<string> _tempFamilyPaths = new();

    private bool _systemSeeded;
    private bool _nestedSeeded;
    private string? _systemSkipReason;
    private string? _nestedSkipReason;
    private long _pipe1Id;
    private long _pipe2Id;
    private long _hostInstanceId;
    private long _nestedInstanceId;
    private long _hostSymbolIdValue;
    private string _flangeSharedProbe = "";
    private string _hostNestedProbe = "";
    private List<long>? _members;

    private Document Doc => _document!;

    private string DocumentId => AgentDocumentIdentity.GetDocumentId(Doc);

    /// <summary>
    /// System members read post-commit (the same timing the SUT selector has):
    /// membership settles only after the seed transaction commits.
    /// </summary>
    private List<long> MemberIds => _members ??= CollectMembersFromModel();

    private SqliteAgentJobStore Store => _store!;

    private JobRunner Runner => _runner!;

    [Before(Test)]
    public void Seed()
    {
        // The plain default template has no PipeType — the MEP template does
        // (probe-proven, see SampleFiles.NewMepTemplateDocument).
        _document = SampleFiles.NewMepTemplateDocument(Application)
                    ?? Application.NewProjectDocument(UnitSystem.Metric);
        _transactions = new RevitTransactionService(new StubRevitContext(Doc));
        _sharedFile = SharedParameterFileScope.Create(Application);
        _databasePath = Path.Combine(
            Path.GetTempPath(), "sc-systemfill-" + Guid.NewGuid().ToString("N") + ".db");
        _database = new AgentDatabase(_databasePath);
        _database.Initialize();
        _store = new SqliteAgentJobStore(_database);
        _runner = new JobRunner(_store, new IJobAction[] { new SetParametersJobAction() }, _transactions);

        _nestedSeeded = TrySeedNestedFamily();
        SeedSystemAndBinding();
    }

    [After(Test)]
    public void Cleanup()
    {
        Doc.Close(false);
        _sharedFile?.Dispose();

        if (_databasePath is not null && File.Exists(_databasePath))
        {
            try
            {
                File.Delete(_databasePath);
            }
            catch (IOException)
            {
                // The store closes every connection; a locked file is not fatal.
            }
        }

        foreach (var path in _tempFamilyPaths)
        {
            if (!File.Exists(path))
                continue;

            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // Best-effort temp cleanup.
            }
        }
    }

    [Test]
    public async Task SystemName_SelectsSystemMembersAndFillsSharedParameter()
    {
        if (!_systemSeeded)
            Skip.Test(_systemSkipReason ?? "Систему SC_Т1 не удалось наполнить");

        await Assert.That(MemberIds.Count).IsGreaterThanOrEqualTo(2);
        await Assert.That(MemberIds.Contains(_pipe1Id)).IsTrue();
        await Assert.That(MemberIds.Contains(_pipe2Id)).IsTrue();

        var (job, _) = await PreviewAndApply(
            "{\"select\":{\"systemName\":\"" + SystemName + "\"},\"parameter\":\"" + ParamName +
            "\",\"value\":\"Т1\",\"scope\":\"instance\"}");

        // Units = the full member union computed by the seed itself.
        var items = await Store.LoadAllItemsAsync(job.JobId, CancellationToken.None);
        await Assert.That(items.Count).IsEqualTo(MemberIds.Count);
        await Assert.That(items.Count(item => item.State == JobItemState.Done)).IsEqualTo(MemberIds.Count);

        var written = 0;
        foreach (var id in MemberIds)
        {
            var parameter = Doc.GetElement(ElementIdCompat.Create(id))?.LookupParameter(ParamName);
            if (parameter?.AsString() == "Т1")
                written++;
        }

        await Assert.That(written).IsEqualTo(MemberIds.Count);
    }

    [Test]
    public async Task IncludeSubComponents_AddsNestedSharedFamily()
    {
        if (!_nestedSeeded)
            Skip.Test(_nestedSkipReason ?? "Вложенное shared-семейство не собралось");

        // Without the flag the nested instance is NOT part of the selection.
        var plain = await Preview(
            "{\"select\":{\"elementIds\":[" + _hostInstanceId + "]},\"parameter\":\"" + ParamName +
            "\",\"value\":\"Т1\"}");
        await Assert.That(plain.Count).IsEqualTo(1);

        // With the flag the nested shared family joins as a full unit.
        var (job, previewData) = await PreviewAndApply(
            "{\"select\":{\"elementIds\":[" + _hostInstanceId +
            "],\"includeSubComponents\":true},\"parameter\":\"" + ParamName + "\",\"value\":\"Т1\"}");
        await Assert.That(previewData.Count).IsEqualTo(2);

        var items = await Store.LoadAllItemsAsync(job.JobId, CancellationToken.None);
        await Assert.That(items.Count(item => item.State == JobItemState.Done)).IsEqualTo(2);

        var host = Doc.GetElement(ElementIdCompat.Create(_hostInstanceId));
        var nested = Doc.GetElement(ElementIdCompat.Create(_nestedInstanceId));
        await Assert.That(host?.LookupParameter(ParamName)?.AsString()).IsEqualTo("Т1");
        await Assert.That(nested?.LookupParameter(ParamName)?.AsString()).IsEqualTo("Т1");
        await Assert.That((nested as FamilyInstance)?.SuperComponent?.Id.GetValue())
            .IsEqualTo(_hostInstanceId);
    }

    [Test]
    public async Task SystemName_UnknownSystem_FailsTypedWithHint()
    {
        var preview = Runner.Start(
            new JobSpec
            {
                DocumentId = DocumentId,
                Action = "set_parameters",
                Mode = JobMode.Preview,
                SpecJson = "{\"select\":{\"systemName\":\"SC_НетТакой\"},\"parameter\":\"" + ParamName +
                           "\",\"value\":\"X\"}",
                Options = Options()
            },
            Doc);

        await Assert.That(preview.Success).IsFalse();
        await Assert.That(preview.Error!.Code).IsEqualTo("system_not_found");
        await Assert.That(preview.Error.ToMessage().Contains("list_mep_systems")).IsTrue();
    }

    [Test]
    public async Task SystemFillJob_Rollback_RestoresValues()
    {
        if (!_systemSeeded)
            Skip.Test(_systemSkipReason ?? "Систему SC_Т1 не удалось наполнить");

        var (job, _) = await PreviewAndApply(
            "{\"select\":{\"systemName\":\"" + SystemName + "\"},\"parameter\":\"" + ParamName +
            "\",\"value\":\"Т1\"}");

        var rollback = Runner.Rollback(Doc, job.JobId, dryRun: false);

        await Assert.That(rollback.State).IsEqualTo(JobState.RolledBack);
        await Assert.That(rollback.Restored).IsEqualTo(MemberIds.Count);
        await Assert.That(rollback.RestoreFailed).IsEqualTo(0);

        foreach (var id in MemberIds)
        {
            var value = Doc.GetElement(ElementIdCompat.Create(id))?.LookupParameter(ParamName)?.AsString();
            await Assert.That(string.IsNullOrEmpty(value)).IsTrue();
        }
    }

    // ── Сид ─────────────────────────────────────────────────────────────

    private void SeedSystemAndBinding()
    {
        var definition = SeedSharedDefinition();
        _systemSkipReason = "В шаблоне нет PipeType/PipingSystemType";
        try
        {
            _transactions!.RunInTransaction(Doc, "Seed system fill model", doc =>
            {
                BindParameterToCategories(doc, definition);

                var systemTypeId = new FilteredElementCollector(doc)
                    .OfClass(typeof(PipingSystemType))
                    .WhereElementIsElementType()
                    .FirstElementId();
                var pipeTypeId = new FilteredElementCollector(doc)
                    .OfClass(typeof(PipeType))
                    .WhereElementIsElementType()
                    .FirstElementId();
                if (systemTypeId.Equals(ElementId.InvalidElementId) ||
                    pipeTypeId.Equals(ElementId.InvalidElementId))
                    return;

                var level = ModelSeed.CreateLevel(doc, 0);

                var start = new XYZ(0, 0, 0);
                var joint = new XYZ(10, 0, 0);
                // A 90° bend: NewElbowFitting refuses collinear pipes ("failed to
                // insert elbow" — that join wants a coupling, not an elbow).
                var end = new XYZ(10, 10, 0);
                var pipe1 = Pipe.Create(doc, systemTypeId, pipeTypeId, level.Id, start, joint);
                var pipe2 = Pipe.Create(doc, systemTypeId, pipeTypeId, level.Id, joint, end);
                if (pipe1 is null || pipe2 is null)
                    return;
                _pipe1Id = pipe1.Id.GetValue();
                _pipe2Id = pipe2.Id.GetValue();

                // Pipe.Create assigns its connectors to an AUTO-created logical
                // system, so a manual PipingSystem.Create + Add always fails with
                // "connectors have been used". The reliable path: join physically
                // and RENAME the auto system.
                var elbow = doc.Create.NewElbowFitting(
                    ModelSeed.FindConnectorAt(pipe1, joint),
                    ModelSeed.FindConnectorAt(pipe2, joint));
                doc.Regenerate();

                _systemSkipReason = "Автосистема не создалась у труб после соединения " +
                                    "(pipe1.MEPSystem == null)";
                if (pipe1.MEPSystem is not PipingSystem system)
                    return;
                system.Name = SystemName;
                doc.Regenerate();
                _systemSeeded = true;
                _systemSkipReason = null;

                SeedHostInstance(doc);
                doc.Regenerate();
            });
        }
        catch (Exception ex)
        {
            // A broken seed must skip honestly instead of failing every test in
            // the class through the Before-hook (including the model-free ones).
            _systemSeeded = false;
            _systemSkipReason = "Сид системы упал: " + ex.GetType().Name + ": " + ex.Message;
        }
    }

    private List<long> CollectMembersFromModel()
    {
        foreach (var element in new FilteredElementCollector(Doc).OfClass(typeof(PipingSystem)))
        {
            if (element is not PipingSystem system ||
                !string.Equals(system.Name ?? string.Empty, SystemName, StringComparison.Ordinal))
                continue;
            return CollectMemberIds(system);
        }

        return new List<long>();
    }

    private static List<long> CollectMemberIds(PipingSystem system)
    {
        // ElementSet iterates ELEMENTS, not ids (InvalidCastException otherwise).
        var ids = new HashSet<long>();
        if (system.Elements is not null)
            foreach (var member in system.Elements)
                if (member is Element element)
                    ids.Add(element.Id.GetValue());
        if (system.BaseEquipment is { } equipment)
            ids.Add(equipment.Id.GetValue());
        if (system.PipingNetwork is not null)
            foreach (var member in system.PipingNetwork)
                if (member is Element element)
                    ids.Add(element.Id.GetValue());
        return ids.ToList();
    }

    private Definition SeedSharedDefinition()
    {
        var file = Application.OpenSharedParameterFile();
        var group = file!.Groups.get_Item(SharedGroup) ?? file.Groups.Create(SharedGroup);
        return group!.Definitions.Create(
            new ExternalDefinitionCreationOptions(ParamName, SpecTypeId.String.Text));
    }

    private void BindParameterToCategories(Document doc, Definition definition)
    {
        var categories = Application.Create.NewCategorySet();
        categories.Insert(Category.GetCategory(doc, BuiltInCategory.OST_PipeCurves));
        categories.Insert(Category.GetCategory(doc, BuiltInCategory.OST_PipeFitting));
        categories.Insert(Category.GetCategory(doc, BuiltInCategory.OST_GenericModel));
        var binding = Application.Create.NewInstanceBinding(categories);
        doc.ParameterBindings.Insert(definition, binding, GroupTypeId.Text);
    }

    private void SeedHostInstance(Document doc)
    {
        if (!_nestedSeeded || _hostSymbolIdValue == 0)
            return;

        if (doc.GetElement(ElementIdCompat.Create(_hostSymbolIdValue)) is not FamilySymbol hostSymbol)
        {
            _nestedSeeded = false;
            _nestedSkipReason = "Типоразмер хозяина не найден в проекте";
            return;
        }

        hostSymbol.Activate();
        var host = doc.Create.NewFamilyInstance(
            new XYZ(30, 10, 0), hostSymbol, StructuralType.NonStructural);
        _hostInstanceId = host.Id.GetValue();

        foreach (var subId in host.GetSubComponentIds())
        {
            _nestedInstanceId = subId.GetValue();
            break;
        }

        if (_nestedInstanceId == 0)
        {
            var superScan = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>()
                .Count(instance => instance.SuperComponent?.Id.GetValue() == _hostInstanceId);
            _nestedSeeded = false;
            _nestedSkipReason = "У экземпляра хозяина нет субкомпонентов; flangeSharedProbe={" +
                                _flangeSharedProbe + "} " + _hostNestedProbe +
                                " superScan=" + superScan;
        }
    }

    /// <summary>
    /// Builds host family A with a nested SHARED family B (flange shape of the
    /// case 4 seed) and loads A into the project. Shared flag via
    /// FAMILY_SHARED on the owner family; the nested instance is placed with
    /// FamilyCreate.NewFamilyInstance inside A's document.
    /// </summary>
    private bool TrySeedNestedFamily()
    {
        var template = SampleFiles.FindFamilyTemplate(Application);
        if (template is null)
        {
            _nestedSkipReason = "В установленном Revit нет family-шаблона (.rft)";
            return false;
        }

        var directory = Path.Combine(Path.GetTempPath(), "SmartConSystemFillTests");
        Directory.CreateDirectory(directory);
        var flangePath = Path.Combine(directory, Guid.NewGuid().ToString("N") + "-flange.rfa");
        var hostPath = Path.Combine(directory, Guid.NewGuid().ToString("N") + "-host.rfa");
        _tempFamilyPaths.Add(flangePath);
        _tempFamilyPaths.Add(hostPath);

        Document? flangeDoc = null;
        Document? hostDoc = null;
        try
        {
            flangeDoc = Application.NewFamilyDocument(template);
            using (var transaction = new Transaction(flangeDoc, "Mark shared"))
            {
                transaction.Start();
                var sharedParameter = flangeDoc.OwnerFamily.get_Parameter(BuiltInParameter.FAMILY_SHARED);
                sharedParameter?.Set(1);
                // Some .rft templates ship without a family type; a loaded family
                // with zero types has no FamilySymbol to nest (FamilySeed pattern).
                if (flangeDoc.FamilyManager.CurrentType is null)
                    flangeDoc.FamilyManager.NewType("Тип1");
                transaction.Commit();
                _flangeSharedProbe = "param=" + (sharedParameter is null ? "null" :
                    sharedParameter.AsInteger().ToString()) + " afterCommit=" +
                    (flangeDoc.OwnerFamily.get_Parameter(BuiltInParameter.FAMILY_SHARED)
                        ?.AsInteger().ToString() ?? "null");
            }

            flangeDoc.SaveAs(flangePath, new SaveAsOptions { OverwriteExistingFile = true });

            // LoadFamily(Document, options) takes the TARGET. The open flange doc
            // is the only transport that preserves types in this host (file loads
            // of the shared .rfa return false), and the target may be a family
            // document — that IS the nesting operation.
            hostDoc = Application.NewFamilyDocument(template);
            using (var transaction = new Transaction(hostDoc, "Name host"))
            {
                transaction.Start();
                hostDoc.OwnerFamily.Name = HostFamilyName;
                transaction.Commit();
            }

            var flangeFamily = flangeDoc.LoadFamily(hostDoc, new NestSharedLoadOptions());
            if (flangeFamily is null)
            {
                _nestedSkipReason = "Фланец не загрузился в семейство-хозяин";
                return false;
            }

            var flangeSymbolId = flangeFamily.GetFamilySymbolIds().FirstOrDefault();
            if (flangeSymbolId is null || hostDoc.GetElement(flangeSymbolId) is not FamilySymbol flangeSymbol)
            {
                _nestedSkipReason = "У фланца нет типоразмера в семействе-хозяине: family='" +
                                    flangeFamily.Name + "' symbols=" +
                                    flangeFamily.GetFamilySymbolIds().Count;
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
            _hostNestedProbe = "hostDocInstances=" + new FilteredElementCollector(hostDoc)
                .OfClass(typeof(FamilyInstance)).GetElementCount();
            hostDoc.Close(false);
            hostDoc = null;

            // Host into the project BY FILE: an open-doc load does not materialize
            // the nested SHARED instance as a subcomponent (probe: superScan=0),
            // while a file load is the UI workflow that does.
            if (!Doc.LoadFamily(hostPath, new NestSharedLoadOptions(), out var hostFamily) ||
                hostFamily is null)
            {
                _nestedSkipReason = "Семейство-хозяин не загрузилось в проект из файла; " +
                                    _hostNestedProbe;
                return false;
            }

            var hostSymbolId = hostFamily.GetFamilySymbolIds().FirstOrDefault();
            if (hostSymbolId is null)
            {
                _nestedSkipReason = "У хозяина нет типоразмера в проекте: family='" +
                                    hostFamily.Name + "'";
                return false;
            }

            _hostSymbolIdValue = hostSymbolId.GetValue();

            flangeDoc.Close(false);
            flangeDoc = null;
            return true;
        }
        catch (Exception ex)
        {
            _nestedSkipReason = "Сид вложенного семейства упал: " + ex.GetType().Name + ": " +
                                ex.Message;
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
                    // Already closed or invalid.
                }
            }
        }
    }

    /// <summary>Nesting load of a shared family: FamilySource.Project keeps the
    /// shared behaviour (the nested instance is exposed as a subcomponent).</summary>
    private sealed class NestSharedLoadOptions : IFamilyLoadOptions
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

    // ── Джоба ───────────────────────────────────────────────────────────

    private static JobOptions Options() => new()
    {
        SavePolicy = JobSavePolicy.Never,
        BatchSize = 50,
        SliceBudgetMs = 200,
        MaxItems = 1000
    };

    private async Task<JobPreview> Preview(string specJson)
    {
        var preview = Runner.Start(
            new JobSpec
            {
                DocumentId = DocumentId,
                Action = "set_parameters",
                Mode = JobMode.Preview,
                SpecJson = specJson,
                Options = Options()
            },
            Doc);

        if (!preview.Success)
            throw new InvalidOperationException("Preview failed: " + preview.Error!.ToMessage());

        Runner.RunToCompletion(Doc, TimeSpan.FromMinutes(2));
        var record = await Store.GetAsync(preview.Job!.JobId, CancellationToken.None);
        return JobJson.Deserialize<JobPreview>(record!.PreviewJson)!;
    }

    private async Task<(JobRecord Job, JobPreview Preview)> PreviewAndApply(string specJson)
    {
        var previewData = await Preview(specJson);

        var apply = Runner.Start(
            new JobSpec
            {
                DocumentId = DocumentId,
                Action = "set_parameters",
                Mode = JobMode.Apply,
                SpecJson = specJson,
                PreviewDigest = previewData.Digest,
                Options = Options()
            },
            Doc);

        if (!apply.Success)
            throw new InvalidOperationException("Apply failed: " + apply.Error!.ToMessage());

        var result = Runner.RunToCompletion(Doc, TimeSpan.FromMinutes(2));
        if (result.State != JobState.Completed)
            throw new InvalidOperationException("Job ended in state " + result.State + ": " + result.Error);

        return (apply.Job!, previewData);
    }
}
