# ADR-051: Изоляция зависимостей — ILRepack (net48) + AssemblyLoadContext (net8) + конвейер миграции пользователей

**Date:** 2026-07-17  
**Status:** accepted  
**Related:** Issue #134 (DLL hell у пользователя), #129 (System.Text.Json MissingMethodException, R21), #114 (CodePages pin), ADR-042 (HelixToolkit pins)

## Context

У пользователя SmartCon не загружался: Revit 2020–2022 — `TypeLoadException` («Method 'DisposeAsync' in type '…ServiceProvider' … does not have an implementation», сборка `Microsoft.Extensions.DependencyInjection 8.0.0.1`); Revit 2025 — `FileLoadException 0x80131621` (`CommunityToolkit.Mvvm 8.4.0.0`, «assembly with same name is already loaded»). Root cause подтверждён (см. Issue #134): на net48 все адд-ины делят один AppDomain («кто первый загрузил — того и версия»), на net8 — один default `AssemblyLoadContext`, куда нельзя загрузить вторую копию сборки с тем же simple name. Чужой адд-ин (или компонент Revit) загрузил несовместимые версии общих библиотек раньше SmartCon. Это тот же класс проблемы, что и #129, но для DI/MVVM-зависимостей «переписать без зависимостей» невозможно — нужна изоляция.

Наш собственный усилитель: `App.OnAssemblyResolve` возвращал первую уже загруженную сборку с тем же именем **игнорируя версию**.

## Decision

### 1. net48 (Revit 2019–2024): ILRepack merge в `SmartCon.Dependencies.dll`

Листовой проект `src/SmartCon.Dependencies` (multi-TFM). Сторонние «конфликтные» пакеты сшиваются ILRepack в его выходную сборку — типы получают identity `SmartCon.Dependencies`, и внешние версии перестают иметь значение.

**Правило merge (критично, проверено инцидентом при реализации):** пакет можно сшивать, только если **ни один подписанный сторонний компонент** в нашей dependency-замыкания не ссылается на него по strong name. Подписанный потребитель отклоняет merged-замену: неподписанную — `0x80131044 «Strong name required»`, подписанную с другим identity — `0x80131040 «manifest does not match»` (так HelixToolkit → `CommunityToolkit.Diagnostics 8.3.0.0/4aff` уронил 3D preview: `XamlParseException → TypeInitializationException ViewBoxNode`). Правило проверяется перечислением `GetReferencedAssemblies()` всех подписанных третьих сторон (HelixToolkit, SharpDX, Roslyn, Logging.Abstractions, Sqlite, MahApps).

**Финальный merge-список (6 пакетов, только наши неподписанные потребители):** CommunityToolkit.Mvvm, Microsoft.Extensions.DependencyInjection, Microsoft.Bcl.AsyncInterfaces, System.Text.Json, System.Text.Encodings.Web, UTF.Unknown.

**Намеренно LOOSE (оригинальные подписанные файлы в output, net48-блок в SmartCon.App.csproj):**
- CommunityToolkit.Common/.Diagnostics — HelixToolkit → 4aff 8.3.0.0 exact
- Microsoft.Extensions.DependencyInjection.Abstractions — Logging.Abstractions → adb 8.0.0.2
- System.Memory / Buffers / Unsafe / Tasks.Extensions / Numerics.Vectors — Roslyn/Helix/Sqlite (cc7b/b03f, токен-чувствительные)
- System.Text.Encoding.CodePages — Roslyn → b03f 7.0.0.0
- HelixToolkit/SharpDX/MahApps/Xaml.Behaviors — WPF (XmlnsDefinition/pack URI); SQLite — native interop; System.ValueTuple — фасад фреймворка

**Механика:**
- `Internalize=false` (типы публичны для потребителей); output подписан `SmartCon.snk` (коммит в репо, identity-стабильность; не обязательно при текущем списке, защита на будущее).
- `ProduceReferenceAssembly=false` — иначе потребители компилируются против ref/сборки ДО merge (CS0246).
- Потребители получают типы через `ProjectReference` на `SmartCon.Dependencies` (обе TFM); `PackageReference` на те же пакеты — только net8.
- `PrivateAssets=all` на merge-пакетах; у `CommunityToolkit.Mvvm` сохранён `analyzers` — его source-генераторы обязаны работать в проектах-потребителях.
- Транзитивные пути merge-пакетов обрезаны централизованно в `src/Directory.Build.targets` (`ExcludeAssets`) — иначе CS0433 и сырые dll в output.
- `OnAssemblyResolve` (net48): merge-имена — только из `SmartCon.Dependencies.dll`; если чужой адд-ин уже загрузил **более старую** версию прочей сборки — предпочитаем файл из нашей папки, удовлетворяющий запросу (exact-identity side-by-side load), вместо возврата старой.

### 2. net8 (Revit 2025+): AssemblyLoadContext через Nice3point.Revit.Toolkit

Изоляция по образцу RevitLookup 2025.0.8 (production-proven):

- `Nice3point.Revit.Toolkit` (`VersionOverride="$(RevitVersion).*"` → 2025.*; собственная ALC-реализация существует в линейке 2025.x и удалена из 2026.0.0, т.к. Revit 2026 получил нативную изоляцию). **Сам toolkit сшит ILRepack в `SmartCon.Dependencies.dll` (net8-таргет):** toolkit — это общая точка конфликта Default-контекста (любой другой адд-ин, напр. RevitLookup с toolkit 2025.0.x, загружает свою версию первым → `0x80131621` при нашей загрузке — подтверждено инцидентом). В merged-identity наш toolkit не пересекается с чужим. Механизм бриджа сохраняется: все наши entry-сборки (App + 3 модуля команд) делят одну копию toolkit из `SmartCon.Dependencies.dll` → один общий ALC.
- **Механика на Revit 2025** (подтверждено исходником `AddinLoadContext.cs` в теге 2025.0.3): базовые классы toolkit создают изолированный контекст **от пути сборки** (имя папки = имя контекста), манифест не участвует. Поэтому на Revit 2025 изоляция работает БЕЗ каких-либо изменений манифеста.
- **`ManifestSettings` — только нативная фича Revit 2026+** («Option for Add-in Dependency Isolation», API Changes 2026). На Revit 2025 тег категорически ЗАПРЕЩЁН: парсер отвергает весь манифест (`Failed to load add-in manifest file: The 'ManifestSettings' tag is incorrect`) — плагин вообще не грузится (инцидент при первой реализации, пойман журналом Revit). Nice3point SDK по той же причине вырезает этот узел для целей < 2026.
- Итог по манифестам: Revit 2025 — манифест без `ManifestSettings` (изоляция через toolkit); Revit 2026+ — манифест с `<ManifestSettings><UseRevitContext>False</UseRevitContext><ContextName>SmartCon</ContextName></ManifestSettings>` (нативная изоляция; toolkit обнаруживает не-default контекст и работает напрямую).
- `App` наследует `ExternalApplication` (toolkit) вместо `IExternalApplication`; все 6 команд наследуют `ExternalCommand` вместо `IExternalCommand`. На net48 те же классы компилируются с интерфейсами Revit — через alias-shim `CommandBase`/`AppBase` (`#if NET8_0_OR_GREATER`), тело команд вынесено в `ExecuteCore(UIApplication, out string)`.
- `EnableDynamicLoading=true` (net8) — полный `deps.json` для резолвера изолированного контекста.
- `IExternalEventHandler`/`IDockablePaneProvider` конвертации не требуют: инстанцируются нашим кодом внутри контекста. `IExternalCommandAvailability` и `IUpdater` в проекте отсутствуют.

### 3. Резолвер и детектор (обе платформы)

- `OnAssemblyResolve` (net48): имена из merge-списка обслуживаются **только** из `SmartCon.Dependencies.dll` — никогда с диска и никогда чужая версия. Прочее: уже загруженное → папка плагина. Каждое решение логируется (`AsmResolve` scope, Debug), downgrade версии — Warn с `[Action:]`.
- `DependencyGuard` (стартовый скан): `DependencyConflictAnalyzer` (Core, pure) сравнивает загруженные сборки с минимальными версиями сборки SmartCon. Пост-изоляции — чистая диагностика (Warn с путём чужого адд-ина), а не fail-fast.
- Единый источник merge-списка: `src/SmartCon.App/Resources/merged-dependencies.txt` (embedded; читается резолвером; `release.ps1` копирует его в архивы как `obsolete-files.txt`).

### 4. Self-healing `.addin` (AddinManifestHealer)

При каждом старте манифест `%APPDATA%\Autodesk\Revit\Addins\{year}\SmartCon.addin` сравнивается с эталоном (`SmartConAddinManifest.Build`, Core) и перезаписывается атомарно при расхождении. Закрывает: (а) Updater исторически не обновлял манифесты; (б) активацию ALC-изоляции у существующих пользователей без переустановки. Installer/`build-and-deploy.bat` пишут тот же шаблон (bootstrap), healer — авторитет.

### 5. Конвейер миграции пользователей

- **Updater**: копирует также `.json` (`deps.json`/`runtimeconfig.json`); удаляет файлы по `obsolete-files.txt` из staging (парсер отвергает path traversal и не-dll; применяется **только для net48-папок** — на net8 те же имена нужны loose); бэкап целевой папки в `backup\{folder}-{timestamp}` (хранит 3); валидация путей маркера (всё внутри `%APPDATA%\SmartCon`); после обновления пишет манифесты для всех годов целевой папки (шаблон продублирован — Updater self-contained, грузить SmartCon.Core.dll нельзя; ManifestSettings только для 2026+).
- **Installer**: `DeleteObsoleteDlls` (тот же список через `dontcopy` + `ExtractTemporaryFile`) на `ssInstall`; `WriteAddinFile` с `IncludeIsolation=True` только для 2026 (на 2025 тег отвергается парсером Revit).
- Пользовательские данные (`%APPDATA%\SmartCon\FamilyManager`, `%APPDATA%\AGK\SmartCon`) лежат вне папок с DLL — чистка их не задевает.

## Consequences

### Плюсы
- Класс багов «плагин не загружается из-за чужого адд-ина» устранён структурно на всех платформах: на net48 типы физически переезжают в наш identity, на net8 — в наш контекст.
- Повторное использование production-решений (RevitLookup/Nice3point) вместо собственных механизмов.
- Миграция существующих пользователей — прозрачная: Updater/installer чистят устаревшие dll, healer активирует изоляцию.
- Логи (`AsmResolve`, `DepGuard`, `AddinHealer`) делают следующий подобный кейс диагностируемым по `smartcon.log`.

### Минусы / риски
- `SmartCon.Dependencies.dll` растёт (~4 МБ) — приемлемо.
- Merge-список нужно держать синхронным в 3 местах: `ILRepack.targets`, `merged-dependencies.txt`, `Directory.Build.targets` (ExcludeAssets) — помечено комментариями; дрейф ловится сборкой (CS0433) и ручным тестом.
- Первый запуск после обновления через Updater может пройти неизолированно (старый манифест до записи нового); healer гарантирует изоляцию со второго запуска. Installer-путь изолирован сразу.
- Toolkit 2025.x на Revit 2026 — официально toolkit 2026.0.0 изоляцию убрал («moved to Revit itself»); наш R25-бинарник с toolkit 2025.x сохраняет изоляцию на 2026, нативная изоляция Revit 2026+ придёт позже — тогда toolkit-прослойку можно будет убрать.

### Verification
- Build: R19/R21/R24/R25 — 0 errors / 0 warnings.
- Tests: 2019/2019 (46 новых: manifest builder, conflict analyzer, obsolete cleaner, backup, manifest writer).
- Ручной тест (обязателен до релиза): чистая установка и обновление поверх (Updater + setup.exe) на R21 и R25; симуляция конфликта — адд-ин со старым `CommunityToolkit.Mvvm` 8.2.x рядом; проверка изоляции в логе.
