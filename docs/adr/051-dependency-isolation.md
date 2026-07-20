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

### 2. net8 (Revit 2025+): ILRepack merge (та же механика, что net48)

**Изначальный план (ALC через Nice3point.Revit.Toolkit) провалился в бою:** NuGet-сборки toolkit 2025.1.0/2025.2.0 НЕ содержат `AddinLoadContext` — Nice3point удалил собственную реализацию изоляции, т.к. Revit 2026 получил её нативно (проверено перебором всех 2025.x пакетов и подтверждено форумом Autodesk: «The version 2025.1.0 does not have context isolation»). В changelog toolkit записи о 2025.1.x/2025.2.x отсутствуют — это пост-removal сборки. Версии с рабочим ALC — только 2025.0.1–2025.0.3, но их API несовместим с 2025.2.0 (пин проверён и отвергнут: `Application` типа `ApplicationServices.Application` ломает команды). Нативная изоляция Revit 2026 на момент решения сыровата даже у Autodesk (форум: не изолирует корректно и в 2026.1).

**Принятое решение — merge на net8** (рекомендация ricaun: «the easiest option to isolate the plugin would be to ILRepack all the dependencies inside the main plugin dll»; тот же принцип, что IsRepackable у RevitLookup). На .NET Core нет strong-name enforcement уровня net48 (загрузчик биндит по simple name, конфликт возможен только при совпадении simple name в одном контексте) — поэтому merge безопасен для любых пакетов:

**net8 merge-список:** Nice3point.Revit.Toolkit (2025.2.0 — используется только ради базовых классов/ResolveHelper, его ALC не используется), CommunityToolkit.Mvvm, Microsoft.Extensions.DependencyInjection, UTF.Unknown.

**Намеренно НЕ мержим на net8:** System.Text.Json, Microsoft.Bcl.AsyncInterfaces, System.Text.Encodings.Web, System.Text.Encoding.CodePages — предоставляются shared-фреймворком .NET (проверено наличием в `dotnet/shared/Microsoft.NETCore.App`), конфликт невозможен по построению. CT.Common/Diagnostics и M.E.DI.Abstractions — loose (нужны Roslyn/HelixToolkit).

Результат: наш код вообще не запрашивает `CommunityToolkit.Mvvm`/`Microsoft.Extensions.DependencyInjection` по имени — чужая старая версия в Default-контексте (Eneca Auditor 8.2.0.0) физически не может вызвать 0x80131621. `ManifestSettings` для Revit 2026+ сохранён как бонус (нативная изоляция поверх merge).

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

### Известные безобидные предупреждения ILRepack (не дефекты)

При merge ILRepack печатает два класса WARN, оба проверены функционально (repro на Release-битах: 3D-узел, сериализация STJ, построение ServiceProvider — всё работает):

1. `Duplicate resource ILLink.Substitutions.xml` — метаданные для IL Linker (триммер .NET, у нас не используется); при merge нескольких .NET-библиотек с одноимённым ресурсом остаётся одна копия. На runtime не влияет.
2. `Method reference ... JsonConverter::set_ConverterStrategy ... modreq(IsExternalInit)` — ILRepack консервативно предупреждает, что определение `IsExternalInit` может отсутствовать в merge-наборе. Фактически тип `System.Runtime.CompilerServices.IsExternalInit` присутствует в merged-сборке (проверено на Release.R24), ссылка разрешается. К тому же init-сеттер `ConverterStrategy` вызывается только из object-initializer в пользовательском коде — наши кодовые пути его не трогают.
