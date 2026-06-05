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
        // Регистрируем CodePages encoding provider для ANSI кодировок (не встроен в .NET Core)
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private async Task ImportTypeCatalogIfPresentAsync(
        string sourceFilePath,
        string? originalSourcePath,
        string catalogItemId,
        string versionId,
        string versionLabel,
        CancellationToken ct)
    {
        SmartConLogger.Debug(
            $"[TypeCatalog] ImportTypeCatalogIfPresentAsync: source='{sourceFilePath}', " +
            $"original='{originalSourcePath ?? "<none>"}', catalogItem='{catalogItemId}', version='{versionLabel}'");

        var sourceTxtPath = Path.ChangeExtension(sourceFilePath, ".txt");

        if (!File.Exists(sourceTxtPath))
        {
            // 1) Try the sidecar next to the ORIGINAL file (e.g. when sourceFilePath
            //    is a temp .rfa that was copied from the user's working folder or
            //    from managed storage but the sidecar wasn't copied along with it).
            if (!string.IsNullOrEmpty(originalSourcePath)
                && !string.Equals(originalSourcePath, sourceFilePath, StringComparison.OrdinalIgnoreCase))
            {
                var originalTxtPath = Path.ChangeExtension(originalSourcePath, ".txt");
                if (File.Exists(originalTxtPath))
                {
                    sourceTxtPath = originalTxtPath;
                    SmartConLogger.Info(
                        $"[TypeCatalog] Using sidecar from ORIGINAL source (next to '{originalSourcePath}'): " +
                        $"{Path.GetFileName(sourceTxtPath)}");
                }
                else
                {
                    SmartConLogger.Debug(
                        $"[TypeCatalog] No .txt next to original '{originalSourcePath}' — trying previous version");
                }
            }

            // 2) Legacy fallback: previous version in managed storage.
            if (!File.Exists(sourceTxtPath) || sourceTxtPath == Path.ChangeExtension(sourceFilePath, ".txt"))
            {
                var previousTxtPath = await FindPreviousVersionTypeCatalogPathAsync(catalogItemId, ct);
                if (!string.IsNullOrEmpty(previousTxtPath))
                {
                    sourceTxtPath = previousTxtPath;
                    SmartConLogger.Info(
                        $"[TypeCatalog] Using Type Catalog from previous version: " +
                        $"{Path.GetFileName(sourceTxtPath)}");
                }
                else
                {
                    SmartConLogger.Info(
                        $"[TypeCatalog] No Type Catalog found for '{catalogItemId}' (no sidecar, no previous version)");
                    return;
                }
            }
        }
        else
        {
            SmartConLogger.Info(
                $"[TypeCatalog] Found sidecar next to import source: {Path.GetFileName(sourceTxtPath)}");
        }

        try
        {
            var destTxtPath = _pathResolver.GetRfaFilePath(catalogItemId, versionLabel, Path.GetFileName(sourceTxtPath));
            var destDir = Path.GetDirectoryName(destTxtPath);
            if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                Directory.CreateDirectory(destDir);

            await CopyTypeCatalogWithRetryAsync(sourceTxtPath!, destTxtPath, ct);

            var content = await Task.Run(() => ReadTypeCatalogWithEncodingFallback(sourceTxtPath!), ct);
            
            if (content.Length == 0)
            {
                SmartConLogger.Warn($"[TypeCatalog] File is empty: {Path.GetFileName(sourceTxtPath)}");
                return;
            }
            
            var parseResult = TypeCatalogParser.Parse(content);

            if (!parseResult.HasEntries)
            {
                SmartConLogger.Warn($"[TypeCatalog] Parsed 0 entries from {Path.GetFileName(sourceTxtPath)}");
                return;
            }

            var runId = Guid.NewGuid().ToString();
            var now = DateTimeOffset.UtcNow;

            // Create import run record (required for FOREIGN KEY constraint)
            var run = new FamilyDataImportRun(
                runId,
                catalogItemId,
                versionId,
                null,
                null,
                0, // Revit version not applicable for Type Catalog
                FamilyDataImportStatus.Succeeded,
                parseResult.Entries.Count,
                now,
                now,
                null);
            await _runRepository.CreateRunAsync(run, ct);

            var types = new List<FamilyTypeDescriptor>();
            var values = new List<ExtractedAttributeValue>();
            var seenTypeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var duplicateCount = 0;

            for (var i = 0; i < parseResult.Entries.Count; i++)
            {
                var entry = parseResult.Entries[i];
                if (!seenTypeNames.Add(entry.TypeName))
                {
                    duplicateCount++;
                    SmartConLogger.Warn($"[TypeCatalog] Duplicate type name '{entry.TypeName}' at index {i}, skipping (keeping first occurrence)");
                    continue;
                }

                var typeId = Guid.NewGuid().ToString();
                types.Add(new FamilyTypeDescriptor(
                    typeId,
                    catalogItemId,
                    entry.TypeName,
                    types.Count,
                    versionId,
                    null,
                    runId));

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

            if (duplicateCount > 0)
            {
                SmartConLogger.Info($"[TypeCatalog] Skipped {duplicateCount} duplicate type(s), imported {types.Count} unique types");
            }

            await _typeRepository.SaveTypesForRunAsync(catalogItemId, versionId, null, runId, types, ct);
            await _valueRepository.ReplaceSnapshotAsync(catalogItemId, versionId, runId, values, ct);

            SmartConLogger.Info($"[TypeCatalog] Imported {types.Count} types from '{Path.GetFileName(sourceTxtPath)}' for catalog item {catalogItemId}");
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"[TypeCatalog] Failed to import type catalog for {catalogItemId}: {ex.Message}");
            throw;
        }
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
            SmartConLogger.Warn($"[TypeCatalog] Failed to find previous version Type Catalog for {catalogItemId}: {ex.Message}");
            return null;
        }
    }

    private static async Task CopyTypeCatalogWithRetryAsync(string sourcePath, string destPath, CancellationToken ct)
    {
        for (var attempt = 0; attempt < CopyMaxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await Task.Run(() =>
                {
                    using var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var destStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
                    sourceStream.CopyTo(destStream);
                    destStream.Flush();
                }, ct);
                
                File.SetAttributes(destPath, File.GetAttributes(destPath) | FileAttributes.ReadOnly);
                return;
            }
            catch (IOException) when (attempt < CopyMaxRetries - 1)
            {
                await Task.Delay(CopyRetryDelaysMs[attempt], ct);
            }
        }
        
        throw new IOException($"Failed to copy Type Catalog after {CopyMaxRetries} attempts");
    }

    private static string ReadTypeCatalogWithEncodingFallback(string filePath)
    {
        // Try UTF-8 first (most common, with BOM detection)
        var utf8Bytes = File.ReadAllBytes(filePath);
        
        // Check for UTF-16 BOM
        if (utf8Bytes.Length >= 2)
        {
            if (utf8Bytes[0] == 0xFF && utf8Bytes[1] == 0xFE)
                return Encoding.Unicode.GetString(utf8Bytes, 2, utf8Bytes.Length - 2);
            if (utf8Bytes[0] == 0xFE && utf8Bytes[1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(utf8Bytes, 2, utf8Bytes.Length - 2);
        }
        
        // Check for UTF-8 BOM
        if (utf8Bytes.Length >= 3 && utf8Bytes[0] == 0xEF && utf8Bytes[1] == 0xBB && utf8Bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(utf8Bytes, 3, utf8Bytes.Length - 3);
        
        // No BOM: try UTF-8 first, fallback to ANSI if invalid sequences detected
        var utf8String = Encoding.UTF8.GetString(utf8Bytes);
        if (utf8String.Contains('\0'))
            return Encoding.Unicode.GetString(utf8Bytes);
        
        // Check for replacement characters (U+FFFD) which indicate invalid UTF-8 sequences.
        // Use Mozilla Universal Charset Detector for automatic encoding detection.
        if (utf8String.Contains('\uFFFD'))
        {
            try
            {
                var result = UtfUnknown.CharsetDetector.DetectFromBytes(utf8Bytes);
                if (result.Detected?.Confidence > 0.7f && result.Detected.Encoding != null)
                {
                    return result.Detected.Encoding.GetString(utf8Bytes);
                }

                // Fallback to system ANSI if detector is not confident enough
                var ansiCodePage = CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
                return Encoding.GetEncoding(ansiCodePage).GetString(utf8Bytes);
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn($"[TypeCatalog] Charset detection failed: {ex.Message}, returning UTF-8 result with replacement chars");
            }
        }
        
        return utf8String;
    }
}
