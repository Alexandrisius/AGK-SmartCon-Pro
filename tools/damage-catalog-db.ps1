<#
.SYNOPSIS
    «Состаривает» тестовую БД FamilyManager (catalog.db): удаляет/ломает данные,
    которые чинят задачи движка актуализации (ADR-054, команда «Обновить базу»).

.DESCRIPTION
    === ИНСТРУКЦИЯ ДЛЯ АГЕНТА (читать целиком перед использованием) ===

    ЗАЧЕМ ЭТОТ СКРИПТ
    -----------------
    Движок актуализации (ADR-054) дозаполняет записи старых баз задачами:
    `hash-v7` (content-хэши), `attributes-v1` (типы+атрибуты), `glb-v1`
    (3D превью), `revit-category-v1`, `mini-project-marker-v1` (#189, ES
    маркер staged .rvt). Чтобы проверить их вручную, нужна «старая» база.
    Этот скрипт берёт НОРМАЛЬНУЮ тестовую базу (импортированную текущей
    версией плагина) и выборочно ломает данные ровно по критериям детекции
    задач:

      A  -Attributes  удаляет import-runs + family_types + extracted_attribute_values
                      активной версии (форма «импорт старой версии без извлечения»,
                      а также регрессия #152 — 0 типов)
      B1 -BreakUnits  unit_type_id=NULL + value_text=raw-число у Double/Found (#151)
      B2 -ReadError   value_text='READERROR' у Found-значений (#153)
      C  -Glb         удаляет auto-extracted Model3D assets активного label
      D  -Hash        content_hash=NULL, hash_format_version=NULL (versions + items)
      E  -Counters    types_count/parameters_count=NULL
      F  -MiniProjectMarker  es_marker_version=0 для ВСЕХ system-версий (#189) —
                      задача mini-project-marker-v1 становится pending

    Если ни один флаг не указан — применяются ВСЕ повреждения.
    Scope: только loadable, только АКТИВНАЯ версия — ровно scope миграции
    (режим F — единственный, кто трогает system-версии).

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
    - Loadable-режимы (A-E) system-семейства не трогают; -MiniProjectMarker
      наоборот сбрасывает es_marker_version только у system-версий (#189).
    - Терминальные маркеры хэша (-1/-2) скрипт не выставляет — задачи их
      уважают и не ретраят (семантика hash-v7, ADR-050).
    - -WhatIf выполняет повреждения ВНУТРИ транзакции, печатает реальные
      счётчики и expected-pending ПОСЛЕ повреждения, затем откатывает —
      база не меняется.

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
    [switch]$MiniProjectMarker,
    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'

# Если флаги не указаны — ломаем всё.
$all = -not ($Attributes -or $BreakUnits -or $ReadError -or $Glb -or $Hash -or $Counters -or $MiniProjectMarker)
if ($all) { $Attributes = $BreakUnits = $ReadError = $Glb = $Hash = $Counters = $MiniProjectMarker = $true }
$damageLoadable = $Attributes -or $BreakUnits -or $ReadError -or $Glb -or $Hash -or $Counters

$binCandidates = @(
    "D:\Project\dotNET\AGK-SmartCon-Pro\src\SmartCon.Tests\bin\Debug.R25\net8.0-windows",
    "D:\Project\dotNET\AGK-SmartCon-Pro\src\SmartCon.App\bin\Debug.R25\net8.0-windows\win-x64"
)
$bin = $binCandidates | Where-Object { Test-Path (Join-Path $_ "SQLitePCLRaw.core.dll") } | Select-Object -First 1
if (-not $bin) { throw "SQLitePCLRaw assemblies not found in any candidate bin: $($binCandidates -join ', ')" }
$native = Join-Path $bin "runtimes\win-x64\native"
$env:PATH = "$native;$env:PATH"

# Microsoft.Data.Sqlite.dll is resolved by the test host from the NuGet cache
# (not copied to bin) — locate the managed assembly in the split package.
$sqliteCore = Get-ChildItem "$env:USERPROFILE\.nuget\packages\microsoft.data.sqlite.core" -Directory -ErrorAction SilentlyContinue |
    Sort-Object Name -Descending | Select-Object -First 1
$managedSqlite = $null
if ($sqliteCore) {
    foreach ($tfm in @('net8.0', 'net6.0', 'netstandard2.0')) {
        $candidate = Join-Path $sqliteCore.FullName "lib\$tfm\Microsoft.Data.Sqlite.dll"
        if (Test-Path $candidate) { $managedSqlite = $candidate; break }
    }
}
if (-not $managedSqlite -and (Test-Path (Join-Path $bin "Microsoft.Data.Sqlite.dll"))) {
    $managedSqlite = Join-Path $bin "Microsoft.Data.Sqlite.dll"
}
if (-not $managedSqlite) { throw "Microsoft.Data.Sqlite.dll not found (nuget cache microsoft.data.sqlite.core or bin)" }

Add-Type -Path (Join-Path $bin "SQLitePCLRaw.core.dll")
Add-Type -Path (Join-Path $bin "SQLitePCLRaw.provider.e_sqlite3.dll")
Add-Type -Path (Join-Path $bin "SQLitePCLRaw.batteries_v2.dll")
Add-Type -Path $managedSqlite

[SQLitePCL.Batteries_V2]::Init()

if (-not (Test-Path -LiteralPath $DbPath)) { throw "DB not found: $DbPath" }

$cs = "Data Source=$DbPath;Pooling=false"
$conn = [Microsoft.Data.Sqlite.SqliteConnection]::new($cs)
$conn.Open()
try {
    [void]($conn.CreateCommand() | ForEach-Object { $_.CommandText = "PRAGMA foreign_keys = ON"; $_.ExecuteNonQuery() })

    # --- Кандидаты: loadable айтемы, АКТИВНАЯ версия (scope миграции) ---
    $byItem = @()
    if ($damageLoadable) {
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
        $byItem = @($candidates | Group-Object ItemId)
        if ($Limit -gt 0) { $byItem = @($byItem | Select-Object -First $Limit) }
        if ($byItem.Count -eq 0) { throw "No loadable items matched pattern '$ItemNamePattern'" }
    }

    Write-Host "=== Damaging $(if ($damageLoadable) { $byItem.Count } else { 0 }) loadable item(s) + system marker reset$(if ($WhatIf) { ' [WhatIf — no writes]' }) ==="

    $tx = $conn.BeginTransaction()
    $txDone = $false
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
                    [void]$cmd.ExecuteNonQuery()
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
                $n = $cmd.ExecuteNonQuery()
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
                $n = $cmd.ExecuteNonQuery()
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
                $n = $cmd.ExecuteNonQuery()
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
                [void]$cmd.ExecuteNonQuery()
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
                [void]$cmd.ExecuteNonQuery()
                $done += 'E:counters cleared'
            }

            Write-Host ("  {0} ({1}, Revit {2}): {3}" -f $item.Name, $item.Label, $item.Revit, ($done -join '; '))
        }

        if ($MiniProjectMarker) {
            # #189: reset the V28 marker column for system versions — the
            # mini-project-marker-v1 task must go pending (its ES check is
            # inside the files; the SQL detection reads only this column).
            $cmd = $conn.CreateCommand()
            if ($tx) { $cmd.Transaction = $tx }
            $cmd.CommandText = @"
                UPDATE catalog_versions SET es_marker_version = 0
                WHERE catalog_item_id IN (SELECT id FROM catalog_items WHERE family_source = 'system' AND name LIKE @p)
"@
            [void]$cmd.Parameters.Add([Microsoft.Data.Sqlite.SqliteParameter]::new('@p', $ItemNamePattern))
            $n = $cmd.ExecuteNonQuery()
            Write-Host "  [system] F:es_marker_version reset ($n rows)"
        }

        # --- Ожидаемый pending: зеркало SQL-детекции задач актуализации ---
        # Считаем ВНУТРИ транзакции: под -WhatIf отчёт показывает числа ПОСЛЕ
        # повреждения, а откат ниже возвращает базу в исходное состояние.
        $check = $conn.CreateCommand()
        $check.Transaction = $tx
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
                             OR (cv.hash_format_version IS NULL OR cv.hash_format_version NOT IN (7, -1, -2))
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

        $mkPending = $null
        if ($MiniProjectMarker) {
            $mk = $conn.CreateCommand()
            $mk.Transaction = $tx
            $mk.CommandText = @"
            SELECT COUNT(DISTINCT cv.catalog_item_id || '|' || cv.version_label)
            FROM catalog_versions cv
            JOIN catalog_items ci ON ci.id = cv.catalog_item_id
            WHERE ci.family_source = 'system' AND cv.es_marker_version = 0
"@
            $mkPending = $mk.ExecuteScalar()
        }

        if ($WhatIf) { $tx.Rollback() } else { $tx.Commit() }
        $txDone = $true
    }
    catch {
        if (-not $txDone) { try { $tx.Rollback() } catch { } }
        throw
    }

    Write-Host ""
    Write-Host "=== Expected backfill pending groups (Revit $RevitMajorVersion): $pending ==="
    Write-Host "В плагине: меню «Инструменты базы» → «Обновить базу» должно показать то же число."
    if ($null -ne $mkPending) {
        Write-Host "=== Expected mini-project-marker-v1 pending groups: $mkPending ==="
    }
}
finally {
    $conn.Close()
}
