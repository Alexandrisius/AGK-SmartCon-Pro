using System.Globalization;
using System.IO;
using System.Text;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

internal sealed partial class LocalFamilyImportService
{
    private readonly IFamilyTypeRepository _typeRepository;
    private readonly IAttributeValueRepository _valueRepository;
    private readonly IFamilyDataImportRunRepository _runRepository;

    static LocalFamilyImportService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private sealed record TypeCatalogResolutionResult(TypeCatalogParseResult ParseResult, string SourceTxtPath);

    /// <summary>
    /// Finds a Type Catalog sidecar and either bakes its types into the managed
    /// .rfa or falls back to copying the source .rfa when no sidecar is present.
    /// Returns the parsed catalog when types were baked, so the caller can write
    /// the type descriptors to the database after the main catalog/version row
    /// has been committed.
    /// </summary>
    private async Task<TypeCatalogResolutionResult?> PrepareManagedRfaAsync(
        string sourceFilePath,
        string? originalSourcePath,
        string catalogItemId,
        string versionId,
        string versionLabel,
        string managedRfaPath,
        CancellationToken ct)
    {
        using var _scope = SmartConLogger.BeginScope("TypeCatalog",
            ("Method", nameof(PrepareManagedRfaAsync)),
            ("CatalogItemId", catalogItemId),
            ("VersionLabel", versionLabel));

        SmartConLogger.Debug(
            $"{nameof(PrepareManagedRfaAsync)}: source='{Path.GetFileName(sourceFilePath)}', " +
            $"original='{(originalSourcePath is null ? "<none>" : Path.GetFileName(originalSourcePath))}', catalogItem='{catalogItemId}', version='{versionLabel}'");

        // v2.0.0 UC-2: when the caller already wrote the file to managed
        // storage via SaveAs (Import Active Family Document), sourceFilePath
        // equals managedRfaPath byte-for-byte. Skip Copy/Bake — the file is
        // already at its final destination, and any I/O against the same
        // path while Revit holds the document open would be rejected with
        // "Access to the path is denied".
        if (string.Equals(sourceFilePath, managedRfaPath, StringComparison.OrdinalIgnoreCase))
        {
            SmartConLogger.Info(
                "Source equals managed path — file already in managed storage, " +
                "skipping copy/bake");
            return null;
        }

        // v2.0.0 UC-3/UC-4: the VM has already produced a managed staging file
        // for the system family (.rvt) or loadable family (.rfa) via CreateCleanProject
        // or EditFamily+SaveAs in the batch flow. The file lives somewhere under
        // {dbRoot}/files/ but at a different catalog-item-id directory than the
        // one ImportBatchAsync will allocate. Skip the copy (the source is
        // already in managed storage); we only need to register it.
        var dbRoot = _database.GetDatabaseRoot();
        var managedFilesRoot = string.IsNullOrEmpty(dbRoot)
            ? null
            : Path.Combine(dbRoot, "files");
        if (!string.IsNullOrEmpty(managedFilesRoot))
        {
            var normalizedSource = Path.GetFullPath(sourceFilePath);
            var normalizedManagedRoot = Path.GetFullPath(managedFilesRoot);
            var sourceIsInManaged = normalizedSource.StartsWith(
                normalizedManagedRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
            if (sourceIsInManaged)
            {
                SmartConLogger.Info(
                    $"Source already inside managed storage ('{Path.GetFileName(sourceFilePath)}') — " +
                    "skipping copy/bake; ImportBatchAsync will register it as the new version");
                return null;
            }
        }

        var sourceTxtPath = ResolveTypeCatalogPath(sourceFilePath, originalSourcePath, catalogItemId, ct);

        if (string.IsNullOrEmpty(sourceTxtPath))
        {
            SmartConLogger.Info($"No Type Catalog found for '{catalogItemId}' — copying source .rfa to managed storage");
            await CopySourceToManagedAsync(sourceFilePath, managedRfaPath, ct);
            return null;
        }

        SmartConLogger.Info($"Found Type Catalog sidecar: {Path.GetFileName(sourceTxtPath)}");

        var content = await Task.Run(() => ReadTypeCatalogWithEncodingFallback(sourceTxtPath!), ct);

        if (content.Length == 0)
        {
            SmartConLogger.Warn($"Type Catalog file is empty: {Path.GetFileName(sourceTxtPath)} [Action: verify the file content]");
            await CopySourceToManagedAsync(sourceFilePath, managedRfaPath, ct);
            return null;
        }

        var parseResult = TypeCatalogParser.Parse(content);

        if (!parseResult.HasEntries)
        {
            SmartConLogger.Warn($"Parsed 0 entries from {Path.GetFileName(sourceTxtPath)} [Action: verify the Type Catalog format]");
            await CopySourceToManagedAsync(sourceFilePath, managedRfaPath, ct);
            return null;
        }

        var bakeResult = await _typeCatalogBaker.BakeAsync(sourceFilePath, parseResult, managedRfaPath, ct);

        if (!bakeResult.Success || string.IsNullOrEmpty(bakeResult.OutputRfaPath) || !File.Exists(managedRfaPath))
        {
            throw new InvalidOperationException(
                $"Type Catalog bake failed for '{catalogItemId}': {bakeResult.ErrorMessage ?? "output file missing"} " +
                $"[Action: verify the .rfa and .txt are compatible]");
        }

        SmartConLogger.Info(
            $"Type Catalog baked into '{Path.GetFileName(managedRfaPath)}': {bakeResult.BakedTypeCount} type(s)");

        return new TypeCatalogResolutionResult(parseResult, sourceTxtPath!);
    }

    private async Task CopySourceToManagedAsync(string sourceFilePath, string managedRfaPath, CancellationToken ct)
    {
        var copyResult = await CopyFileWithRetryAsync(sourceFilePath, managedRfaPath, ct);
        if (!copyResult.Success)
        {
            throw new IOException(copyResult.ErrorMessage ?? "Failed to copy source .rfa to managed storage");
        }
    }

    private async Task ImportParsedTypeCatalogAsync(
        TypeCatalogParseResult parseResult,
        string sourceTxtPath,
        string catalogItemId,
        string versionId,
        string versionLabel,
        CancellationToken ct)
    {
        using var _scope = SmartConLogger.BeginScope("TypeCatalog",
            ("Method", nameof(ImportParsedTypeCatalogAsync)),
            ("CatalogItemId", catalogItemId),
            ("VersionLabel", versionLabel));

        var runId = Guid.NewGuid().ToString();
        var now = DateTimeOffset.UtcNow;

        var run = new FamilyDataImportRun(
            runId,
            catalogItemId,
            versionId,
            null,
            0,
            FamilyDataImportStatus.Succeeded,
            parseResult.Entries.Count,
            now,
            now,
            null);
        await _runRepository.CreateRunAsync(run, ct);

        var types = new List<FamilyTypeDescriptor>();
        var seenTypeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var duplicateCount = 0;

        for (var i = 0; i < parseResult.Entries.Count; i++)
        {
            var entry = parseResult.Entries[i];
            if (!seenTypeNames.Add(entry.TypeName))
            {
                duplicateCount++;
                SmartConLogger.Warn($"Duplicate type name '{entry.TypeName}' at index {i}, skipping (keeping first occurrence) [Action: проверьте .txt — удалите дубликат, если хотите импортировать все строки]");
                continue;
            }

            types.Add(new FamilyTypeDescriptor(
                Guid.NewGuid().ToString(),
                catalogItemId,
                entry.TypeName,
                types.Count,
                versionId,
                null,
                runId));
        }

        if (duplicateCount > 0)
        {
            SmartConLogger.Info($"Skipped {duplicateCount} duplicate type(s), imported {types.Count} unique types");
        }

        var typeIdsByName = await _typeRepository.SyncTypesAsync(catalogItemId, versionId, null, runId, types, ct);

        var values = new List<ExtractedAttributeValue>();
        for (var i = 0; i < parseResult.Entries.Count; i++)
        {
            var entry = parseResult.Entries[i];
            if (!seenTypeNames.Contains(entry.TypeName)) continue;
            if (!typeIdsByName.TryGetValue(SystemTypeIdentityKey.Build(null, null, entry.TypeName), out var typeId)) continue;

            foreach (var param in entry.ParameterValues)
            {
                values.Add(new ExtractedAttributeValue(
                    Id: Guid.NewGuid().ToString(),
                    CatalogItemId: catalogItemId,
                    VersionId: versionId,
                    FileId: null,
                    TypeId: typeId,
                    AttributeId: null,
                    BindingId: null,
                    ParameterName: param.Key,
                    ParameterScope: AttributeScope.Type,
                    StorageType: "String",
                    ValueText: param.Value,
                    ValueRaw: param.Value,
                    ValueNumber: null,
                    UnitTypeId: null,
                    Status: AttributeValueStatus.Found,
                    Message: null,
                    ExtractionRunId: runId,
                    ExtractedAtUtc: now));
            }
        }

        await _valueRepository.ReplaceSnapshotAsync(catalogItemId, versionId, runId, values, ct);

        SmartConLogger.Info($"Imported {types.Count} types from '{Path.GetFileName(sourceTxtPath)}' for catalog item {catalogItemId}");
    }

    private string? ResolveTypeCatalogPath(
        string sourceFilePath,
        string? originalSourcePath,
        string catalogItemId,
        CancellationToken ct)
    {
        var sourceTxtPath = Path.ChangeExtension(sourceFilePath, ".txt");

        if (!File.Exists(sourceTxtPath))
        {
            if (!string.IsNullOrEmpty(originalSourcePath)
                && !string.Equals(originalSourcePath, sourceFilePath, StringComparison.OrdinalIgnoreCase))
            {
                var originalTxtPath = Path.ChangeExtension(originalSourcePath, ".txt");
                if (File.Exists(originalTxtPath))
                {
                    sourceTxtPath = originalTxtPath;
                    SmartConLogger.Info(
                        $"Using sidecar from ORIGINAL source (next to '{originalSourcePath}'): " +
                        $"{Path.GetFileName(sourceTxtPath)}");
                }
                else
                {
                    SmartConLogger.Debug(
                        $"No .txt next to original '{originalSourcePath}' — trying previous version");
                }
            }

            if (!File.Exists(sourceTxtPath))
            {
                var previousTxtPath = FindPreviousVersionTypeCatalogPathAsync(catalogItemId, ct).GetAwaiter().GetResult();
                if (!string.IsNullOrEmpty(previousTxtPath))
                {
                    sourceTxtPath = previousTxtPath;
                    SmartConLogger.Info(
                        $"Using Type Catalog from previous version: " +
                        $"{Path.GetFileName(sourceTxtPath)}");
                }
                else
                {
                    return null;
                }
            }
        }
        else
        {
            SmartConLogger.Info(
                $"Found sidecar next to import source: {Path.GetFileName(sourceTxtPath)}");
        }

        return sourceTxtPath;
    }

    private async Task<string?> FindPreviousVersionTypeCatalogPathAsync(string catalogItemId, CancellationToken ct)
    {
        try
        {
            using var connection = _database.CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT ff.relative_path 
                FROM catalog_versions cv
                INNER JOIN family_files ff ON ff.id = cv.file_id
                WHERE cv.catalog_item_id = @itemId
                ORDER BY cv.published_at_utc DESC
                LIMIT 1 OFFSET 1
                """;
            cmd.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@itemId", catalogItemId));

            var result = await cmd.ExecuteScalarAsync(ct) as string;
            if (string.IsNullOrEmpty(result)) return null;

            var previousRfaPath = Path.Combine(_database.GetDatabaseRoot(), result);
            var previousTxtPath = Path.ChangeExtension(previousRfaPath, ".txt");

            return File.Exists(previousTxtPath) ? previousTxtPath : null;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"Failed to find previous version Type Catalog for {catalogItemId}: {ex.Message} [Action: проверьте БД каталога, эта версия будет импортирована без diff со старой]");
            return null;
        }
    }

    internal static string ReadTypeCatalogWithEncodingFallback(string filePath)
    {
        var utf8Bytes = File.ReadAllBytes(filePath);

        if (utf8Bytes.Length >= 2)
        {
            if (utf8Bytes[0] == 0xFF && utf8Bytes[1] == 0xFE)
                return Encoding.Unicode.GetString(utf8Bytes, 2, utf8Bytes.Length - 2);
            if (utf8Bytes[0] == 0xFE && utf8Bytes[1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(utf8Bytes, 2, utf8Bytes.Length - 2);
        }

        if (utf8Bytes.Length >= 3 && utf8Bytes[0] == 0xEF && utf8Bytes[1] == 0xBB && utf8Bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(utf8Bytes, 3, utf8Bytes.Length - 3);

        var utf8String = Encoding.UTF8.GetString(utf8Bytes);
        if (utf8String.Contains('\0'))
            return Encoding.Unicode.GetString(utf8Bytes);

        if (utf8String.Contains('\uFFFD'))
        {
            try
            {
                var result = UtfUnknown.CharsetDetector.DetectFromBytes(utf8Bytes);
                if (result.Detected?.Confidence > 0.7f && result.Detected.Encoding != null)
                {
                    return result.Detected.Encoding.GetString(utf8Bytes);
                }

                var ansiCodePage = CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
                return Encoding.GetEncoding(ansiCodePage).GetString(utf8Bytes);
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn($"Charset detection failed: {ex.Message}, returning UTF-8 result with replacement chars [Action: проверьте кодировку .txt; ожидается UTF-8 или Windows-1251]");
            }
        }

        return utf8String;
    }
}
