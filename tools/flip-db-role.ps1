<#
.SYNOPSIS
    Меняет роль пользователя в таблице db_users тестовой/боевой SQLite-БД FamilyManager (catalog.db).

.DESCRIPTION
    === ИНСТРУКЦИЯ ДЛЯ АГЕНТА (читать целиком перед использованием) ===

    ЗАЧЕМ ЭТОТ СКРИПТ
    -----------------
    FamilyManager имеет RBAC (ADR-022): роли Owner / BimMaster / Engineer хранятся
    в таблице db_users внутри catalog.db. UI не позволяет владельцу понизить самого
    себя, поэтому протестировать сценарий "подключение read-only пользователем"
    штатными средствами нельзя. Этот скрипт — ручной тестовый инструмент:
    напрямую UPDATE-ит роль в db_users, минуя плагин.

    Типовой кейс: проверка I-14 (Mode=ReadOnly коннекшены для роли Engineer) —
    после флипа роли подключаемся к БД в Revit и убеждаемся, что дерево/поиск/
    загрузка в проект работают, а запись заблокирована.

    КАК ПОЛЬЗОВАТЬСЯ (пошагово)
    ---------------------------
    1. Пользователь создаёт/выбирает тестовую БД в плагине и хотя бы раз
       подключается к ней в Revit — иначе его записи нет в db_users
       (авто-регистрация при первом подключении, ADR-022) и скрипт упадёт
       с "UserId not found".
    2. Пользователь ОТКЛЮЧАЕТСЯ от БД в плагине или закрывает Revit.
       Это обязательно: SQLite один writer, открытое подключение держит лок.
    3. Запуск (из корня репо или откуда угодно):
         pwsh tools/flip-db-role.ps1 -DbPath "<путь>\catalog.db" -Role Engineer
       Обратно после теста:
         pwsh tools/flip-db-role.ps1 -DbPath "<путь>\catalog.db" -Role Owner
    4. Скрипт печатает всю таблицу db_users до изменения (с меткой <== target)
       и результат UPDATE — покажи вывод пользователю.

    ПАРАМЕТРЫ
    ---------
    -DbPath  Путь к catalog.db (внутри корня БД FamilyManager, рядом лежат storage/).
    -UserId  По умолчанию "$env:USERNAME@$env:COMPUTERNAME" — это и есть формат
             UserId плагина (ADR-022: "{Environment.UserName}@{Environment.MachineName}").
             Переопределяй только если нужно флипнуть ДРУГОГО пользователя,
             чей UserId виден в выводе BEFORE.
    -Role    Owner | BimMaster | Engineer.

    ЗАВИСИМОСТИ (важно, не ломать)
    ------------------------------
    sqlite3 CLI на машине нет. Скрипт грузит Microsoft.Data.Sqlite + SQLitePCLRaw
    из bin тестового проекта (Debug.R25) и добавляет runtimes\win-x64\native в PATH
    процесса, чтобы провайдер нашёл нативный e_sqlite3.dll. Если путь bin изменился
    (другая конфигурация), поправь переменную $bin в начале скрипта.
    Предусловие: хотя бы раз собран src/SmartCon.Tests (-c Debug.R25).

    ОГРАНИЧЕНИЯ / ПОДВОДНЫЕ КАМНИ
    -----------------------------
    - НЕ запускай на БД, к которой сейчас подключён Revit, — UPDATE упадёт на локе
      или создаст гонку. Сначала отключить/закрыть.
    - Скрипт НЕ трогает status (Active/Banned) — только role. Для теста бана
      меняй status вручную (UPDATE db_users SET status='Banned').
    - Назначить Owner можно только если текущий Owner понижен/удалён — иначе
      в БД окажется два Owner (плагин такое не валидирует при чтении,
      но TransferOwnership в UI рассчитан на одного). После теста верни как было.
    - Тестовую БД после проверки обычно удаляют — не запускай это на боевой
      корпоративной базе без явной просьбы пользователя.

    СВЯЗАННОЕ
    ---------
    - ADR-022 (RBAC, матрица прав, формат UserId)
    - I-14 в docs/invariants.md (DELETE journal, один writer, Mode=ReadOnly для read-only ролей)
    - LocalCatalogDatabase.SetWriteAccess / DbAccessControlService.ApplyWriteAccess —
      механизм, который этот скрипт помогает тестировать.
#>
#Requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$DbPath,
    [string]$UserId = "$env:USERNAME@$env:COMPUTERNAME",
    [Parameter(Mandatory)][ValidateSet('Owner','BimMaster','Engineer')][string]$Role
)

$ErrorActionPreference = 'Stop'

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
    $cmd = $conn.CreateCommand()
    $cmd.CommandText = "SELECT user_id, display_name, role, status FROM db_users"
    $reader = $cmd.ExecuteReader()
    Write-Host "=== db_users BEFORE ==="
    $found = $false
    while ($reader.Read()) {
        $mark = if ($reader.GetString(0) -eq $UserId) { $found = $true; ' <== target' } else { '' }
        Write-Host ("{0} | {1} | {2} | {3}{4}" -f $reader.GetString(0), $reader.GetString(1), $reader.GetString(2), $reader.GetString(3), $mark)
    }
    $reader.Close()

    if (-not $found) { throw "UserId '$UserId' not found in db_users. Open the DB in the plugin once so the user is registered, then re-run." }

    $upd = $conn.CreateCommand()
    $upd.CommandText = "UPDATE db_users SET role = @role WHERE user_id = @uid"
    [void]$upd.Parameters.Add([Microsoft.Data.Sqlite.SqliteParameter]::new('@role', $Role))
    [void]$upd.Parameters.Add([Microsoft.Data.Sqlite.SqliteParameter]::new('@uid', $UserId))
    $rows = $upd.ExecuteNonQuery()

    Write-Host "=== UPDATED $rows row(s): $UserId -> $Role ==="
}
finally {
    $conn.Close()
}
