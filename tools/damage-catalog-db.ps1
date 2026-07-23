<#
.SYNOPSIS
    «Состаривает» тестовую БД FamilyManager (catalog.db): удаляет/ломает данные,
    которые чинит optional backfill-миграция (ADR-054, команда «Обновить базу»).

.DESCRIPTION
    === ИНСТРУКЦИЯ ДЛЯ АГЕНТА (читать целиком перед использованием) ===

    ЗАЧЕМ ЭТОТ СКРИПТ
    -----------------
    Backfill-миграция `catalog-backfill-v1` дозаполняет записи старых баз:
    типы+атрибуты, 3D GLB превью, content-хэши, shared nested, счётчики.
    Чтобы проверить её вручную, нужна «старая» база. Этот скрипт берёт
    НОРМАЛЬНУЮ тестовую базу (импортированную текущей версией плагина) и
    выборочно ломает данные ровно по критериям детекции миграции:

      A  -Attributes  удаляет import-runs + family_types + extracted_attribute_values
                      активной версии (форма «импорт старой версии без извлечения»,
                      а также регрессия #152 — 0 типов)
      B1 -BreakUnits  unit_type_id=NULL + value_text=raw-число у Double/Found (#151)
      B2 -ReadError   value_text='READERROR' у Found-значений (#153)
      C  -Glb         удаляет auto-extracted Model3D assets активного label
      D  -Hash        content_hash=NULL, hash_format_version=NULL (versions + items)
      E  -Counters    types_count/parameters_count=NULL

    Если ни один флаг не указан — применяются ВСЕ повреждения.
    Scope: только loadable, только АКТИВНАЯ версия — ровно scope миграции.

    КАК ПОЛЬЗОВАТЬСЯ (пошагово)
    ---------------------------
    1. В плагине создай тестовую БД и импортируй несколько семейств ТЕКУЩЕЙ
       версией — у них будут типы, атрибуты, GLB, хэши.
    2. ОТКЛЮЧИСЬ от БД в плагине или закрой Revit (SQLite — один writer,
       открытое подключение держит лок).
    3. Запуск:
         pwsh tools/damage-catalog-db.ps1 -DbPath "<путь>\catalog.db"
       Частично (контрольная группа остаётся здоровой для сравнения):
         pwsh tools/damage-catalog-db.ps1 -DbPath "<путь>\catalog.db" -Limit 3
       Только GLB и хэш:
         pwsh tools/damage-catalog-db.ps1 -DbPath "<путь>\catalog.db" -Glb -Hash
       Сухой прогон (ничего не пишет, печатает что было бы повреждено):
         pwsh tools/damage-catalog-db.ps1 -DbPath "<путь>\catalog.db" -WhatIf
    4. Скрипт печатает отчёт по каждому айтему и в конце — ОЖИДАЕМОЕ число
       pending-групп по SQL-детекции миграции. Подключи базу в Revit:
       в меню «Инструменты базы» должна быть «Обновить базу (N)» с тем же N.
    5. После теста базу обычно удаляют. Восстановления нет — делай копию
       catalog.db заранее, если данные жалко.

    ПАРАМЕТРЫ
    ---------
    -DbPath             Путь к catalog.db (обязателен).
    -ItemNamePattern    SQL LIKE фильтр по имени айтема (default '%').
    -Limit              Повредить только первые N подходящих айтемов (0 = все).
    -RevitMajorVersion  Версия Revit для финальной оценки pending (default 2025).
    -WhatIf             Только отчёт, без записи.
    Флаги повреждений: -Attributes -BreakUnits -ReadError -Glb -Hash -Counters.

    ЗАВИСИМОСТИ (важно, не ломать)
    ------------------------------
    sqlite3 CLI на машине нет. Скрипт грузит Microsoft.Data.Sqlite + SQLitePCLRaw
    из bin тестового проекта (Debug.R25) — тот же паттерн, что flip-db-role.ps1.
    Предусловие: хотя бы раз собран src/SmartCon.Tests (-c Debug.R25).

    ОГРАНИЧЕНИЯ
    -----------
    - НЕ запускай на БД, к которой сейчас подключён Revit.
    - System-семейства скрипт не трогает (миграция их тоже не обслуживает).
    - Терминальные маркеры хэша (-1/-2) скрипт не выставляет — миграция их
      уважает и не ретраит (это семантика hash-v2, не backfill).

    СВЯЗАННОЕ
    ---------
    - ADR-054 (движок актуализации: задачи hash/attributes/glb), docs/architecture/database-migrations.md
    - SmartCon.FamilyManager/Services/Actualization/ — SQL-детекция задач, зеркалом которой является -WhatIf отчёт
    - tools/flip-db-role.ps1 — паттерн загрузки SQLite из bin
#>
#Requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$DbPath,
    [string]$ItemNamePattern = '%',
    [int]$Limit = 0,
    [int]$RevitMajorVersion = 2025,
    [switch]$Attributes,
    [switch]$BreakUnits,
    [switch]$ReadError,
    [switch]$Glb,
    [switch]$Hash,
    [switch]$Counters,
    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'

# Если флаги не указаны — ломаем всё.
$all = -not ($Attributes -or $BreakUnits -or $ReadError -or $Glb -or $Hash -or $Counters)
if ($all) { $Attributes = $BreakUnits = $ReadError = $Glb = $Hash = $Counters = $true }

$bin = "D:\Project\dotNET\AGK-SmartCon-Pro\src\SmartCon.Tests\bin\Debug.R25\net8.0-windows"
$native = Join-Path $bin "runtimes\win-x64\native"
$env:PATH = "$native;$env:PATH"

Add-Type -Path (Join-Path $bin "SQLitePCLRaw.core.dll")
Add-Type -Path (Join-Path $bin "SQLitePCLRaw.provider.e_sqlite3.dll")
Add-Type -Path (Join-Path $bin "SQLitePCLRaw.batteries_v2.dll")
Add-Type -Path (Join-Path $bin "Microsoft.Data.Sqlite.dll")

[SQLitePCL.Batteries_V2]::Init()

if (-not (Test-Path -LiteralPath $DbPath)) { throw "DB not found: $DbPath" }

$cs = "Data Source=$DbPath;Pooling=false"
$conn = [Microsoft.Data.Sqlite.SqliteConnection]::new($cs)
$conn.Open()
try {
    [void]($conn.CreateCommand() | ForEach-Object { $_.CommandText = "PRAGMA foreign_keys = ON"; $_.ExecuteNonQuery() })

    # --- Кандидаты: loadable айтемы, АКТИВНАЯ версия (scope миграции) ---
    $sel = $conn.CreateCommand()
    $sel.CommandText = @"
        SELECT ci.id, ci.name, cv.id, cv.version_label, cv.revit_major_version
        FROM catalog_items ci
        JOIN catalog_versions cv ON cv.catalog_item_id = ci.id
             AND cv.version_label = ci.current_version_label
        WHERE ci.family_source = 'loadable' AND ci.name LIKE @p
        ORDER BY ci.name, cv.revit_major_version DESC
"@
    [void]$sel.Parameters.Add([Microsoft.Data.Sqlite.SqliteParameter]::new('@p', $ItemNamePattern))
    $candidates = @()
    $reader = $sel.ExecuteReader()
    while ($reader.Read()) {
        $candidates += [pscustomobject]@{
            ItemId = $reader.GetString(0); Name = $reader.GetString(1)
            VersionId = $reader.GetString(2); Label = $reader.GetString(3)
            Revit = $reader.GetInt32(4)
        }
    }
    $reader.Close()

    # Группируем варианты одного label: ломаем по айтему (все варианты активного label).
    $byItem = $candidates | Group-Object ItemId
    if ($Limit -gt 0) { $byItem = $byItem | Select-Object -First $Limit }
    if ($byItem.Count -eq 0) { throw "No loadable items matched pattern '$ItemNamePattern'" }

    Write-Host "=== Damaging $($byItem.Count) item(s)$(if ($WhatIf) { ' [WhatIf — no writes]' }) ==="

    $tx = $null
    if (-not $WhatIf) { $tx = $conn.BeginTransaction() }
    try {
        foreach ($itemGroup in $byItem) {
            $item = $itemGroup.Group[0]
            $done = @()

            if ($Attributes) {
                foreach ($v in $itemGroup.Group) {
                    $cmd = $conn.CreateCommand()
                    if ($tx) { $cmd.Transaction = $tx }
                    $cmd.CommandText = @"
                        DELETE FROM extracted_attribute_values WHERE catalog_item_id = @id AND version_id = @vid;
                        DELETE FROM family_types WHERE catalog_item_id = @id AND version_id = @vid;
                        DELETE FROM family_data_import_runs WHERE catalog_item_id = @id AND version_id = @vid;
"@
                    [void]$cmd.Parameters.Add([Microsoft.Data.Sqlite.SqliteParameter]::new('@id', $item.ItemId))
                    [void]$cmd.Parameters.Add([Microsoft.Data.Sqlite.SqliteParameter]::new('@vid', $v.VersionId))
                    if (-not $WhatIf) { [void]$cmd.ExecuteNonQuery() }
                }
                $done += 'A:runs+types+values deleted'
            }

            if ($BreakUnits) {
                $cmd = $conn.CreateCommand()
                if ($tx) { $cmd.Transaction = $tx }
                $cmd.CommandText = @"
                    UPDATE extracted_attribute_values
                    SET unit_type_id = NULL, value_text = CAST(value_number AS TEXT)
                    WHERE catalog_item_id = @id AND storage_type = 'Double' AND status = 'Found'
                      AND version_id IN (SELECT id FROM catalog_versions WHERE catalog_item_id = @id AND version_label = @label)
"@
                [void]$cmd.Parameters.Add([Microsoft.Data.Sqlite.SqliteParameter]::new('@id', $item.ItemId))
                [void]$cmd.Parameters.Add([Microsoft.Data.Sqlite.SqliteParameter]::new('@label', $item.Label))
                $n = if ($WhatIf) { 0 } else { $cmd.ExecuteNonQuery() }
                $done += "B1:units broken ($n rows)"
            }

            if ($ReadError) {
                $cmd = $conn.CreateCommand()
                if ($tx) { $cmd.Transaction = $tx }
                $cmd.CommandText = @"
                    UPDATE extracted_attribute_values
                    SET value_text = 'READERROR'
                    WHERE catalog_item_id = @id AND status = 'Found' AND storage_type IN ('Double', 'Integer')
                      AND version_id IN (SELECT id FROM catalog_versions WHERE catalog_item_id = @id AND version_label = @label)
"@
                [void]$cmd.Parameters.Add([Microsoft.Data.Sqlite.SqliteParameter]::new('@id', $item.ItemId))
                [void]$cmd.Parameters.Add([Microsoft.Data.Sqlite.SqliteParameter]::new('@label', $item.Label))
                $n = if ($WhatIf) { 0 } else { $cmd.ExecuteNonQuery() }
                $done += "B2:READERROR ($n rows)"
            }

            if ($Glb) {
                $cmd = $conn.CreateCommand()
                if ($tx) { $cmd.Transaction = $tx }
                $cmd.CommandText = @"
                    DELETE FROM family_assets
                    WHERE catalog_item_id = @id AND version_label = @label
                      AND asset_type = 'Model3D' AND description LIKE 'auto-extracted-preview:%'
"@
                [void]$cmd.Parameters.Add([Microsoft.Data.Sqlite.SqliteParameter]::new('@id', $item.ItemId))
                [void]$cmd.Parameters.Add([Microsoft.Data.Sqlite.SqliteParameter]::new('@label', $item.Label))
                $n = if ($WhatIf) { 0 } else { $cmd.ExecuteNonQuery() }
                $done += "C:GLB deleted ($n)"
            }

            if ($Hash) {
                $cmd = $conn.CreateCommand()
                if ($tx) { $cmd.Transaction = $tx }
                $cmd.CommandText = @"
                    UPDATE catalog_versions SET content_hash = NULL, hash_format_version = NULL
                    WHERE catalog_item_id = @id AND version_label = @label;
                    UPDATE catalog_items SET content_hash = NULL, hash_format_version = NULL WHERE id = @id;
"@
                [void]$cmd.Parameters.Add([Microsoft.Data.Sqlite.SqliteParameter]::new('@id', $item.ItemId))
                [void]$cmd.Parameters.Add([Microsoft.Data.Sqlite.SqliteParameter]::new('@label', $item.Label))
                if (-not $WhatIf) { [void]$cmd.ExecuteNonQuery() }
                $done += 'D:hash cleared'
            }

            if ($Counters) {
                $cmd = $conn.CreateCommand()
                if ($tx) { $cmd.Transaction = $tx }
                $cmd.CommandText = @"
                    UPDATE catalog_versions SET types_count = NULL, parameters_count = NULL
                    WHERE catalog_item_id = @id AND version_label = @label
"@
                [void]$cmd.Parameters.Add([Microsoft.Data.Sqlite.SqliteParameter]::new('@id', $item.ItemId))
                [void]$cmd.Parameters.Add([Microsoft.Data.Sqlite.SqliteParameter]::new('@label', $item.Label))
                if (-not $WhatIf) { [void]$cmd.ExecuteNonQuery() }
                $done += 'E:counters cleared'
            }

            Write-Host ("  {0} ({1}, Revit {2}): {3}" -f $item.Name, $item.Label, $item.Revit, ($done -join '; '))
        }

        if ($tx) { $tx.Commit() }
    }
    catch {
        if ($tx) { $tx.Rollback() }
        throw
    }

    # --- Ожидаемый pending: зеркало SQL-детекции CatalogBackfillService ---
    $check = $conn.CreateCommand()
    $check.CommandText = @"
        SELECT COUNT(*) FROM (
            SELECT cv.catalog_item_id, cv.version_label,
                   MAX(CASE WHEN cv.revit_major_version <= @maxRevit THEN 1 ELSE 0 END) AS openable,
                   MAX(CASE WHEN NOT EXISTS(SELECT 1 FROM family_data_import_runs r
                                             WHERE r.catalog_item_id = ci.id AND r.version_id = cv.id
                                               AND r.status IN ('Succeeded', 'Partial'))
                             OR NOT EXISTS(SELECT 1 FROM family_types t
                                            WHERE t.catalog_item_id = ci.id AND t.version_id = cv.id)
                             OR EXISTS(SELECT 1 FROM extracted_attribute_values v
                                        WHERE v.catalog_item_id = ci.id AND v.version_id = cv.id
                                          AND (v.value_text = 'READERROR'
                                               OR (v.storage_type = 'Double' AND v.status = 'Found' AND v.unit_type_id IS NULL)))
                             OR NOT EXISTS(SELECT 1 FROM family_assets a
                                            WHERE a.catalog_item_id = ci.id AND a.version_label = cv.version_label
                                              AND a.asset_type = 'Model3D' AND a.description LIKE 'auto-extracted-preview:%')
                             OR (cv.hash_format_version IS NULL OR cv.hash_format_version NOT IN (2, -1, -2))
                            THEN 1 ELSE 0 END) AS anyPending
            FROM catalog_versions cv
            JOIN catalog_items ci ON ci.id = cv.catalog_item_id
            JOIN family_files ff ON ff.id = cv.file_id
            WHERE ci.family_source = 'loadable'
              AND cv.version_label = ci.current_version_label
            GROUP BY cv.catalog_item_id, cv.version_label
            HAVING openable = 1 AND anyPending = 1
        )
"@
    [void]$check.Parameters.Add([Microsoft.Data.Sqlite.SqliteParameter]::new('@maxRevit', $RevitMajorVersion))
    $pending = $check.ExecuteScalar()

    Write-Host ""
    Write-Host "=== Expected backfill pending groups (Revit $RevitMajorVersion): $pending ==="
    Write-Host "В плагине: меню «Инструменты базы» → «Обновить базу» должно показать то же число."
}
finally {
    $conn.Close()
}
