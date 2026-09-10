using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using SmartCon.Core.Logging;
using SmartCon.Core.Services.Implementation;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.FamilyManager;
using TUnit.Core.Executors;

namespace SmartCon.IntegrationTests.FamilyManager;

/// <summary>
/// Issue #238 regression (FHV11, LOOKUP section): an edit of a lookup
/// table's (таблица поиска / FamilySizeTable CSV) values must shift the
/// family content hash — pre-FHV11 such an edit produced a false Duplicate
/// on import. Pins:
///  1. The extractor reads the table content in the same family-document
///     session (raw CSV via ExportSizeTable, locale-invariant) and a
///     values-only CSV edit shifts <c>ComputeForLoadable</c>;
///  2. Merge-transfer consistency (the FHV10 GROUPS lesson): after a reload
///     merge with overwrite, the embedded family in the project carries the
///     NEW table content, so the embedded hash (EditFamily extraction —
///     the post-verify path) equals the file hash. Without this the LOOKUP
///     section would loop every stale update of such families on a
///     post-verify mismatch.
/// </summary>
public sealed class LookupTableHashTests : RevitApiTest
{
    private const string CsvName = "probe_lookup";
    private const string FamilyName = "LookupHashFam";

    private string? _template;
    private string? _tempDir;
    private string? _rfaPath;
    private List<Document>? _openDocs;

    [Before(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void Seed()
    {
        _template = SampleFiles.FindFamilyTemplate(Application);
        if (_template is null)
        {
            Skip.Test("Family templates (.rft) not found — cannot seed the lookup hash regression");
            return;
        }

        _tempDir = Path.Combine(Path.GetTempPath(), $"SmartConLookupHash_{Guid.NewGuid().ToString("N")}");
        Directory.CreateDirectory(_tempDir);
        _rfaPath = Path.Combine(_tempDir, FamilyName + ".rfa");
        _openDocs = new List<Document>();
    }

    [After(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void Cleanup()
    {
        if (_openDocs is not null)
        {
            foreach (var doc in _openDocs)
            {
                try
                {
                    if (doc.IsValidObject) doc.Close(false);
                }
                catch
                {
                }
            }
        }

        try
        {
            if (_tempDir is not null && Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, true);
        }
        catch
        {
        }
    }

    [Test]
    public async Task LookupTableValueEdit_ShiftsContentHash()
    {
        CreateFamilyWithLookupTable(_rfaPath!, csvRowPn: "15");
        var hashV1 = ComputeFileHash(_rfaPath!);

        MutateLookupCsv(_rfaPath!, csvRowPn: "16");
        var hashV2 = ComputeFileHash(_rfaPath!);

        SmartConLogger.Info($"#238 regression: hashV1={hashV1} hashV2={hashV2} shifted={hashV1 != hashV2}");

        using (Assert.Multiple())
        {
            await Assert.That(hashV1).IsNotNull();
            await Assert.That(hashV2).IsNotNull();
            await Assert.That(hashV2).IsNotEqualTo(hashV1);
        }
    }

    [Test]
    public async Task LookupTable_MergeTransfer_EmbeddedHashMatchesFileHash()
    {
        CreateFamilyWithLookupTable(_rfaPath!, csvRowPn: "15");
        var project = Application.NewProjectDocument(UnitSystem.Metric);
        _openDocs!.Add(project);
        using (var tx = new Transaction(project, "load v1"))
        {
            tx.Start();
            if (!project.LoadFamily(_rfaPath!, out _))
                throw new InvalidOperationException("LoadFamily(v1) returned false");
            tx.Commit();
        }

        MutateLookupCsv(_rfaPath!, csvRowPn: "16");

        using (var tx = new Transaction(project, "reload v2"))
        {
            tx.Start();
            var reloaded = project.LoadFamilySymbol(
                _rfaPath!, "T1", new OverwriteLoadOptions(), out _);
            if (!reloaded)
                throw new InvalidOperationException("LoadFamilySymbol(v2) returned false");
            tx.Commit();
        }

        var fileHash = ComputeFileHash(_rfaPath!);
        string? embeddedHash;
        var editDoc = project.EditFamily(FindFamily(project));
        try
        {
            embeddedHash = new FamilyContentHasher()
                .ComputeForLoadable(new RevitFamilySnapshotExtractor().ExtractFromFamilyDocument(editDoc))
                ?.HexString;
        }
        finally
        {
            editDoc.Close(false);
        }

        SmartConLogger.Info($"#238 merge-transfer: embedded={embeddedHash} file={fileHash} match={embeddedHash == fileHash}");

        using (Assert.Multiple())
        {
            await Assert.That(embeddedHash).IsNotNull();
            await Assert.That(fileHash).IsNotNull();
            await Assert.That(embeddedHash).IsEqualTo(fileHash);
        }
    }

    private string? ComputeFileHash(string rfaPath)
    {
        var fileDoc = Application.OpenDocumentFile(rfaPath);
        try
        {
            var snapshot = new RevitFamilySnapshotExtractor().ExtractFromFamilyDocument(fileDoc);
            return new FamilyContentHasher().ComputeForLoadable(snapshot)?.HexString;
        }
        finally
        {
            fileDoc.Close(false);
        }
    }

    private void CreateFamilyWithLookupTable(string rfaPath, string csvRowPn)
    {
        var famDoc = Application.NewFamilyDocument(_template!);
        using (var tx = new Transaction(famDoc, "seed"))
        {
            tx.Start();
#if REVIT2022_OR_GREATER
            famDoc.FamilyManager.AddParameter("Dn", GroupTypeId.General, SpecTypeId.Length, false);
            famDoc.FamilyManager.AddParameter("Pn", GroupTypeId.General, SpecTypeId.Number, false);
#else
            famDoc.FamilyManager.AddParameter("Dn", BuiltInParameterGroup.PG_GENERAL, ParameterType.Length, false);
            famDoc.FamilyManager.AddParameter("Pn", BuiltInParameterGroup.PG_GENERAL, ParameterType.Number, false);
#endif
            famDoc.FamilyManager.NewType("T1");
            tx.Commit();
        }

        var csvPath = Path.Combine(_tempDir!, CsvName + ".csv");
        File.WriteAllText(csvPath,
            ",Dn##length##millimeters,Pn##number##general\r\n50,50," + csvRowPn + "\r\n65,65,20\r\n");
        ImportCsv(famDoc, csvPath);

        famDoc.SaveAs(rfaPath, new SaveAsOptions { OverwriteExistingFile = true });
        famDoc.Close(false);
    }

    private void MutateLookupCsv(string rfaPath, string csvRowPn)
    {
        var famDoc = Application.OpenDocumentFile(rfaPath);
        try
        {
            var csvPath = Path.Combine(_tempDir!, CsvName + ".csv");
            File.WriteAllText(csvPath,
                ",Dn##length##millimeters,Pn##number##general\r\n50,50," + csvRowPn + "\r\n65,65,20\r\n");
            ImportCsv(famDoc, csvPath);
            famDoc.Save();
        }
        finally
        {
            famDoc.Close(false);
        }
    }

    private void ImportCsv(Document famDoc, string csvPath)
    {
        using var tx = new Transaction(famDoc, "import size table");
        tx.Start();
        var fstm = FamilySizeTableManager.GetFamilySizeTableManager(famDoc, famDoc.OwnerFamily.Id);
        if (fstm is null)
        {
            FamilySizeTableManager.CreateFamilySizeTableManager(famDoc, famDoc.OwnerFamily.Id);
            fstm = FamilySizeTableManager.GetFamilySizeTableManager(famDoc, famDoc.OwnerFamily.Id);
        }

        var tableName = Path.GetFileNameWithoutExtension(csvPath);
        if (fstm!.HasSizeTable(tableName))
            fstm.RemoveSizeTable(tableName);

        var ok = fstm.ImportSizeTable(famDoc, csvPath, new FamilySizeTableErrorInfo());
        SmartConLogger.Info($"#238 seed: ImportSizeTable('{tableName}') = {ok}");
        tx.Commit();
    }

    private static Family FindFamily(Document doc)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(Family))
            .Cast<Family>()
            .First(f => string.Equals(f.Name, FamilyName, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class OverwriteLoadOptions : IFamilyLoadOptions
    {
        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
        {
            overwriteParameterValues = true;
            return true;
        }

        public bool OnSharedFamilyFound(
            Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
        {
            source = FamilySource.Family;
            overwriteParameterValues = true;
            return true;
        }
    }
}
