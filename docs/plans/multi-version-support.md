# Multi-Version Support — План доработки SmartCon для Revit 2021–2025

> **Статус:** Планирование (ревью пройдено 2026-04-15, двенадцать раундов)
> **Дата создания:** 2026-04-14
> **Дата ревью:** 2026-04-15
> **Цель:** Расширить поддержку плагина SmartCon с Revit 2025 до Revit 2021–2025 (5 версий)
> **Версия плана:** v13 (с учётом замечаний двенадцати раундов рецензента — см. раздел "Журнал изменений" в конце)

---

## Контекст для нового агента

SmartCon — плагин для Autodesk Revit, написанный на .NET 8 / C# 12 / WPF. Сейчас поддерживает **только Revit 2025** (первая версия Revit на .NET 8). Задача — добавить поддержку Revit 2021, 2022, 2023, 2024.

**Проблема:** Revit 2021–2024 работают на .NET Framework 4.8, а Revit 2025 — на .NET 8. Это разные runtime'ы. Нельзя собрать один бинарник для всех версий. Нужен multi-targeting и адаптация кода.

**Единый источник правды проекта:** `docs/README.md` → `docs/invariants.md` → `docs/architecture/dependency-rule.md`. Обязательно загрузи эти файлы перед началом работы.

---

## 1. Карта версий Revit → .NET

| Revit | .NET Runtime | TFM | ElementId | ParameterType | Год выпуска |
|-------|-------------|-----|-----------|---------------|-------------|
| **2021** | .NET Framework 4.8 | `net48` | `int` (32-bit), `.IntegerValue` | enum `ParameterType` OK | 2020 |
| **2022** | .NET Framework 4.8 | `net48` | `int` (32-bit), `.IntegerValue` | Добавлен `ForgeTypeId` | 2021 |
| **2023** | .NET Framework 4.8 | `net48` | `int` (32-bit), `.IntegerValue` | `ParameterType` **удалён** | 2022 |
| **2024** | .NET Framework 4.8 | `net48` | `long` (64-bit), `.Value` | `ForgeTypeId` обязателен | 2023 |
| **2025** | .NET 8 | `net8.0-windows` | `long` (64-bit), `.Value` | `ForgeTypeId` обязателен | 2024 |

**Ключевой разрыв:**
- Revit 2021–2024 = .NET Framework 4.8 (TFM: `net48`)
- Revit 2025+ = .NET 8 (TFM: `net8.0-windows`)
- Revit 2024 = первый год с 64-bit ElementId (внутри net48)

**Следствие:** Нужно минимум **3 артефакта сборки**:
1. `SmartCon-2021-2023` — net48, RevitAPI 2021 (32-bit ElementId)
2. `SmartCon-2024` — net48, RevitAPI 2024 (64-bit ElementId)
3. `SmartCon-2025` — net8.0-windows, RevitAPI 2025

> Revit 2021, 2022, 2023 используют один и тот же RevitAPI (32-bit ElementId). Один бинарник, собранный против RevitAPI 2021, загрузится в 2021, 2022 и 2023.

---

## 2. Текущее состояние проекта

### 2.1. Структура Solution

```
src/
├── SmartCon.sln
├── Directory.Build.props          <- Глобальные настройки: net8.0-windows, C# 12
├── SmartCon.Core/                 <- Чистый C#. Модели, интерфейсы, алгоритмы
├── SmartCon.Revit/                <- Реализации через Revit API
├── SmartCon.UI/                   <- Общая WPF-библиотека: стили, контролы
├── SmartCon.App/                  <- Точка входа: IExternalApplication, Ribbon, DI
├── SmartCon.PipeConnect/          <- Модуль PipeConnect: Commands, ViewModels, Views
├── SmartCon.Tests/                <- Unit + ViewModel тесты (xUnit + Moq)
└── SmartCon.Updater/              <- Standalone EXE (net8.0, не участвует в multi-target)
```

### 2.2. Текущие Target Framework'и

Все проекты (кроме Updater) наследуют от `Directory.Build.props`:
```xml
<TargetFramework>net8.0-windows</TargetFramework>
```
Ссылки на RevitAPI — через HintPath на локальную установку:
```xml
<RevitApiPath>C:\Program Files\Autodesk\Revit 2025</RevitApiPath>
```

### 2.3. NuGet-зависимости

| Пакет | Версия | Поддержка net48? |
|-------|--------|-----------------|
| CommunityToolkit.Mvvm | 8.4.0 | Да (netstandard2.0) |
| Microsoft.Extensions.DependencyInjection | 8.0.1 | Да (net462+) |
| xUnit | 2.9.3 | Да |
| Moq | 4.20.72 | Да |
| Microsoft.NET.Test.Sdk | 17.12.0 | Да |

> **Важно:** Microsoft.Extensions.DependencyInjection **версии 9.0+** дропнула поддержку net48. Нужно оставаться на 8.x.

### 2.4. Проблемные места в коде

#### ElementId.Value (только Revit 2024+)

Проект использует `ElementId.Value` (64-bit свойство, добавлено в Revit 2024). В Revit 2021-2023 этого свойства нет — там `ElementId.IntegerValue` (int).

**Файлы с проблемными местами (полный список — см. Фаза 2.2 для детализации):**

 | Файл | Что используется | Проблема |
 |------|-----------------|----------|
 | `SmartCon.Core/Models/VirtualCtcStore.cs` | `new ElementId(long)`, `ElementId.Value` (×6) | 64-bit ctor/property |
 | `SmartCon.Core/Models/NetworkSnapshotStore.cs` | `ElementId.Value` (×4) | 64-bit property |
 | `SmartCon.Core/Models/ConnectionGraph.cs` | `ElementId.Value` (×4), `GetHashCode(ElementId)` | 64-bit property |
 | `SmartCon.Revit/Selection/ElementChainIterator.cs` | `elementId.Value` | 64-bit property |
 | `SmartCon.Revit/Parameters/RevitParameterResolver.cs` | `elementId.Value`, `symbolId.Value` (×20) | 64-bit property |
 | `SmartCon.Revit/Parameters/RevitDynamicSizeResolver.cs` | `elementId.Value` (×4) | 64-bit property |
 | `SmartCon.Revit/Parameters/RevitLookupTableService.cs` | `elementId.Value` (×6) | 64-bit property |
 | `SmartCon.Revit/Parameters/FamilyParameterAnalyzer.cs` | `.Id.Value` (×8) | 64-bit property |
 | `SmartCon.Revit/Parameters/FamilySymbolSizeExtractor.cs` | `symbolId.Value` (×2) | 64-bit property |
 | `SmartCon.Revit/Family/FittingFamilyRepository.cs` | `f.FamilyCategory?.Id.Value` | 64-bit property |
 | `SmartCon.Revit/Family/RevitFamilyConnectorService.cs` | `.Id.Value` (×3) | 64-bit property |
 | `SmartCon.Revit/Network/NetworkMover.cs` | `reducerId.Value` (×3) | 64-bit property |
 | `SmartCon.PipeConnect/Services/FittingCtcManager.cs` | `.ElementId.Value`, `.Id.Value` (×10) | 64-bit property |
 | `SmartCon.PipeConnect/Services/ChainOperationHandler.cs` | `elemId.Value`, `reducerId.Value` (×25) | 64-bit property |
 | `SmartCon.PipeConnect/Services/ConnectExecutor.cs` | `.OwnerElementId.Value`, `.Value` (×4) | 64-bit property |
 | `SmartCon.PipeConnect/Services/PipeConnectSessionBuilder.cs` | `.ElementId.Value`, `.OwnerElementId.Value` (×5) | 64-bit property |
 | `SmartCon.PipeConnect/Services/PipeConnectInitHandler.cs` | `dynId.Value` | 64-bit property |
 | `SmartCon.PipeConnect/Services/PipeConnectSizeHandler.cs` | `element.Id.Value` | 64-bit property |
 | `SmartCon.PipeConnect/Services/PipeConnectRotationHandler.cs` | `dynId.Value`, `fittingId?.Value` | 64-bit property |
 | `SmartCon.PipeConnect/Services/DynamicSizeLoader.cs` | `dynId.Value` | 64-bit property |
 | `SmartCon.PipeConnect/ViewModels/PipeConnectEditorViewModel.Insert.cs` | `_primaryReducerId.Value` (×5) | 64-bit property |
 | `SmartCon.PipeConnect/ViewModels/PipeConnectEditorViewModel.Connect.cs` | `_primaryReducerId.Value` | 64-bit property |
 | `SmartCon.Revit/Parameters/RevitParameterResolver.cs` | `new ElementId(BuiltInParameter.CONNECTOR_RADIUS)` | Работает везде |

> `new ElementId(BuiltInParameter.X)` работает на всех версиях — не требует изменений.

#### Record types

Проект активно использует `record` и `record struct` (C# 9+). Для net48 нужен Polyfill-пакет для корректной работы.

**Примеры:**
- `Vec3` — `readonly record struct`
- `ConnectionTypeCode` — `readonly record struct`
- `SizeOption`, `SizeTableRow`, `FamilyInfo` — `sealed record`
- AST-узлы FormulaEngine — `abstract record`, `sealed record`

---

## 3. Выбранный подход

### 3.1. Nice3point.Revit.Api NuGet-пакеты

Nice3point публикует RevitAPI.dll / RevitAPIUI.dll как NuGet-пакеты для каждой версии Revit. Это де-факто стандарт в сообществе Revit-разработчиков.

**Преимущества:**
- Не нужна локальная установка каждой версии Revit для сборки
- CI/CD friendly — NuGet restore вместо HintPath
- Автоматическое разрешение правильных DLL по версии
- Используется в RevitLookup, pyRevit и других крупных проектах

**Пакеты:**
- `Nice3point.Revit.Api.RevitAPI` — RevitAPI.dll
- `Nice3point.Revit.Api.RevitAPIUI` — RevitAPIUI.dll
- `Nice3point.Revit.Api.AdWindows` — AdWindows.dll (если нужен Ribbon)

> **Альтернатива (ЗП-1):** Существует мета-пакет `Nice3point.Revit.Sdk`, который подключает RevitAPI + RevitAPIUI + AdWindows одновременно. Если AdWindows используется (Ribbon), можно заменить три отдельных PackageReference на один. В текущем плане используются раздельные пакеты для большего контроля.

**Версии NuGet-пакетов по Revit:**
| Revit | Версия NuGet | TFM |
|-------|-------------|-----|
| 2021 | 2021.* | net48 |
| 2022 | 2022.* | net48 |
| 2023 | 2023.* | net48 |
| 2024 | 2024.* | net48 |
| 2025 | 2025.* | net8.0 |

### 3.2. Multi-targeting с условной компиляцией

Каждый проект (кроме Tests и Updater) собирается под два TFM:
```xml
<TargetFrameworks>net48;net8.0-windows</TargetFrameworks>
```

Различия между версиями Revit внутри одного TFM решаются через символы условной компиляции:
- `NETFRAMEWORK` — автоматически для net48
- `NET8_0` — автоматически для net8.0-windows
- `REVIT2021_OR_GREATER`, `REVIT2024_OR_GREATER` и т.д. — кастомные (см. Фазу 1.3)

### 3.3. C# latest на net48 через Polyfill

Пакет `Polyfill` (от SimonCropp) добавляет ~889 polyfill-типов для net48, позволяя использовать:
- `record` / `record struct` (C# 9)
- `init`-свойства (C# 9)
- `required`-свойства (C# 11)
- Nullable reference types attributes
- И другие современные C#-фичи

**Альтернатива:** `PolySharp` (от Sergio0694) — легче, но меньше polyfill'ов.

Оба пакета — source-only (не добавляют runtime-зависимости, только compile-time типы).

---

## 4. План доработки по фазам

---

### ФАЗА 1: Инфраструктура сборки (2-3 дня)

**Цель:** Настроить multi-targeting, заменить HintPath на NuGet, добавить Polyfill.

#### 1.1. Добавить global.json

Файл `src/global.json` — фиксирует версию .NET SDK:

```json
{
  "sdk": {
    "version": "8.0.400",
    "rollForward": "latestFeature"
  }
}
```

**Зачем:** Polyfill/PolySharp требуют Roslyn-компилятор из .NET 8 SDK для работы C# latest на net48.

#### 1.2. Создать Directory.Packages.props (Central Package Management)

Файл `src/Directory.Packages.props`:

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>

  <ItemGroup>
    <!-- MVVM Toolkit — работает на обоих TFM (netstandard2.0) -->
    <PackageVersion Include="CommunityToolkit.Mvvm" Version="8.4.0" />

    <!-- DI — НЕ ОБНОВЛЯТЬ выше 8.x! Версии 9.0+ дропнули net48.
         При апгрейде убедиться что новая версия поддерживает net48 -->
    <PackageVersion Include="Microsoft.Extensions.DependencyInjection" Version="8.0.1" />

    <!-- C# polyfill для net48 -->
    <PackageVersion Include="Polyfill" Version="10.3.0" />

    <!-- Тестирование -->
    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
    <PackageVersion Include="xunit" Version="2.9.3" />
    <PackageVersion Include="xunit.runner.visualstudio" Version="2.8.2" />
    <PackageVersion Include="Moq" Version="4.20.72" />

    <!-- JSON для net48 -->
    <PackageVersion Include="System.Text.Json" Version="8.0.5" />

    <!--
      ВНИМАНИЕ: RevitAPI NuGet НЕ указываем в PackageVersion!
      Версия RevitAPI динамическая — задаётся через wildcard $(RevitVersion).*
      прямо в PackageReference (VersionOverride) в Directory.Build.targets.
      
      CPM требует либо PackageVersion, либо VersionOverride для каждого
      PackageReference. RevitAPI использует VersionOverride с wildcard,
      поэтому PackageVersion не нужен.
    -->
  </ItemGroup>
</Project>
```

> **Важно:** RevitAPI NuGet не входит в `Directory.Packages.props` — его версия динамическая (`$(RevitVersion).*`). Версия задаётся через `VersionOverride` в `Directory.Build.targets`. В `.csproj` файлах `PackageReference` указывается **без** версии.

#### 1.3. Переписать Directory.Build.props

Файл `src/Directory.Build.props` — полная замена:

```xml
<Project>
  <!-- ============================================================ -->
  <!-- ШАГ 1: Парсим RevitVersion из имени конфигурации (.R21 → 2021) -->
  <!-- ДОЛЖНО идти ПЕРЕД TargetFrameworks, иначе $(RevitVersion) пуст -->
  <!-- КРИТИЧЕСКИЙ БАГ (v11): в v10 TargetFrameworks был раньше,     -->
  <!-- поэтому условие на RevitVersion всегда было false              -->
  <!-- ============================================================ -->
  <PropertyGroup Condition="$(Configuration.EndsWith('.R21'))">
    <RevitVersion>2021</RevitVersion>
  </PropertyGroup>
  <PropertyGroup Condition="$(Configuration.EndsWith('.R22'))">
    <RevitVersion>2022</RevitVersion>
  </PropertyGroup>
  <PropertyGroup Condition="$(Configuration.EndsWith('.R23'))">
    <RevitVersion>2023</RevitVersion>
  </PropertyGroup>
  <PropertyGroup Condition="$(Configuration.EndsWith('.R24'))">
    <RevitVersion>2024</RevitVersion>
  </PropertyGroup>
  <PropertyGroup Condition="$(Configuration.EndsWith('.R25'))">
    <RevitVersion>2025</RevitVersion>
  </PropertyGroup>

  <!-- ============================================================ -->
  <!-- ШАГ 2: Теперь RevitVersion задан — можно использовать в условии -->
  <!-- ============================================================ -->
  <PropertyGroup>
    <!-- Multi-target: net48 (Revit 2021-2024) + net8.0-windows (Revit 2025+)
         v10: БЕЗ Condition на IsTestProject — IsTestProject определяется
         в Microsoft.NET.Test.Sdk.props, который импортируется ПОСЛЕ
         Directory.Build.props. Override делается в самом .csproj через
         <TargetFrameworks> (множественное число).
         v11: RevitVersion=2025 → только net8.0-windows, чтобы
         `dotnet build -c Release.R25` без `-f` не падал на net48-ветке -->
    <TargetFrameworks Condition="'$(RevitVersion)' == '2025'">net8.0-windows</TargetFrameworks>
    <TargetFrameworks Condition="'$(RevitVersion)' != '2025'">net48;net8.0-windows</TargetFrameworks>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
    <AnalysisLevel>latest-recommended</AnalysisLevel>
    <!-- СРЕДНЕЕ (v10): MSB3277 НЕ подавлять. После первой net48-сборки изучить
         предупреждения вручную и либо устранить конфликты, либо применить ILRepack.
         2.2 (v12): при первой net48-сборке с TreatWarningsAsErrors=true возможны
         падения из-за MSB3277. Временно добавить в NoWarn для отладки, затем убрать:
         <NoWarn>$(NoWarn);MSB3277</NoWarn>
         СРЕДНИЙ-2 (v13): порядок действий:
         1. Временно: <NoWarn>$(NoWarn);MSB3277</NoWarn>
         2. dotnet build -c Release.R21 — изучить MSB3277 warnings
         3. Определить конфликтующие сборки (типичные: System.Runtime.CompilerServices.Unsafe,
            System.Buffers, Microsoft.Bcl.AsyncInterfaces)
         4. Разрешить: явный <PackageReference> с нужной версией ИЛИ ILRepack (Фаза 7.5)
         5. Убрать NoWarn -->
    <NoWarn>$(NoWarn);CA1001;CA1010;CA1305;CA1310;CA1707;CA1711;CA1716;CA1805;CA1822;CA1851;CA1852;CA1854;CA1859;CA1860;CA1861;CA1862;CA1866;CA1869</NoWarn>

    <!-- Подавить предупреждения о net48 deprecated -->
    <SuppressTfmSupportBuildWarnings>true</SuppressTfmSupportBuildWarnings>

    <!-- ============================================================ -->
    <!-- Именованные Build Configurations (паттерн Nice3point)        -->
    <!-- Суффикс .R21 → RevitVersion=2021 и т.д.                      -->
    <!-- Разработчик выбирает конфигурацию в IDE — забыть невозможно  -->
    <!-- ============================================================ -->
    <Configurations>Debug;Release</Configurations>
    <Configurations>$(Configurations);Debug.R21;Debug.R22;Debug.R23;Debug.R24;Debug.R25</Configurations>
    <Configurations>$(Configurations);Release.R21;Release.R22;Release.R23;Release.R24;Release.R25</Configurations>

    <Company>AGK</Company>
    <Product>SmartCon</Product>
    <Copyright>Copyright © AGK 2026</Copyright>
    <SmartConVersionFile>$(MSBuildThisFileDirectory)..\Version.txt</SmartConVersionFile>
    <IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>
  </PropertyGroup>

  <!-- C# polyfill: позволяет record, init, required на net48 -->
  <!-- ВНИМАНИЕ: НЕ используем GlobalPackageReference — чтобы не утекал в Tests и Updater (net8.0) -->
  <!-- СРЕДНИЙ-3 (v9): CS0436 подавление если Polyfill + CommunityToolkit конфликтуют -->
  <PropertyGroup Condition="'$(TargetFramework)' == 'net48'">
    <NoWarn>$(NoWarn);CS0436</NoWarn>
  </PropertyGroup>
  <ItemGroup Condition="'$(TargetFramework)' == 'net48'">
    <PackageReference Include="Polyfill">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers</IncludeAssets>
    </PackageReference>
  </ItemGroup>

  <!-- Версия: ReadAllText + Trim надёжнее ReadLinesFromFile (v10) -->
  <PropertyGroup>
    <SmartConVersion>$([System.IO.File]::ReadAllText('$(SmartConVersionFile)').Trim())</SmartConVersion>
    <Version>$(SmartConVersion)</Version>
    <AssemblyVersion>$(SmartConVersion).0</AssemblyVersion>
    <FileVersion>$(SmartConVersion).0</FileVersion>
    <InformationalVersion>$(SmartConVersion)</InformationalVersion>
  </PropertyGroup>
  <Target Name="PrintSmartConVersion" BeforeTargets="PrepareForBuild">
    <Message Importance="High" Text="SmartCon version: $(SmartConVersion)" />
  </Target>
</Project>
```

**Что удалено из текущего Directory.Build.props:**
- `<TargetFramework>net8.0-windows</TargetFramework>` → заменён на `<TargetFrameworks>net48;net8.0-windows</TargetFrameworks>`
- `<LangVersion>12</LangVersion>` → заменён на `<LangVersion>latest</LangVersion>` (Polyfill требует latest)
- `<RevitApiPath>` и `<RevitAddinsPath>` → удалены, больше не нужны (NuGet вместо HintPath)

> **Важно (замечание 1.1):** RevitAPI `PackageReference` **НЕ** добавлен в `Directory.Build.props` глобально — это утекло бы в `SmartCon.UI` и другие проекты, где он не нужен. Вместо этого RevitAPI подключается в отдельных `.csproj` файлах проектов, которым он реально нужен: `SmartCon.Core` (carrier types, I-09), `SmartCon.Revit`, `SmartCon.App`, `SmartCon.PipeConnect`.

> **Важно (замечание 2.1):** Polyfill подключён через `PackageReference` с `Condition="'$(TargetFramework)' == 'net48'"`, а не через `GlobalPackageReference`. Это предотвращает попадание Polyfill в `SmartCon.Tests` и `SmartCon.Updater` (оба net8.0), где он не нужен и может вызывать CS0436.

> **2.3 (v12): AdWindows.dll** — в DeployToRevit target (Фаза 4.2) AdWindows*.dll исключается из копирования, но пакет не подключён в .csproj. Grep подтвердил: `AdWindows` / `Autodesk.Windows` **не используются** в проекте. Если появятся — добавить `Nice3point.Revit.Api.AdWindows` в SmartCon.App.csproj с `ExcludeAssets=runtime` и соответствующий `VersionOverride` в Directory.Build.targets.

> **Важно (Build Configurations):** При использовании `Debug.R21` / `Release.R24` и т.д., Visual Studio / Rider показывает эти конфигурации в dropdown — разработчик выбирает нужную версию Revit одним кликом. Обычные `Debug`/`Release` (без суффикса) тоже доступны — `RevitVersion` при этом не установлен, что вызовет ошибку NuGet restore для net48 (см. валидацию в Directory.Build.targets).

> **ВЫСОКИЙ-2 (v11): Nice3point.Revit.Build.Tasks** — готовый NuGet-пакет от Nice3point, который **уже сейчас** заменяет три блока ручного кода:
> - Ручные `DefineConstants` в `Directory.Build.targets` → автоматические `REVIT2021_OR_GREATER`...`REVIT2025_OR_GREATER`
> - Ручной `DeployToRevit` MSBuild-target → `<DeployRevitAddin>true</DeployRevitAddin>`
> - Ручная настройка ILRepack → `<IsRepackable>true</IsRepackable>`
> 
> **Рекомендация:** принять решение до начала разработки. Если пакет подходит — убрать ~100 строк XML из `Directory.Build.targets` и весь DeployToRevit target. Если решено делать вручную — зафиксировать причину в ADR.
> 
> **Осторожность:** при использовании пакета убедиться, что его `AppendTargetFrameworkToOutputPath=false` не конфликтует с `release.ps1`, который ожидает артефакты в `bin/Release.R21/net48/...`.

#### 1.4. Создать Directory.Build.targets

Файл `src/Directory.Build.targets` — символы условной компиляции + wildcard VersionOverride для RevitAPI:

```xml
<Project>
  <!-- ============================================================ -->
  <!-- Символы условной компиляции                                   -->
  <!-- ============================================================ -->

  <!-- net48: кастомные символы для Revit API (NETFRAMEWORK определяется автоматически SDK) -->
  <!-- СРЕДНИЙ-1 (v9): NETFRAMEWORK убран — auto-defined by .NET SDK for net48 TFM -->

  <!-- net8.0-windows: NET8_0 + полная цепочка OR_GREATER (Revit 2025 поддерживает всё API 2021-2024) -->
  <PropertyGroup Condition="'$(TargetFramework)' == 'net8.0-windows'">
    <DefineConstants>$(DefineConstants);NET8_0;REVIT2021_OR_GREATER;REVIT2022_OR_GREATER;REVIT2023_OR_GREATER;REVIT2024_OR_GREATER;REVIT2025_OR_GREATER</DefineConstants>
  </PropertyGroup>

  <!-- net48 + конкретная версия Revit (из Configuration .R21/.R22/.../.R25 или -p:RevitVersion) -->
  <PropertyGroup Condition="'$(TargetFramework)' == 'net48' And '$(RevitVersion)' == '2024'">
    <DefineConstants>$(DefineConstants);REVIT2021_OR_GREATER;REVIT2022_OR_GREATER;REVIT2023_OR_GREATER;REVIT2024_OR_GREATER</DefineConstants>
  </PropertyGroup>
  <PropertyGroup Condition="'$(TargetFramework)' == 'net48' And '$(RevitVersion)' == '2023'">
    <DefineConstants>$(DefineConstants);REVIT2021_OR_GREATER;REVIT2022_OR_GREATER;REVIT2023_OR_GREATER</DefineConstants>
  </PropertyGroup>
  <PropertyGroup Condition="'$(TargetFramework)' == 'net48' And '$(RevitVersion)' == '2022'">
    <DefineConstants>$(DefineConstants);REVIT2021_OR_GREATER;REVIT2022_OR_GREATER</DefineConstants>
  </PropertyGroup>
  <PropertyGroup Condition="'$(TargetFramework)' == 'net48' And '$(RevitVersion)' == '2021'">
    <DefineConstants>$(DefineConstants);REVIT2021_OR_GREATER</DefineConstants>
  </PropertyGroup>

  <!-- ============================================================ -->
  <!-- Wildcard VersionOverride для RevitAPI NuGet                   -->
  <!-- Паттерн Nice3point: $(RevitVersion).* — NuGet выбирает        -->
  <!-- последний патч в рамках major version автоматически           -->
  <!-- БЛОКЕР-1 (v9): TFM-условие обязательно! RevitAPI 2021-2024   -->
  <!-- = net48 only, RevitAPI 2025 = net8.0 only. Без условия       -->
  <!-- VersionOverride применится к обоим TFM → NU1202 restore error -->
  <!-- ============================================================ -->

  <!-- net48: версия задаётся через RevitVersion (2021–2024) -->
  <ItemGroup Condition="'$(TargetFramework)' == 'net48' And '$(RevitVersion)' != ''">
    <PackageReference Update="Nice3point.Revit.Api.RevitAPI"
                      VersionOverride="$(RevitVersion).*" />
    <PackageReference Update="Nice3point.Revit.Api.RevitAPIUI"
                      VersionOverride="$(RevitVersion).*" />
  </ItemGroup>

  <!-- Fallback: RevitVersion не указан → restore не падает, ValidateRevitVersion выдаёт понятную ошибку -->
  <!-- 1.1 (v12): без этого fallback CPM не находит PackageVersion → NU1604 ДО ValidateRevitVersion -->
  <ItemGroup Condition="'$(TargetFramework)' == 'net48' And '$(RevitVersion)' == ''">
    <PackageReference Update="Nice3point.Revit.Api.RevitAPI"
                      VersionOverride="2021.*" />
    <PackageReference Update="Nice3point.Revit.Api.RevitAPIUI"
                      VersionOverride="2021.*" />
  </ItemGroup>

  <!-- net8.0-windows: всегда RevitAPI 2025, независимо от RevitVersion -->
  <!-- Update без существующего PackageReference — no-op, безопасно для SmartCon.UI -->
  <ItemGroup Condition="'$(TargetFramework)' == 'net8.0-windows'">
    <PackageReference Update="Nice3point.Revit.Api.RevitAPI"
                      VersionOverride="2025.*" />
    <PackageReference Update="Nice3point.Revit.Api.RevitAPIUI"
                      VersionOverride="2025.*" />
  </ItemGroup>

  <!-- Валидация: RevitVersion обязателен для net48 + проверка поддерживаемых значений -->
  <!-- ВЫСОКИЙ-1 (v9): явная валидация неизвестных значений (2019, 2026 и т.д.) -->
  <Target Name="ValidateRevitVersion" BeforeTargets="PrepareForBuild"
          Condition="'$(TargetFramework)' == 'net48'">
    <Error Condition="'$(RevitVersion)' == ''"
           Text="RevitVersion не указан. Используйте конфигурацию Debug.R21/.../Debug.R25 или передайте -p:RevitVersion=2021 (2022, 2023, 2024)." />
    <Error Condition="'$(RevitVersion)' != '2021' And '$(RevitVersion)' != '2022' And '$(RevitVersion)' != '2023' And '$(RevitVersion)' != '2024'"
           Text="Неизвестная версия RevitVersion=$(RevitVersion). Поддерживаемые значения для net48: 2021, 2022, 2023, 2024." />
  </Target>
</Project>
```

> **Wildcards вместо хардкода:** Версия `$(RevitVersion).*` (например `2024.*`) указывает NuGet выбрать последний доступный патч. Это устраняет необходимость вручную обновлять версии (`2024.3.3` → `2024.3.4`) при выходе новых патчей Nice3point.

> **TFM-условие (БЛОКЕР-1):** RevitAPI 2021–2024 NuGet-пакеты поддерживают **только** `net48`. RevitAPI 2025 — **только** `net8.0`. Без TFM-условия `VersionOverride` применится к обоим TFM одновременно → NU1202 restore error.

> **Нет fallback:** Если `RevitVersion` не указан (обычный `Debug`/`Release` без суффикса), `ValidateRevitVersion` выдаёт ошибку. Это защищает от молчаливого использования неправильной версии RevitAPI (проблема KO-1).

> **Валидация значений (ВЫСОКИЙ-1):** При передаче `-p:RevitVersion=2019` или `2026` — явная ошибка с перечислением поддерживаемых значений, а не непонятная ошибка NuGet.

#### 1.5. Обновить все .csproj файлы

**Общий паттерн:** убрать `<TargetFramework>` (наследуется от Directory.Build.props), убрать HintPath-ссылки на RevitAPI (подключаются через NuGet из Directory.Build.props).

**SmartCon.Core.csproj** — после изменений:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>SmartCon.Core</RootNamespace>
    <AssemblyName>SmartCon.Core</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="SmartCon.Tests" />
    <InternalsVisibleTo Include="SmartCon.Revit" />
  </ItemGroup>
  <!-- RevitAPI: compile-time only (I-09: carrier types — ElementId, XYZ).
       ExcludeAssets=runtime: DLL не копируется в output, загружается из Revit. -->
  <ItemGroup>
    <PackageReference Include="Nice3point.Revit.Api.RevitAPI">
      <ExcludeAssets>runtime</ExcludeAssets>
    </PackageReference>
  </ItemGroup>
  <!-- System.Text.Json: встроен в .NET 8 BCL. Для net48 — NuGet. (1.2 v12) -->
  <ItemGroup Condition="'$(TargetFramework)' == 'net48'">
    <PackageReference Include="System.Text.Json" />
  </ItemGroup>
</Project>
```

**SmartCon.Revit.csproj** — после изменений:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>SmartCon.Revit</RootNamespace>
    <AssemblyName>SmartCon.Revit</AssemblyName>
  </PropertyGroup>
  <!-- RevitAPI + RevitAPIUI: ExcludeAssets=runtime — DLL не копируется в output -->
  <ItemGroup>
    <PackageReference Include="Nice3point.Revit.Api.RevitAPI">
      <ExcludeAssets>runtime</ExcludeAssets>
    </PackageReference>
    <PackageReference Include="Nice3point.Revit.Api.RevitAPIUI">
      <ExcludeAssets>runtime</ExcludeAssets>
    </PackageReference>
  </ItemGroup>
  <!-- System.Text.Json: встроен в .NET 8 BCL. Для net48 — NuGet. (1.2 v12) -->
  <ItemGroup Condition="'$(TargetFramework)' == 'net48'">
    <PackageReference Include="System.Text.Json" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\SmartCon.Core\SmartCon.Core.csproj" />
  </ItemGroup>
</Project>
```

**SmartCon.UI.csproj** — после изменений:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>SmartCon.UI</RootNamespace>
    <AssemblyName>SmartCon.UI</AssemblyName>
    <UseWPF>true</UseWPF>
    <EnableWindowsTargeting>true</EnableWindowsTargeting>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="CommunityToolkit.Mvvm" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\SmartCon.Core\SmartCon.Core.csproj" />
  </ItemGroup>
</Project>
```

**SmartCon.PipeConnect.csproj** — после изменений:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>SmartCon.PipeConnect</RootNamespace>
    <AssemblyName>SmartCon.PipeConnect</AssemblyName>
    <UseWPF>true</UseWPF>
    <EnableWindowsTargeting>true</EnableWindowsTargeting>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="CommunityToolkit.Mvvm" />
  </ItemGroup>
  <!-- RevitAPI + RevitAPIUI: ExcludeAssets=runtime — DLL не копируется в output -->
  <ItemGroup>
    <PackageReference Include="Nice3point.Revit.Api.RevitAPI">
      <ExcludeAssets>runtime</ExcludeAssets>
    </PackageReference>
    <PackageReference Include="Nice3point.Revit.Api.RevitAPIUI">
      <ExcludeAssets>runtime</ExcludeAssets>
    </PackageReference>
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\SmartCon.Core\SmartCon.Core.csproj" />
    <ProjectReference Include="..\SmartCon.UI\SmartCon.UI.csproj" />
  </ItemGroup>
</Project>
```

**SmartCon.App.csproj** — после изменений:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>SmartCon.App</RootNamespace>
    <AssemblyName>SmartCon.App</AssemblyName>
    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
    <UseWPF>true</UseWPF>
    <EnableWindowsTargeting>true</EnableWindowsTargeting>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" />
  </ItemGroup>
  <!-- RevitAPI + RevitAPIUI: ExcludeAssets=runtime — DLL не копируется в output -->
  <ItemGroup>
    <PackageReference Include="Nice3point.Revit.Api.RevitAPI">
      <ExcludeAssets>runtime</ExcludeAssets>
    </PackageReference>
    <PackageReference Include="Nice3point.Revit.Api.RevitAPIUI">
      <ExcludeAssets>runtime</ExcludeAssets>
    </PackageReference>
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\SmartCon.Core\SmartCon.Core.csproj" />
    <ProjectReference Include="..\SmartCon.Revit\SmartCon.Revit.csproj" />
    <ProjectReference Include="..\SmartCon.UI\SmartCon.UI.csproj" />
    <ProjectReference Include="..\SmartCon.PipeConnect\SmartCon.PipeConnect.csproj" />
  </ItemGroup>
  <ItemGroup>
    <None Update="Resources\SmartCon.addin">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </None>
    <EmbeddedResource Include="Resources\Icons\*.png" />
  </ItemGroup>

  <!-- DeployToRevit target — см. Фазу 4.2 (определён один раз, не дублировать здесь) -->
</Project>
```

**SmartCon.Tests.csproj** — оставить `net8.0-windows` (тесты только на .NET 8):
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>SmartCon.Tests</RootNamespace>
    <AssemblyName>SmartCon.Tests</AssemblyName>
    <IsPackable>false</IsPackable>
    <!-- v10: TargetFrameworks (множественное) — явно перекрывает глобальный из Directory.Build.props.
         Singular TargetFramework не перекрывает plural в SDK-style проектах. -->
    <TargetFrameworks>net8.0-windows</TargetFrameworks>
    <!-- Tests всегда на net8.0-windows, всегда RevitAPI 2025 — игнорирует -p:RevitVersion -->
    <UseWPF>true</UseWPF>
    <EnableWindowsTargeting>true</EnableWindowsTargeting>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
    <PackageReference Include="Moq" />
  </ItemGroup>
  <!-- RevitAPI: compile-time only для создания XYZ/ElementId в тестах.
       ExcludeAssets=runtime: нативные зависимости Revit отсутствуют в CI. -->
  <ItemGroup>
    <PackageReference Include="Nice3point.Revit.Api.RevitAPI">
      <ExcludeAssets>runtime</ExcludeAssets>
    </PackageReference>
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\SmartCon.Core\SmartCon.Core.csproj" />
    <ProjectReference Include="..\SmartCon.PipeConnect\SmartCon.PipeConnect.csproj" />
  </ItemGroup>
</Project>
```

**SmartCon.Updater.csproj** — без изменений, остаётся `net8.0`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <!-- v10: TargetFrameworks (множественное) — явно перекрывает глобальный из Directory.Build.props -->
    <TargetFrameworks>net8.0</TargetFrameworks>
    <OutputType>Exe</OutputType>
    <AssemblyName>SmartCon.Updater</AssemblyName>
    <RootNamespace>SmartCon.Updater</RootNamespace>
    <PublishTrimmed>false</PublishTrimmed>
    <SelfContained>false</SelfContained>
  </PropertyGroup>
</Project>
```

#### 1.6. .NET SDK polyfill-нюанс для net48

При multi-targeting `net48;net8.0-windows` с `<LangVersion>latest</LangVersion>`:
- .NET 8 SDK Roslyn компилирует код для обоих TFM
- Polyfill добавляет типы (атрибуты и т.д.), которых нет в net48 BCL
- `record` / `record struct` компилируются нормально — это чисто компиляторная фича

**Проверка после настройки:** Запустить `dotnet build` — должно скомпилироваться для обоих TFM.

---

### ФАЗА 2: Compat-слой для ElementId (1-2 дня)

**Цель:** Абстрагировать различия 32/64-bit ElementId через helper-методы.

#### 2.1. Создать ElementIdCompat

Файл `src/SmartCon.Core/Compatibility/ElementIdCompat.cs`:

> **Почему в SmartCon.Core, а не SmartCon.Revit?** Инвариант I-09 разрешает Core ссылаться на RevitAPI.dll для **carrier-типов** (ElementId, XYZ) — см. текущий `SmartCon.Core.csproj` с `<Reference Include="RevitAPI">`. `ElementIdCompat` не вызывает Revit API — он только читает/создаёт carrier-тип `ElementId`. Поэтому размещение в Core допустимо и не нарушает I-09.

```csharp
namespace SmartCon.Core.Compatibility;

/// <summary>
/// Абстракция над ElementId для совместимости Revit 2021-2023 (32-bit) и 2024+ (64-bit).
/// Revit 2021-2023: ElementId(int), .IntegerValue
/// Revit 2024+:     ElementId(long), .Value
/// </summary>
public static class ElementIdCompat
{
#if REVIT2024_OR_GREATER
    /// <summary>Получить числовое значение ElementId (64-bit для 2024+).</summary>
    public static long GetValue(this ElementId id) => id.Value;

    /// <summary>Создать ElementId из числового значения.</summary>
    public static ElementId Create(long value) => new(value);

    /// <summary>Получить хэш-код ElementId.</summary>
    public static int GetStableHashCode(this ElementId id) => id.Value.GetHashCode();
#else
    /// <summary>Получить числовое значение ElementId (32-bit для 2021-2023).</summary>
    public static long GetValue(this ElementId id) => id.IntegerValue;

    /// <summary>Создать ElementId из числового значения.</summary>
    /// <remarks>Revit 2021-2023: ElementId — 32-bit. Значения > int.MaxValue недопустимы.</remarks>
    public static ElementId Create(long value)
    {
        if (value is < int.MinValue or > int.MaxValue)
            throw new InvalidOperationException(
                $"ElementId value {value} was serialized under Revit 2024+ (64-bit) " +
                $"and cannot be restored in Revit 2021-2023 (32-bit). Data migration required.");
        return new((int)value);
    }

    /// <summary>Получить хэш-код ElementId.</summary>
    public static int GetStableHashCode(this ElementId id) => id.IntegerValue.GetHashCode();
#endif
}
```

> **Внимание:** `ElementId.IntegerValue` устарел в Revit 2024, но `ElementId.Value` добавлен в 2024. Нельзя использовать оба в одном бинарнике — компилятор выдаст ошибку на версии, где свойства нет.

#### 2.2. Найти и заменить все использования

> **SU-3 (v7):** Исходный список в v6 был неполным — перечислены только 9 файлов, но grep показывает ~30+ мест. Ниже — полный алгоритм поиска. Список файлов обновлён после полного grep по всем проектам.

**Обязательный grep-шаг перед Фазой 2.2 (выполнить даже если кажется, что всё найдено):**

```bash
# 1. Найти все ElementId.Value обращения (основная замена)
#    Исключаем .Value на nullable (.HasValue / result!.Value / parsed.Value),
#    Dictionary .Value, NumberNode.Value, ConnectionTypeCode.Value и т.д.
rg "\.Value\b" src/ --include="*.cs" | rg "ElementId|elemId|elementId|\.Id\.Value|OwnerElementId"

# 2. Найти все new ElementId(long) — не BuiltInParameter/BuiltInCategory
rg "new ElementId\(" src/ --include="*.cs" | rg -v "BuiltInParameter|BuiltInCategory"

# 3. Найти все .IntegerValue (нет в проекте, но проверить)
rg "\.IntegerValue\b" src/ --include="*.cs"

# 4. Проверить InvalidElementId использование
rg "InvalidElementId" src/ --include="*.cs"
```

**Полный список файлов с ElementId.Value (grep от 2026-04-15):**

<details>
<summary>SmartCon.Core (10 мест)</summary>

1. **`Models/VirtualCtcStore.cs`** — 7 мест:
   - Строка 22: `elemId.Value` → `elemId.GetValue()`
   - Строка 31: `elemId.Value` → `elemId.GetValue()`
   - Строка 41: `elemId.Value` → `elemId.GetValue()`
   - Строка 56: `new ElementId(kvp.Key.ElemId)` → `ElementIdCompat.Create(kvp.Key.ElemId)`
   - Строка 76: `elemId.Value` → `elemId.GetValue()`
   - Строка 99: `oldElemId.Value` → `oldElemId.GetValue()`
   - Строка 100: `newElemId.Value` → `newElemId.GetValue()`

2. **`Models/NetworkSnapshotStore.cs`** — 4 места:
   - Строка 17: `snapshot.ElementId.Value` → `snapshot.ElementId.GetValue()`
   - Строка 20: `elementId.Value` → `elementId.GetValue()`
   - Строка 28: `elementId.Value` → `elementId.GetValue()`
   - Строка 38: `elementId.Value` → `elementId.GetValue()`

3. **`Models/ConnectionGraph.cs`** — 4 места:
   - Строка 62: `elementId.Value` → `elementId.GetValue()`
   - Строка 69: `r.NeighborElementId.Value` (×2) → `.GetValue()`
   - Строка 126: `obj.Value.GetHashCode()` → `obj.GetStableHashCode()`
</details>

<details>
<summary>SmartCon.Revit (20+ мест, преимущественно логирование)</summary>

4. **`Selection/ElementChainIterator.cs`** — 1 место:
   - Строка 50: `startElementId.Value` → `startElementId.GetValue()`

5. **`Family/FittingFamilyRepository.cs`** — 1 место:
   - Строка 41: `f.FamilyCategory?.Id.Value` → `f.FamilyCategory?.Id.GetValue()`

6. **`Parameters/RevitParameterResolver.cs`** — ~15 мест:
   - Строки 25, 78, 80, 81, 86, 92, 101, 183, 213, 308, 328, 341, 342, 348, 404, 439, 479, 480, 488, 498, 516:
   - Паттерн: `elementId.Value`, `symbolId.Value`, `fittingId.Value`, `paramId.Value`, `winnerId.Value` → все `.GetValue()`

7. **`Parameters/RevitDynamicSizeResolver.cs`** — ~4 места:
   - Строки 35, 375, 444: `elementId.Value`, `currentSymbolId?.Value` → `.GetValue()`

8. **`Parameters/RevitLookupTableService.cs`** — ~6 мест:
   - Строки 26, 52, 94, 105, 553: `elementId.Value` → `.GetValue()`

9. **`Parameters/FamilyParameterAnalyzer.cs`** — ~8 мест:
   - Строки 94, 98, 111, 115, 119, 148: `.Id.Value` на ConnectorElement/Parameter → `.Id.GetValue()`

10. **`Parameters/FamilySymbolSizeExtractor.cs`** — 2 места:
    - Строки 59, 113: `symbolId.Value` → `.GetValue()`

11. **`Family/RevitFamilyConnectorService.cs`** — 3 места:
    - Строки 141, 145, 155: `.Id.Value` → `.Id.GetValue()`

12. **`Network/NetworkMover.cs`** — 3 места:
    - Строки 58, 65, 88: `reducerId.Value` → `.GetValue()`

13. **`Fittings/RevitFittingInsertService.cs`** — логирование (`.Value` на CTC, не ElementId — ОК)
</details>

<details>
<summary>SmartCon.PipeConnect (15+ мест)</summary>

14. **`Services/FittingCtcManager.cs`** — ~10 мест:
    - Строки 63, 64, 87, 88, 159, 168, 454, 465, 582, 602: `elementId.Value`, `.Id.Value` → `.GetValue()`

15. **`Services/ChainOperationHandler.cs`** — ~25 мест:
    - Строки 47, 50, 65, 88, 89, 129, 198, 280, 290, 296, 303, 308, 333, 351, 370, 432, 528, 529, 570:
    - Паттерн: `elemId.Value`, `reducerId.Value`, `edge.Value.ParentId.Value`, `snapshot.FamilySymbolId?.Value` → `.GetValue()`
    - Внимание: `edge.Value.XXX` — это `edge` типа nullable struct, `.Value` — не ElementId! Заменять только внутренние `.XxxId.Value`.

16. **`Services/ConnectExecutor.cs`** — 4 места:
    - Строки 154, 161: `.OwnerElementId.Value`, `primaryReducerId.Value` → `.GetValue()`

17. **`Services/PipeConnectSessionBuilder.cs`** — ~5 мест:
    - Строки 51, 73, 77, 175, 189, 341, 342: `.ElementId.Value`, `.OwnerElementId.Value` → `.GetValue()`

18. **`Services/PipeConnectInitHandler.cs`** — 1 место:
    - Строка 108: `dynId.Value` → `.GetValue()`

19. **`Services/PipeConnectSizeHandler.cs`** — 1 место:
    - Строка 180: `element.Id.Value` → `.Id.GetValue()`

20. **`Services/PipeConnectRotationHandler.cs`** — 2 места:
    - Строка 30: `dynId.Value`, `fittingId?.Value`, `reducerId?.Value` → `.GetValue()`

21. **`Services/DynamicSizeLoader.cs`** — 1 место:
    - Строка 42: `dynId.Value` → `.GetValue()`

22. **`ViewModels/PipeConnectEditorViewModel.Insert.cs`** — 4 места:
    - Строки 35, 49, 82, 294, 334: `_primaryReducerId.Value`, `insertedId.Value`, `_currentFittingId?.Value` → `.GetValue()`

23. **`ViewModels/PipeConnectEditorViewModel.Connect.cs`** — 1 место:
    - Строка 55: `_primaryReducerId.Value` → `.GetValue()`
</details>

**Паттерн замены:**
- `someId.Value` (где someId — ElementId) → `someId.GetValue()`
- `new ElementId(someLong)` → `ElementIdCompat.Create(someLong)`
- `.Value.GetHashCode()` → `.GetStableHashCode()`
- **НЕ трогать:** `new ElementId(BuiltInParameter.X)` — работает на всех версиях
- **НЕ трогать:** `someNullableStruct.Value.Property` (где `.Value` — доступ к nullable struct, не ElementId.Value)
- **НЕ трогать:** `ConnectionTypeCode.Value`, `NumberNode.Value`, `result!.Value`, `kvp.Value` и т.д.

#### 2.3. Не забыть using

Во всех файлах с заменами добавить:
```csharp
using SmartCon.Core.Compatibility;
```

#### 2.4. Ручная проверка ElementIdCompat в Revit 2021–2023

> **2.4 (v12):** Ветка `#else` (`.IntegerValue`, net48) не покрывается автотестами — RevitAPI.dll требует нативных зависимостей. Обязательная ручная проверка после Фазы 2:

1. Открыть модель с трубопроводной сетью ≥ 5 элементов в Revit 2021 (или 2022, 2023)
2. Выполнить команду PipeConnect — проверить отсутствие `InvalidCastException` / `OverflowException`
3. Сохранить модель → закрыть → открыть повторно — проверить что ElementId восстанавливаются корректно
4. **Специальный тест:** открыть модель, сохранённую в Revit 2024 (64-bit ElementId), в Revit 2023 (32-bit) — проверить, что `ElementIdCompat.Create()` корректно бросает `InvalidOperationException` при overflow

#### 2.4. Ручная проверка ElementIdCompat в Revit 2021–2023

> **2.4 (v12):** Ветка `#else` (`.IntegerValue`, net48) не покрывается автотестами — RevitAPI.dll требует нативных зависимостей. Обязательная ручная проверка после Фазы 2:

1. Открыть модель с трубопроводной сетью ≥ 5 элементов в Revit 2021 (или 2022, 2023)
2. Выполнить команду PipeConnect — проверить отсутствие `InvalidCastException` / `OverflowException`
3. Сохранить модель → закрыть → открыть повторно — проверить что ElementId восстанавливаются корректно
4. **Специальный тест:** открыть модель, сохранённую в Revit 2024 (64-bit ElementId), в Revit 2023 (32-bit) — проверить, что `ElementIdCompat.Create()` корректно бросает `InvalidOperationException` при overflow

---

### ФАЗА 3: Конфигурации сборки по версиям Revit (1-2 дня)

**Цель:** Настроить отдельные конфигурации для каждой версии Revit.

> **Проблема:** Revit 2021-2023 (32-bit ElementId) и Revit 2024 (64-bit ElementId) оба работают на net48, но их RevitAPI.dll **бинарно несовместимы**. Нужны разные сборки.

#### 3.1. Подход с именованными Build Configurations

Вся логика заложена в `Directory.Build.props` (Configurations `.R21`–`.R25`) и `Directory.Build.targets` (символы компиляции + wildcard VersionOverride). Разработчик выбирает конфигурацию в IDE (например `Debug.R24`) или указывает `-c Release.R21` в CLI — `RevitVersion` извлекается автоматически.

> **Преимущество перед `-p:RevitVersion`:** В IDE конфигурация выбирается из dropdown — забыть указать версию невозможно. При обычном `Debug`/`Release` (без суффикса) сборка net48 упадёт с ошибкой валидации.

> **3.1 (v12):** При `dotnet build -c Release.R21` (без `-f`) собираются оба TFM: `net48` и `net8.0-windows`. Net8.0-артефакт идентичен Release.R25. Для локальной разработки рекомендуется всегда использовать `-f net48` с `.R21`–`.R24`, чтобы избежать лишней компиляции.

> **3.1 (v12):** При `dotnet build -c Release.R21` (без `-f`) собираются оба TFM: `net48` и `net8.0-windows`. Net8.0-артефакт идентичен Release.R25. Для локальной разработки рекомендуется всегда использовать `-f net48` с `.R21`–`.R24`, чтобы избежать лишней компиляции.

> **СРЕДНИЙ-4 (v9): ВАЖНО про R22/R23:** Конфигурации `Release.R22` и `Release.R23` существуют только для локальной отладки на Revit 2022/2023. **Единственный релизный артефакт** для Revit 2021/2022/2023 — это `Release.R21` (собранный против RevitAPI 2021, бинарно совместимый со всеми тремя версиями). Не использовать R22/R23 как release-артефакты!

> **СРЕДНИЙ-4 (v9): ВАЖНО про R22/R23:** Конфигурации `Release.R22` и `Release.R23` существуют только для локальной отладки на Revit 2022/2023. **Единственный релизный артефакт** для Revit 2021/2022/2023 — это `Release.R21` (собранный против RevitAPI 2021, бинарно совместимый со всеми тремя версиями). Не использовать R22/R23 как release-артефакты!

#### 3.2. Команды сборки

Собирать нужно стартовый проект плагина (`SmartCon.App`), а не весь `.sln` — это надёжнее, т.к. `SmartCon.Tests` имеет override `<TargetFramework>net8.0-windows</TargetFramework>` и может конфликтовать с параметром `-p:TargetFrameworks=net48` на уровне solution.

```bash
# Сборка для Revit 2021-2023 (32-bit ElementId)
dotnet build src/SmartCon.App/SmartCon.App.csproj -f net48 -c Release.R21

# Сборка для Revit 2024 (64-bit ElementId)
dotnet build src/SmartCon.App/SmartCon.App.csproj -f net48 -c Release.R24

# Сборка для Revit 2025
# v11: -f net8.0-windows больше не обязателен — Directory.Build.props
# автоматически задаёт TargetFrameworks=net8.0-windows при RevitVersion=2025.
# Но флаг -f по-прежнему допустим для явности.
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Release.R25
# или явно:
dotnet build src/SmartCon.App/SmartCon.App.csproj -f net8.0-windows -c Release.R25

# Альтернатива: через -p:RevitVersion (если конфигурация не используется)
dotnet build src/SmartCon.App/SmartCon.App.csproj -f net48 -p:RevitVersion=2021 -c Release

# Полная проверка компиляции (оба TFM, все проекты) — работает только с конфигурацией != R25
dotnet build src/SmartCon.sln -c Release.R21
```

#### 3.3. Output-папки

При multi-targeting .NET SDK создаёт TFM-подпапки внутри каждой конфигурации:

```
bin/
├── Release.R21/
│   └── net48/              ← DLL для Revit 2021-2023
│   └── net8.0-windows/     ← DLL для Revit 2025 (если не использовался -f net48)
├── Release.R24/
│   └── net48/              ← DLL для Revit 2024
│   └── net8.0-windows/     ← DLL для Revit 2025 (если не использовался -f net48)
└── Release.R25/
    └── net8.0-windows/     ← DLL для Revit 2025 (единственный TFM)
```

> **Примечание:** DLL находятся в `bin/<Configuration>/<TFM>/`, а не в `bin/<Configuration>/`. Это стандартное поведение SDK при `<TargetFrameworks>`.
bin/
├── Release.R21/
│   └── net48/              ← DLL для Revit 2021-2023
│   └── net8.0-windows/     ← DLL для Revit 2025 (если не использовался -f net48)
├── Release.R24/
│   └── net48/              ← DLL для Revit 2024
│   └── net8.0-windows/     ← DLL для Revit 2025 (если не использовался -f net48)
└── Release.R25/
    └── net8.0-windows/     ← DLL для Revit 2025 (единственный TFM)
```

> **Примечание:** DLL находятся в `bin/<Configuration>/<TFM>/`, а не в `bin/<Configuration>/`. Это стандартное поведение SDK при `<TargetFrameworks>`.

---

### ФАЗА 4: Деплой .addin файлов (1 день)

**Цель:** Автоматический деплой в правильные папки Revit для отладки.

#### 4.1. Шаблоны .addin файлов

Создать в `src/SmartCon.App/Resources/`:

```
Resources/
├── SmartCon.addin              <- Общий шаблон (для ручного деплоя)
├── SmartCon-2021.addin         <- Revit 2021
├── SmartCon-2022.addin         <- Revit 2022
├── SmartCon-2023.addin         <- Revit 2023
├── SmartCon-2024.addin         <- Revit 2024
└── SmartCon-2025.addin         <- Revit 2025
```

Содержимое .addin (пример для 2024):
```xml
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>SmartCon</Name>
    <Assembly>%APPDATA%\SmartCon\2024\SmartCon.App.dll</Assembly>
    <!-- ВАЖНО: Заменить на реальный GUID проекта SmartCon.
         Найти существующий GUID в текущем SmartCon.addin или сгенерировать новый.
         Все 5 .addin файлов должны содержать ОДИН И ТОТ ЖЕ GUID. -->
    <AddInId>A1B2C3D4-E5F6-7890-ABCD-EF1234567890</AddInId>
    <FullClassName>SmartCon.App.App</FullClassName>
    <VendorId>AGK</VendorId>
    <VendorDescription>AGK, info@agk.com</VendorDescription>
  </AddIn>
</RevitAddIns>
```

> **Важно (AddInId):** Все 5 `.addin` файлов должны иметь **одинаковый** `AddInId` — это идентификатор плагина, не установки. Revit использует его для трекинга. Путь в `<Assembly>` использует `%APPDATA%` — Revit раскрывает эту переменную при парсинге `.addin` файлов.

#### 4.2. Обновить DeployToRevit target

В `SmartCon.App.csproj` обновить post-build target для выбора правильной папки:

```xml
<Target Name="DeployToRevit" AfterTargets="Build"
        Condition="$(Configuration.StartsWith('Debug'))">
  <PropertyGroup>
    <!-- RevitVersion уже извлечён из Configuration (.R21/.R24/.R25) в Directory.Build.props -->
    <_RevitVersion Condition="'$(RevitVersion)' != ''">$(RevitVersion)</_RevitVersion>
    <_RevitVersion Condition="'$(RevitVersion)' == '' And '$(TargetFramework)' == 'net8.0-windows'">2025</_RevitVersion>
    <_AddinPath>$(AppData)\Autodesk\Revit\Addins\$(_RevitVersion)</_AddinPath>
  </PropertyGroup>
  <!-- Примечание: для локальной отладки используем отдельные папки по версиям (2021, 2022, 2023...).
       Продакшн-установщик (Фаза 7.4) использует общую папку 2021-2023.
       Это намеренное различие — не пытаться унифицировать. -->
  <!-- Примечание: для локальной отладки используем отдельные папки по версиям (2021, 2022, 2023...).
       Продакшн-установщик (Фаза 7.4) использует общую папку 2021-2023.
       Это намеренное различие — не пытаться унифицировать. -->
  <!-- Копируем ВСЕ DLL (CopyLocalLockFileAssemblies=true подтянет транзитивные зависимости),
       ИСКЛЮЧАЯ RevitAPI — он загружается из Revit процесса -->
  <ItemGroup>
    <SmartConDlls Include="$(TargetDir)*.dll"
                  Exclude="$(TargetDir)RevitAPI*.dll;$(TargetDir)AdWindows*.dll;$(TargetDir)UIAutomation*.dll" />
  </ItemGroup>
  <Copy SourceFiles="@(SmartConDlls)"
        DestinationFolder="$(_AddinPath)\SmartCon"
        SkipUnchangedFiles="true" />
  <Copy SourceFiles="$(ProjectDir)Resources\SmartCon-$(_RevitVersion).addin"
        DestinationFiles="$(_AddinPath)\SmartCon.addin"
        SkipUnchangedFiles="true" />
</Target>
```

---

### ФАЗА 5: Ревью API-совместимости (1-2 дня)

**Цель:** Проверить все обращения к Revit API на совместимость с 2021-2024.

#### 5.1. Что уже проверено и ОК

| API | Revit 2021-2023 | Revit 2024 | Revit 2025 | Действие |
|-----|-----------------|-----------|-----------|----------|
| `Connector.ConnectTo()` | OK | OK | OK | Нет |
| `doc.Create.NewFamilyInstance()` | OK | OK | OK | Нет |
| `Transaction` / `TransactionGroup` | OK | OK | OK | Нет |
| `FilteredElementCollector` | OK | OK | OK | Нет |
| `XYZ`, `Line`, `Connector` | OK | OK | OK | Нет |
| `MEPCurve`, `Pipe`, `FlexPipe` | OK | OK | OK | Нет |
| `ExternalEvent` / `IExternalEventHandler` | OK | OK | OK | Нет |
| `Selection.PickObject()` | OK | OK | OK | Нет |
| `FamilySymbol` / `FamilyType` | OK | OK | OK | Нет |
| `EditFamily` | OK | OK | OK | Нет |
| `Parameter.get/set` (строковый/числовой) | OK | OK | OK | Нет |

#### 5.2. Что требует внимания

| API | Проблема | Решение |
|-----|----------|---------|
| `ElementId.Value` | Только 2024+ | `ElementIdCompat.GetValue()` (Фаза 2) |
| `new ElementId(long)` | Только 2024+ | `ElementIdCompat.Create()` (Фаза 2) |
| `(long)BuiltInCategory.*` | `BuiltInCategory` расширен с int до long в Revit 2024. Касты `(int)category` вызовут `OverflowException` в рантайме | **Проверено grep'ом:** кастов `(int)BuiltInCategory` в проекте нет. Все использования — `(long)BuiltInCategory` или `.OfCategory()` — безопасны на всех версиях. Если появятся новые — использовать только `(long)`, никогда `(int)` |
| `FamilyCategory.Id.Value` | `.Id` возвращает `ElementId`, `.Value` только 2024+ | `FamilyCategory.Id.GetValue()` через ElementIdCompat |
| `ParameterType` enum | Удалён в 2023 | Не используется в проекте — нет проблемы |
| `BuiltInParameterGroup` | Удалён в 2025 | Не используется в проекте — нет проблемы |
| `System.Text.Json` | Встроен в .NET 8, NuGet для net48 | Добавить `<PackageReference Include="System.Text.Json" Condition="'$(TargetFramework)' == 'net48'" />` |

**Обнаруженное проблемное место:**
- `SmartCon.Revit/Family/FittingFamilyRepository.cs:41` — `f.FamilyCategory?.Id.Value == (long)BuiltInCategory.OST_PipeFitting`
  - `.Id.Value` — 64-bit, только Revit 2024+ → заменить на `.Id.GetValue()` через ElementIdCompat
  - `(long)BuiltInCategory.OST_PipeFitting` — каст корректен на всех версиях (int→long неявно), но лучше использовать ElementIdCompat для единообразия

#### 5.3. System.Text.Json для net48

Проект использует `System.Text.Json` (встроен в .NET 8). Для net48 нужно добавить NuGet.

**Проекты, использующие System.Text.Json (проверено grep'ом):**
- `SmartCon.Core` — `JsonFittingMappingRepository.cs`, `LocalizationService.cs`, `JsonUpdateSettingsRepository.cs`
- `SmartCon.Revit` — `GitHubUpdateService.cs`

> `SmartCon.Updater` тоже использует JSON, но он на `net8.0` и не участвует в multi-target.

**СРЕДНИЙ-5 (v10): Различия API на net48 vs net8**
`System.Text.Json 8.0.5` на net48 — NuGet-пакет с ограничениями:
- Отсутствуют перегрузки с `ReadOnlySpan<byte>` / `ReadOnlySpan<char>`
- Нет `IAsyncEnumerable` streaming
- Ограничена поддержка `[GeneratedJsonSerializer]` source generators

**Результат grep (2026-04-15):** Ни `Span<T>`, ни `ReadOnlySpan<T>`, ни `IAsyncEnumerable<T>`, ни `[JsonSerializable]`, ни `GeneratedJsonSerializer` — **не найдены** в проекте. System.Text.Json используется только через `JsonSerializer.Serialize/Deserialize` и `JsonDocument.Parse` — всё доступно на net48.

В `Directory.Packages.props`:
```xml
<!-- System.Text.Json: нужен только для net48.
     Для net8.0-windows встроен в BCL — НЕ добавлять PackageReference без Condition! -->
<PackageVersion Include="System.Text.Json" Version="8.0.5" />
```

В `SmartCon.Core.csproj`:
```xml
<ItemGroup Condition="'$(TargetFramework)' == 'net48'">
  <PackageReference Include="System.Text.Json" />
</ItemGroup>
```

В `SmartCon.Revit.csproj`:
```xml
<ItemGroup Condition="'$(TargetFramework)' == 'net48'">
  <PackageReference Include="System.Text.Json" />
</ItemGroup>
```

#### 5.4. ImplicitUsings на net48

При `<ImplicitUsings>enable</ImplicitUsings>` на net48 генерируется `GlobalUsing.g.cs` с:
```csharp
global using System;
global using System.Collections.Generic;
global using System.Linq;
// и т.д.
```

Это работает корректно на net48 с SDK-style проектами. Но **не будет** включать `using System.Threading.Tasks;` и некоторые другие — проверь, что все нужные using'и есть в файлах или добавлены явно.

#### 5.5. Дополнительные grep-проверки перед реализацией

Перед началом реализации выполнить:
```bash
# 1. Property access .ParameterType (удалён в Revit 2023, property, не enum)
grep -rn "\.ParameterType" src/ --include="*.cs"
# Результат: НЕ найдено — нет проблемы

# 2. .StorageType — OK на всех версиях (только для логирования)

# 3. CommunityToolkit.Mvvm + Polyfill совместимость на net48
# После dotnet build проверить папку obj/net48/generated/ на ошибки source generators
# CommunityToolkit 8.4.0 + Polyfill 10.3.0 совместимы, но проверить
```

> **ПРОПУСК-1 (из ревью):** Проверено grep'ом — `.ParameterType` property access в проекте не используется. `StorageType` используется только для логирования — OK.

> **ПРОПУСК-2 (из ревью):** CommunityToolkit.Mvvm 8.4.0 source generators + Polyfill 10.3.0 на net48 — совместимость не проверена в CI. После первого `dotnet build` для net48 проверить сгенерированный код в `obj/net48/generated/`.

#### 5.5.1. Проверка net48-недоступных API

Перед реализацией выполнить grep на типы, отсутствующие в net48 BCL:

```bash
# Span / Memory — нужен System.Memory NuGet для net48
rg "Span<|ReadOnlySpan<|Memory<|ReadOnlyMemory<" src/ --include="*.cs" \
  --glob "!**/SmartCon.Updater/**" --glob "!**/SmartCon.Tests/**"

# ValueTask — нужен System.Threading.Tasks.Extensions для net48
rg "ValueTask[^<]|ValueTask<" src/ --include="*.cs" \
  --glob "!**/SmartCon.Updater/**"

# IAsyncEnumerable — недоступен на net48 без доп. пакетов
rg "IAsyncEnumerable<" src/ --include="*.cs"

# System.Text.Json source generators — ограничены на net48
rg "GeneratedJsonSerializer|JsonSerializerContext|\[JsonSerializable" src/ --include="*.cs"
```

**Результат grep (2026-04-15):** Ни один из перечисленных типов **не найден** в проекте.
- `Span<T>`, `Memory<T>`, `ValueTask`, `IAsyncEnumerable` — не используются
- `[JsonSerializable]`, `GeneratedJsonSerializer` — не используются
- NuGet-пакеты `System.Memory` / `System.Threading.Tasks.Extensions` — **не нужны**

#### 5.6. WPF ImplicitUsings на net48

#### 5.6. Проверка Polyfill на CS0436

Polyfill теперь подключается через `PackageReference` с `Condition="'$(TargetFramework)' == 'net48'"` (Фаза 1.3), что исключает попадание в Tests и Updater (net8.0). Риск CS0436 минимален. Если предупреждения всё же возникнут — убедиться, что Condition применяется корректно:

```xml
<!-- Directory.Build.props — текущий вариант (верный) -->
<ItemGroup Condition="'$(TargetFramework)' == 'net48'">
  <PackageReference Include="Polyfill">
    <PrivateAssets>all</PrivateAssets>
    <IncludeAssets>runtime; build; native; contentfiles; analyzers</IncludeAssets>
  </PackageReference>
</ItemGroup>
```

#### 5.7. Визуальное тестирование WPF на net48 vs net8.0

> **3.2 (v12):** WPF-рендеринг на .NET Framework 4.8 и .NET 8 может отличаться. Вынесено из чеклиста в отдельный подраздел.

**Проверить после первой net48-сборки в Revit 2021 и Revit 2024:**

| Что проверить | Почему может отличаться |
|---------------|------------------------|
| Шрифты в `TextBlock` / `Label` | Разные DPI-настройки, особенно на старых машинах с Revit 2021 |
| Отступы и размеры элементов | WPF layout engine имеет минорные различия между runtimes |
| Анимации `Storyboard` | Разный timing pipeline в net48 vs net8.0 |
| `DataGrid` / виртуализация | Исторически разные дефолты |
| `BitmapImage` / иконки | DPI-aware loading может отличаться |
| `ResourceDictionary` merge | Поведение при слиянии ресурсов из SmartCon.UI |

**Views для прогона:**
- PipeConnectEditorWindow (основное окно)
- FittingCardItem / ConnectorItem (карточки фитингов)
- MappingRuleItem (правила маппинга)

#### 5.8. TLS 1.2 для net48 (GitHub API)

> **СРЕДНИЙ-1 (v13):** На .NET Framework 4.8 дефолтный протокол — TLS 1.0/1.1. GitHub с 2018 года требует TLS 1.2+. Без инициализации обновления из Revit 2021–2024 будут молча падать.

Добавить в точку входа плагина (`SmartCon.App/App.cs`, метод `OnStartup`):

```csharp
#if NETFRAMEWORK
System.Net.ServicePointManager.SecurityProtocol |=
    System.Net.SecurityProtocolType.Tls12 |
    System.Net.SecurityProtocolType.Tls13;
#endif
```

> `ServicePointManager` — глобальный для AppDomain, достаточно одного вызова при старте. Не размещать в `GitHubUpdateService`.

---

### ФАЗА 6: Обновление документации (0.5 дня)

**Цель:** Отразить изменения в документации проекта.

#### 6.1. Обновить файлы

| Файл | Что обновить |
|------|-------------|
| `docs/README.md` | Добавить информацию о поддержке Revit 2021-2025 |
| `docs/architecture/tech-stack.md` | Обновить TargetFramework, NuGet, добавить Nice3point |
| `docs/invariants.md` | Добавить инвариант I-11: "SmartCon.Core может зависеть от RevitAPI.dll только для типов-carriers (ElementId, XYZ). Вызовы методов Revit API из Core запрещены. `ElementIdCompat` — единственный допустимый класс в Core, зависящий от RevitAPI. Новые классы с RevitAPI в Core — запрещены без явного ревью архитектора." |
| `docs/architecture/solution-structure.md` | Добавить папку Compatibility в Core |
| `docs/references.md` | Добавить ссылки на Nice3point NuGet |

#### 6.2. Обновить AGENTS.md (корневой)

Добавить инструкцию для AI-агентов:
```
## Multi-version support
SmartCon поддерживает Revit 2021-2025. Multi-targeting: net48 + net8.0-windows.
- ElementId: используй ElementIdCompat.GetValue() / .Create() вместо .Value / new ElementId(long)
- RevitAPI: подключается через Nice3point.Revit.Api NuGet (wildcard версии)
- C# latest на net48: обеспечивается пакетом Polyfill
- Сборка: dotnet build -c Release.R21 (или .R22/.R23/.R24/.R25)
- Конфигурации .R21–.R25 автоматически задают RevitVersion
```

---

### ФАЗА 7: CI/CD и дистрибуция (1-2 дня)

**Цель:** Настроить автоматическую сборку всех артефактов.

#### 7.1. GitHub Actions workflow

```yaml
# .github/workflows/build.yml
name: Build All Versions

on: [push, pull_request]

jobs:
  build:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'  <!-- ЗАМЕЧАНИЕ-2 (v9): именно 8.0.x, а не 8.x — чтобы не переключиться на 9.0 -->

      # Н-1 (v11): кэш NuGet-пакетов — ускоряет CI на ~30-60 секунд
      - uses: actions/cache@v4
        with:
          path: ~/.nuget/packages
          key: nuget-${{ hashFiles('**/Directory.Packages.props', '**/*.csproj') }}
          restore-keys: nuget-  <!-- ЗАМЕЧАНИЕ-2 (v9): именно 8.0.x, а не 8.x — чтобы не переключиться на 9.0 -->

      # СРЕДНИЙ-4 (v9): Release.R21 — единственный релизный артефакт для Revit 2021/2022/2023.
      # Release.R22 и Release.R23 — только для локальной отладки на конкретной версии.
      # Не использовать R22/R23 как release-артефакты!
      - name: Build Revit 2021-2023 (net48, 32-bit, бинарник совместим с 2021/2022/2023)
        run: dotnet build src/SmartCon.App/SmartCon.App.csproj -f net48 -c Release.R21

      - name: Build Revit 2024 (net48, 64-bit)
        run: dotnet build src/SmartCon.App/SmartCon.App.csproj -f net48 -c Release.R24

      - name: Build Revit 2025 (net8.0-windows)
        run: dotnet build src/SmartCon.App/SmartCon.App.csproj -f net8.0-windows -c Release.R25

      - name: Run Tests
        run: dotnet test src/SmartCon.Tests/SmartCon.Tests.csproj -f net8.0-windows -c Release --no-build --no-build
```

#### 7.2. Адаптация release.ps1

Существующий скрипт `tools/release.ps1` собирает один артефакт. После multi-targeting нужно **3 publish + 3 ZIP**.

**Ключевые изменения в `tools/release.ps1`:**

```powershell
# --- 2. Build Release — заменить один build на три ---
Write-Step "Building all Revit versions"

# Revit 2021-2023
dotnet build $SrcDir\SmartCon.App\SmartCon.App.csproj -f net48 -c Release.R21 --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Build R2021 failed!" }

# Revit 2024
dotnet build $SrcDir\SmartCon.App\SmartCon.App.csproj -f net48 -c Release.R24 --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Build R2024 failed!" }

# Revit 2025
dotnet build $SrcDir\SmartCon.App\SmartCon.App.csproj -f net8.0-windows -c Release.R25 --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Build R2025 failed!" }

# --- 4. Publish — заменить один publish на три ---
$artifacts = @(
    @{ Name = "2021-2023"; TFM = "net48"; Config = "Release.R21"; AddinFile = "SmartCon-2021.addin" },
    @{ Name = "2024";      TFM = "net48"; Config = "Release.R24"; AddinFile = "SmartCon-2024.addin" },
    @{ Name = "2025";      TFM = "net8.0-windows"; Config = "Release.R25"; AddinFile = "SmartCon-2025.addin" }
)

$zipFiles = @()
foreach ($artifact in $artifacts) {
    $publishDir = Join-Path $ArtifactsDir "publish\$($artifact.Name)"
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

    $buildArgs = @(
        "publish", "$SrcDir\SmartCon.App\SmartCon.App.csproj",
        "-f", $artifact.TFM, "-c", $artifact.Config, "--nologo", "-v", "q", "-o", $publishDir
    )

    & dotnet @buildArgs
    if ($LASTEXITCODE -ne 0) { throw "Publish $($artifact.Name) failed!" }

    # IM-2 (v7): страховка — удалить RevitAPI/AdWindows из output (ExcludeAssets=runtime должно работать, но проверяем)
    Remove-Item "$publishDir\RevitAPI*.dll" -ErrorAction SilentlyContinue
    Remove-Item "$publishDir\AdWindows*.dll" -ErrorAction SilentlyContinue
    Remove-Item "$publishDir\UIAutomation*.dll" -ErrorAction SilentlyContinue

    $zipName = "SmartCon-$($artifact.Name)-$newVersion.zip"
    $zipPath = Join-Path $ArtifactsDir $zipName
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path "$publishDir\*" -DestinationPath $zipPath -Force
    $zipFiles += $zipPath
    Write-Ok "Created $zipName"
}

# --- 9. GitHub Release — прикрепить все ZIP ---
$releaseArgs = @("release", "create", "v$newVersion") + $zipFiles + @("--title", "SmartCon v$newVersion") + $notesFlag
```

> **Inno Setup:** Если инсталлер поддерживается, нужно решить — один инсталлер с выбором версии Revit, или три отдельных инсталлера. Рекомендуется **один инсталлер**, который определяет версию Revit через реестр и распаковывает нужные файлы.

#### 7.3. Артефакты дистрибуции

**Сборка — 3 ZIP-артефакта:**
```
dist/
├── SmartCon-2021-2023.zip    <- net48 + RevitAPI 2021 (32-bit ElementId)
├── SmartCon-2024.zip         <- net48 + RevitAPI 2024 (64-bit ElementId)
└── SmartCon-2025.zip         <- net8.0-windows + RevitAPI 2025
```

Каждый ZIP содержит полный набор DLL и `.addin` файл.

#### 7.4. Архитектура установки (Inno Setup)

**Стратегия:** Общая папка `%APPDATA%\SmartCon\` с DLL + `.addin` файлы с абсолютными путями в каждой папке Addins.

**Структура после установки:**
```
%APPDATA%\SmartCon\
├── 2021-2023\                          <- DLL-набор #1 (один экземпляр)
│   ├── SmartCon.App.dll
│   ├── SmartCon.Core.dll
│   ├── SmartCon.Revit.dll
│   ├── SmartCon.UI.dll
│   ├── SmartCon.PipeConnect.dll
│   ├── CommunityToolkit.Mvvm.dll
│   ├── Microsoft.Extensions.DependencyInjection.dll
│   └── ...
├── 2024\                               <- DLL-набор #2
│   └── ... (те же файлы, другой бинарник)
├── 2025\                               <- DLL-набор #3
│   └── ... (те же файлы, другой бинарник)
└── SmartCon.Updater.exe                <- один Updater на все версии

%APPDATA%\Autodesk\Revit\Addins\2021\SmartCon.addin  → <Assembly>%APPDATA%\SmartCon\2021-2023\SmartCon.App.dll</Assembly>
%APPDATA%\Autodesk\Revit\Addins\2022\SmartCon.addin  → <Assembly>%APPDATA%\SmartCon\2021-2023\SmartCon.App.dll</Assembly>
%APPDATA%\Autodesk\Revit\Addins\2023\SmartCon.addin  → <Assembly>%APPDATA%\SmartCon\2021-2023\SmartCon.App.dll</Assembly>
%APPDATA%\Autodesk\Revit\Addins\2024\SmartCon.addin  → <Assembly>%APPDATA%\SmartCon\2024\SmartCon.App.dll</Assembly>
%APPDATA%\Autodesk\Revit\Addins\2025\SmartCon.addin  → <Assembly>%APPDATA%\SmartCon\2025\SmartCon.App.dll</Assembly>
```

> **Важно:** Revit 2021, 2022 и 2023 **делят один набор DLL** — файлы идентичны, достаточно одной копии. `.addin` файлы в папках Addins 2021/2022/2023 ссылаются на один и тот же абсолютный путь.

**Логика Inno Setup инсталлера:**
1. Определить установленные версии Revit через реестр (`HKLM\SOFTWARE\Autodesk\Revit\<year>`)
2. Для каждой найденной версии — распаковать соответствующий DLL-набор в `%APPDATA%\SmartCon\<version>\`
3. Создать `.addin` файл в `%APPDATA%\Autodesk\Revit\Addins\<year>\` с абсолютным путём к DLL
4. Если ни одна версия Revit не найдена — предложить установить все

**Обновление (SmartCon.Updater):**
1. Определить установленные версии Revit через реестр
2. Скачать все нужные ZIP-артефакты с GitHub Release
3. Обновить DLL в `%APPDATA%\SmartCon\<version>\`

**Преимущества общей папки:**
- Один набор DLL для 2021-2023 вместо трёх копий
- Обновление в одном месте — Updater меняет файлы в `%APPDATA%\SmartCon\`
- Проще управлять версиями
- `.addin` файлы минимальные (только путь к DLL)

---

### ФАЗА 7.5: ILRepack для net48 артефакта

> **Приоритет:** Средний-высокий. На net48 DLL-конфликты реальны (Dynamo, pyRevit, Enscape и др. плагины приносят свои версии зависимостей). Оценить после первой успешной сборки net48 — если MSB3277 предупреждения появляются — внедрить ILRepack немедленно.

**Проблема:** На net48 нет `AssemblyLoadContext` — все DLL загружаются в один `AppDomain`. Если два Revit-плагина тянут разные версии одной зависимости (например `Microsoft.Extensions.DependencyInjection 8.0` vs `9.0`), возникает конфликт сборок.

**Решение:** ILRepack сливает все DLL в один `SmartCon.App.dll`, исключая конфликты версий. Официальный шаблон Nice3point использует этот подход.

**Что нужно:**

```xml
<!-- В SmartCon.App.csproj, только для net48 -->
<PackageReference Include="ILRepack" Version="2.0.44" />
<IsRepackable Condition="'$(TargetFramework)' == 'net48'">true</IsRepackable>
```

**Риски и ограничения:**
- ILRepack несовместим с WPF resource dictionaries (BAML) — нужно исключить `SmartCon.UI.dll` из repacking или проверить совместимость
- Увеличивает размер `SmartCon.App.dll` (все зависимости внутри)
- На net8.0-windows менее критично — есть изоляция через `AssemblyLoadContext`

**Рекомендация:** Не внедрять ILRepack в первой итерации multi-version. Добавить только если обнаружатся конфликты сборок у пользователей с другими плагинами.

---

### ФАЗА 8 (Future): Адаптация SmartCon.Updater для multi-version (1-2 дня)

> Эта фаза вынесена отдельно, т.к. Updater — standalone EXE и не блокирует основной план.
> Архитектура установки описана в разделе 7.4 — Updater должен следовать той же структуре.

#### 8.1. Детектирование версии Revit

Определить установленные версии Revit через реестр:
```
HKLM\SOFTWARE\Autodesk\Revit\Autodesk Revit 2021\  → InstallLocation = "C:\Program Files\Autodesk\Revit 2021\"
HKLM\SOFTWARE\Autodesk\Revit\Autodesk Revit 2022\  → InstallLocation = "C:\Program Files\Autodesk\Revit 2022\"
...
HKLM\SOFTWARE\Autodesk\Revit\Autodesk Revit 2025\  → InstallLocation = "C:\Program Files\Autodesk\Revit 2025\"
```

> **Важно:** Ключ реестра включает "Autodesk Revit" в имени — `HKLM\SOFTWARE\Autodesk\Revit\Autodesk Revit 2025\`, а не просто `2025`. На 64-bit системах ключ находится напрямую в `HKLM\SOFTWARE\`, а не в `WOW6432Node` (Revit — 64-bit приложение). Наличие установки проверяется по ключу `InstallLocation` внутри подкатегории — проверять существование самого ключа недостаточно, нужно читать `InstallLocation` и проверять, что путь существует на диске.

#### 8.2. Логика скачивания и обновления

```
Для каждой обнаруженной версии Revit:
  Revit 2021/2022/2023 → скачать SmartCon-2021-2023.zip → распаковать в %APPDATA%\SmartCon\2021-2023\
  Revit 2024           → скачать SmartCon-2024.zip      → распаковать в %APPDATA%\SmartCon\2024\
  Revit 2025           → скачать SmartCon-2025.zip      → распаковать в %APPDATA%\SmartCon\2025\
```

> Если у пользователя установлены и 2021, и 2023 — скачивается только один ZIP `SmartCon-2021-2023.zip` (DLL идентичны).

#### 8.3. Реализация

Добавить в `SmartCon.Updater`:
- Класс `RevitVersionDetector` — сканирование реестра `HKLM\SOFTWARE\Autodesk\Revit\*`
- Класс `ArtifactMapper` — маппинг Revit version → ZIP-артефакт (2021/2022/2023 → `SmartCon-2021-2023.zip`)
- Обновить логику скачивания: вместо одного ZIP — несколько по необходимости

---

## 5. Риски и ограничения

### 5.1. Высокие риски

| Риск | Вероятность | Влияние | Митигация |
|------|------------|---------|-----------|
| Nice3point NuGet перестанет обновляться | Низкая | Высокое | Fallback на локальные HintPath-ссылки |
| Различия Revit API не обнаружены при grep | Средняя | Среднее | Ручное тестирование на каждой версии |
| Polyfill конфликтует с другими пакетами | Низкая | Среднее | Проверить все NuGet-зависимости |

### 5.2. Средние риски

| Риск | Вероятность | Влияние | Митигация |
|------|------------|---------|-----------|
| WPF-окна выглядят по-разному на net48 vs net8 | Низкая | Низкое | Визуальное тестирование |
| System.Text.Json на net48 ведёт себя иначе | Низкая | Среднее | Unit-тесты сериализации |

### 5.3. Ограничения

1. **Нет рантайм-переключения версий.** Каждый бинарник привязан к конкретной версии RevitAPI. Пользователь должен установить правильный пакет.

2. **Revit 2021-2023 = один бинарник.** RevitAPI 2021-2023 бинарно совместимы. Собираем один раз против RevitAPI 2021.

3. **Тесты только на net8.0-windows.** Unit-тесты не запускаются на net48 (RevitAPI.dll требует нативные зависимости Revit). Для тестирования net48-билда — только ручное тестирование в Revit.

4. **SmartCon.Updater не участвует в multi-targeting.** Остается на net8.0. Нужно адаптировать логику определения версии Revit для скачивания правильного артефакта.

---

## 6. Чек-лист перед началом работы

- [ ] Установлен .NET 8 SDK (проверить: `dotnet --version`)
- [ ] Создать ветку `feature/multi-version-support`
- [ ] Прочитать `docs/invariants.md` и `docs/architecture/dependency-rule.md`
- [ ] **РЕШЕНИЕ ДО СТАРТА:** Nice3point.Revit.Build.Tasks (готовый пакет) vs ручной XML (Directory.Build.targets)?
      Если пакет — пропустить Фазы 1.4 (DefineConstants), 4.2 (DeployTarget), 7.5 (ILRepack) и
      заменить на инструкцию пакета. Зафиксировать выбор в ADR.
      Если вручную — продолжать по плану.
- [ ] Понять инвариант I-09: SmartCon.Core не вызывает Revit API, только типы-carriers
- [ ] Понять инвариант I-03: Транзакции — только через ITransactionService
- [ ] **Важно:** Каждый бинарник (SmartCon.Core.dll и др.) привязан к конкретной версии RevitAPI. Сборка — только через `SmartCon.App.csproj` с `-c Release.R21` (или другим .RXX). Не деплойте SmartCon.Core.dll отдельно!
- [ ] После первой успешной net48-сборки выполнить визуальный прогон всех WPF-окон в Revit 2021 и Revit 2024: шрифты, отступы, анимации, DataTemplate-ы, стили из SmartCon.UI — всё может рендериться иначе на net48 WPF.

---

## 7. Порядок выполнения фаз

```
Фаза 1 (Инфраструктура) → Фаза 2 (ElementId compat) → Фаза 3 (Конфигурации)
       ↓
Фаза 5 (API-ревью, чтение) ← может начаться параллельно с Фазой 2
       ↓
     Фаза 5 (исправления кода) ← зависит от Фазы 2 (ElementIdCompat)
       ↓
Фаза 4 (.addin деплой) → Фаза 6 (Документация) → Фаза 7 (CI/CD)
```

> **Уточнение:** Фаза 5 (ревью, чтение кода) может начаться параллельно с Фазой 2, но исправления из Фазы 5 (замена `.Value` на `.GetValue()` и т.д.) зависят от `ElementIdCompat` из Фазы 2 — их нужно применять после завершения Фазы 2.

**Рекомендуемый первый шаг:** Фаза 1.1 (global.json) + Фаза 1.3 (Directory.Build.props) + Фаза 1.4 (Directory.Build.targets) — запустить `dotnet build` и убедиться что проект компилируется для обоих TFM. Это покажет все проблемы сразу.

---

## 8. Оценка трудозатрат

| Фаза | Дни | Сложность |
|------|-----|-----------|
| 1. Инфраструктура сборки | 2-3 | Средняя |
| 2. ElementId compat-слой | 1-2 | Средняя |
| 3. Конфигурации по версиям | 1-2 | Средняя |
| 4. .addin файлы и деплой | 1 | Низкая |
| 5. Ревью API-совместимости | 1-2 | Низкая |
| 6. Обновление документации | 0.5 | Низкая |
| 7. CI/CD | 1-2 | Средняя |
| **Тестирование (каждая версия Revit)** | **2-3** | **Высокая** |
| **Итого** | **~10-15 дней** | |

---

## 9. Ссылки и ресурсы

| Ресурс | URL |
|--------|-----|
| Nice3point Revit API NuGet | https://www.nuget.org/packages/Nice3point.Revit.Api.RevitAPI |
| Nice3point Revit SDK (мета-пакет) | https://www.nuget.org/packages/Nice3point.Revit.Sdk |
| Polyfill NuGet | https://www.nuget.org/packages/Polyfill |
| PolySharp NuGet (альтернатива) | https://www.nuget.org/packages/PolySharp |
| RevitLookup (пример multi-target) | https://github.com/lookup-foundation/RevitLookup |
| pyRevit (пример multi-version) | https://github.com/pyrevitlabs/pyrevit |
| DynamoRevit (пример multi-target) | https://github.com/DynamoDS/DynamoRevit |
| Building Coder (Jeremy Tammik blog) | https://thebuildingcoder.typepad.com |
| Revit API Docs | https://www.revitapidocs.com |

---

## 10. Журнал изменений

### v2 (2026-04-14) — Исправления по результатам ревью

| # | Код | Суть исправления |
|---|-----|-----------------|
| 1 | KO-1 | Исправлена опечатка `Get StableHashCode` → `GetStableHashCode` в ElementIdCompat (Фаза 2.1) |
| 2 | KO-2 | Убраны `PackageReference Update` с `Version` (конфликтует с CPM). Заменено на `VersionOverride` в Directory.Build.targets (Фаза 1.4). RevitAPI убран из Directory.Packages.props (Фаза 1.2) |
| 3 | МН-2 | Добавлен `REVIT2024_OR_GREATER` в символы для `net8.0-windows` — без него ElementIdCompat уходил в 32-bit ветку (Фаза 1.4) |
| 4 | МН-1 | Полная цепочка `OR_GREATER` символов для каждой версии Revit (Фаза 1.4) |
| 5 | МН-3 | DeployToRevit target в Фазе 1.5 согласован с итоговой версией из Фазы 4.2 — использует `$(RevitVersion)` |
| 6 | МН-4 | Добавлен поясняющий комментарий в CI workflow (Фаза 7.1) |
| 7 | МН-5 | Добавлена проверка на CS0436 от Polyfill с инструкцией по ограничению Condition (Фаза 5.6) |
| 8 | ЗП-1 | Добавлено упоминание `Nice3point.Revit.Sdk` мета-пакета как альтернативы (раздел 3.1) |
| 9 | ЗП-2 | Добавлен раздел про WPF ImplicitUsings на net48 и GlobalUsings.cs (Фаза 5.5) |
| 10 | ЗП-3 | Добавлена Фаза 8: адаптация SmartCon.Updater для multi-version дистрибуции |

### v3 (2026-04-14) — Исправления по результатам второго ревью

| # | Суть исправления |
|---|-----------------|
| 1 | Команды сборки (Фаза 3.2): `-p:TargetFrameworks` на `.sln` заменён на `-f net48` / `-f net8.0-windows` на конкретный `SmartCon.App.csproj` — надёжнее при наличии проектов с override TFM (Tests, Updater) |
| 2 | Фаза 5.6: убран `Version="10.3.0"` из Polyfill fallback — при CPM версия задаётся только в `Directory.Packages.props`, иначе NU1008 |
| 3 | Фаза 5.2: добавлена строка про `BuiltInCategory` (64-bit в Revit 2024+) и обнаруженное проблемное место `FittingFamilyRepository.cs:41` — `f.FamilyCategory?.Id.Value` |

### v4 (2026-04-14) — Исправления по результатам третьего ревью

| # | Суть исправления |
|---|-----------------|
| 1 | RevitAPI `PackageReference` убран из `Directory.Build.props` и перенесён в отдельные `.csproj` (Core, Revit, App, PipeConnect, Tests) — не утекает в SmartCon.UI |
| 2 | Ко всем RevitAPI/RevitAPIUI PackageReference добавлен `<ExcludeAssets>runtime</ExcludeAssets>` — DLL не копируются в output |
| 3 | Polyfill: `GlobalPackageReference` заменён на `PackageReference` с `Condition="'$(TargetFramework)' == 'net48'"` — не попадает в Tests и Updater |
| 4 | CI: `dotnet test` теперь запускается с `-f net8.0-windows` и указанием конкретного `.csproj` |
| 5 | ElementIdCompat оставлен в Core с пояснением: I-09 разрешает carrier types, ElementIdCompat не вызывает Revit API |
| 6 | Диаграмма порядка фаз уточнена: Фаза 5 (исправления) зависит от Фазы 2 |
| 7 | Добавлен раздел 7.2: адаптация `tools/release.ps1` для multi-version — 3 publish + 3 ZIP артефакта |
| 8 | Добавлен раздел 7.4: архитектура установки с общей папкой `%APPDATA%\SmartCon\` + абсолютные пути в .addin. 3 DLL-набора, 5 точек установки |

### v5 (2026-04-14) — Исправления по результатам четвёртого ревью

| # | Суть исправления |
|---|-----------------|
| 1 | CI + release.ps1: добавлен явный `-p:RevitVersion=2024` вместо неявного default (KO-3/KO-4) |
| 2 | Directory.Build.targets: разделены ветки fallback (`RevitVersion==''`) и явный `RevitVersion==2024` |
| 3 | Directory.Build.targets: добавлена MSBuild Error при неизвестном RevitVersion — например `-p:RevitVersion=2019` (IM-3) |
| 4 | ElementIdCompat.Create: добавлен guard на overflow при long→int в net48 ветке (SU-1) |
| 5 | DeployToRevit target убран из Фазы 1.5 — определяется один раз в Фазе 4.2 (SU-2) |
| 6 | BuiltInCategory: проверено grep'ом — кастов `(int)BuiltInCategory` нет, обновлён статус в таблице (SU-3) |
| 7 | System.Text.Json: точный список проектов (Core + Revit) вместо "или где используется" (SU-4) |
| 8 | Фаза 6: инвариант I-11 усилен — явный запрет вызова Revit API методов из Core (SU-5) |
| 9 | .addin AddInId: пояснено, что все 5 файлов должны иметь одинаковый GUID — это идентификатор плагина (IM-4) |
| 10 | SmartCon.Tests: добавлен комментарий "игнорирует -p:RevitVersion" (KO-3) |

**Отклонённые замечания:**
- SU-6 (CommunityToolkit в Revit): проверено grep'ом — Revit не использует CommunityToolkit, не нужно
- IM-2 (net48 тесты ElementIdCompat): невозможно — RevitAPI.dll требует нативные зависимости Revit, которых нет в CI
- IM-4 (уникальный GUID на .addin): **неверно** — AddInId должен быть одинаковым для всех версий, это идентификатор плагина, не установки

### v6 (2026-04-14) — Исправления по результатам пятого ревью

| # | Суть исправления |
|---|-----------------|
| 1 | KO-1: `UseWPF` убран из Directory.Build.props → перенесён в SmartCon.UI, PipeConnect, App, Tests |
| 2 | KO-3: Tests и Updater — добавлен `<TargetFrameworks></TargetFrameworks>` для сброса глобального multi-target |
| 3 | SU-1: net8.0-windows — полная цепочка `REVIT2021_OR_GREATER` ... `REVIT2025_OR_GREATER` |
| 4 | SU-3: ElementIdCompat.Create — сообщение: "data migration required" |
| 5 | SU-4: System.Text.Json — предупреждающий комментарий в Directory.Packages.props |
| 6 | SU-5: CI — убран `--no-restore`, каждый build делает свой restore |
| 7 | SU-6: SmartCon.Tests — добавлен `<UseWPF>true</UseWPF>` |
| 8 | DeployToRevit: `*.dll` с Exclude вместо точечных шаблонов (хватает транзитивные deps) |
| 9 | .addin: `%APPDATA%\SmartCon\...` вместо захардкоженного пути |
| 10 | release.ps1: явный RevitVersion для всех артефактов |
| 11 | Реестр: исправлен путь на `HKLM\...\Autodesk Revit 2025\` |
| 12 | Добавлены grep-проверки `.ParameterType` и CommunityToolkit+Polyfill совместимости |

**Отклонённые замечания v6:**
- KO-2 (CPM + PackageReference Update): `Update` с `VersionOverride` — документированный паттерн, работает корректно

### v7 (2026-04-15) — Исправления по результатам шестого ревью

| # | Код | Суть исправления |
|---|-----|-----------------|
| 1 | KO-1 | Fallback при пустом RevitVersion — добавлен MSBuild Warning через `_RevitVersionWasEmpty` флаг. Fallback сохранён (для удобства разработки), но предупреждает |
| 2 | SU-1 | Fallback PropertyGroup теперь задаёт `RevitApiNuGetVersion` — `VersionOverride` всегда применяется корректно |
| 3 | SU-3 | Полный grep всех `.Value` на ElementId — список расширен с 9 до 23 файлов (~80+ мест вместо ~16). Добавлены детали по каждому файлу с `<details>` блоками. Добавлен обязательный grep-шаг перед реализацией |
| 4 | SU-6 | `<TargetFrameworks></TargetFrameworks>` (недокументированный хак) заменён на `Condition` в Directory.Build.props: `TargetFrameworks` задаётся только при `IsTestProject != true` и `IsUpdaterProject != true` |
| 5 | IM-4 | Команда сборки Revit 2024 в Фазе 3.2: добавлен `-p:RevitVersion=2024` (был пропущен) |
| 6 | IM-2 | release.ps1: добавлен шаг очистки `RevitAPI*.dll` / `AdWindows*.dll` из publish output (страховка при `ExcludeAssets=runtime`) |
| 7 | SU-5 | Реестр Revit: уточнено — проверять не только существование ключа, но и `InstallLocation` + существование пути на диске |

**Отклонённые замечания v7:**
- KO-2 (Core.dll привязан к RevitAPI версии): это не баг, а архитектурный факт — каждый артефакт компилируется под конкретную версию RevitAPI. План уже описывает 3 артефакта. Добавлено предупреждение в чек-лист
- SU-2 (CI cache conflicts): NuGet кеширует per-package-version — разные версии Nice3point не конфликтуют. Три `dotnet build` в CI — стандартный паттерн
- SU-4 (HttpClient HTTP/2 на net48): `HttpClient` на net48 по умолчанию использует HTTP/1.1, GitHub API корректно отвечает на HTTP/1.1. Проверено по коду `GitHubUpdateService.cs` — нет принудительной настройки протокола. Не проблема
- IM-1 (InvalidElementId helper): grep показал — `.Value == -1` / `.Value != -1` **не найдены** в проекте. Единственное использование `InvalidElementId` — прямой `==` компарер, не требует ElementIdCompat. Helper `IsInvalid()` не нужен
- IM-3 (xunit.runner.visualstudio в таблице): пакет уже есть в Directory.Packages.props. Отсутствие в таблице 2.3 — косметическое

### v8 (2026-04-15) — Рефакторинг на основе эталонного шаблона Nice3point

> **Контекст:** После изучения официального шаблона Nice3point/RevitTemplates (samples/MultiProjectSolution) выявлены три архитектурных улучшения.

| # | Суть изменения |
|---|----------------|
| 1 | **Wildcard-версии NuGet** вместо захардкоженных: `VersionOverride="$(RevitVersion).*"` вместо `2024.3.3`. Убраны все `PropertyGroup` с `RevitApiNuGetVersion` — NuGet автоматически выбирает последний патч |
| 2 | **Именованные Build Configurations** (`Debug.R21`–`Debug.R25`, `Release.R21`–`Release.R25`) вместо `-p:RevitVersion`. Парсинг через `Configuration.EndsWith('.R21')` в Directory.Build.props. Разработчик выбирает конфигурацию в IDE dropdown |
| 3 | **Убран fallback RevitVersion** — вместо молчаливого дефолта на 2024 + Warning → MSBuild Error при пустом RevitVersion для net48. Это устраняет проблему KO-1 полностью |
| 4 | Обновлены все команды сборки (Фаза 3.2): `-c Release.R21` вместо `-p:RevitVersion=2021 -c Release` |
| 5 | CI workflow (Фаза 7.1): `-c Release.RXX` вместо `-p:RevitVersion=X -c Release` |
| 6 | release.ps1 (Фаза 7.2): `$artifact.Config` вместо `-p:RevitVersion=$($artifact.RevitVersion)` |
| 7 | DeployToRevit target: `Condition="$(Configuration.StartsWith('Debug'))"` вместо `Condition="'$(Configuration)' == 'Debug'"` |
| 8 | AGENTS.md инструкция: обновлена на `-c Release.R21` |
| 9 | Добавлена Фаза 7.5 (Future): ILRepack для net48 — устраняет конфликты сборок в одном AppDomain. Не внедряется в первой итерации |
| 10 | Directory.Packages.props: обновлён комментарий про CPM + wildcard VersionOverride

### v9 (2026-04-15) — Исправления по результатам валидации (критические блокеры + приоритеты)

> **Контекст:** Независимый технический аудит выявил 2 критических блокера, которые сделают сборку невозможной.

| # | Код | Суть исправления |
|---|-----|-----------------|
| 1 | **БЛОКЕР-1** 🔴 | `VersionOverride` в `Directory.Build.targets` получил TFM-условие: `Condition="'$(TargetFramework)' == 'net48'"` для RevitVersion-based версий и `Condition="'$(TargetFramework)' == 'net8.0-windows'"` для фиксированной `2025.*`. Без этого NuGet пытался применить `2021.*` к `net8.0-windows` → NU1202 restore error |
| 2 | **БЛОКЕР-2** 🔴 | Решается БЛОКЕР-1: `net8.0-windows` ItemGroup автоматически даёт SmartCon.Tests `VersionOverride="2025.*"` |
| 3 | **ВЫСОКИЙ-1** 🟠 | `ValidateRevitVersion` target расширен: теперь проверяет не только пустоту, но и неизвестные значения (2019, 2026 и т.д.) |
| 4 | **ВЫСОКИЙ-2** 🟠 | Добавлена заметка про `Nice3point.Revit.Build.Tasks` — готовый пакет, который может заменить ручной код. Рекомендация: оценить после первой сборки |
| 5 | **ВЫСОКИЙ-3** 🟠 | `MSB3277` в `MSBuildWarningsAsMessages` оставлен с комментарием: должен быть удалён после первой успешной сборки net48. На net48 конфликты версий DLL критичны |
| 6 | **СРЕДНИЙ-1** 🟡 | Убран дублирующий `NETFRAMEWORK` символ — определяется автоматически .NET SDK для net48 TFM |
| 7 | **СРЕДНИЙ-2** 🟡 | ILRepack повышен из «Future/Низкий» в «Средний-высокий». На net48 DLL-конфликты реальны. Оценить после первой сборки |
| 8 | **СРЕДНИЙ-3** 🟡 | Добавлен `NoWarn CS0436` для net48 в Directory.Build.props — страховка от Polyfill + CommunityToolkit конфликтов |
| 9 | **СРЕДНИЙ-4** 🟡 | Явно документировано: R22/R23 — только локальные конфиги, не для релиза. Добавлен комментарий в CI workflow |
| 10 | **СРЕДНИЙ-5** 🟡 | Добавлена проверка API System.Text.Json недоступных в net48 (ReadOnlySpan, IAsyncEnumerable, source generators) |
| 11 | **ЗАМЕЧАНИЕ-1** 🟢 | I-11 усилен: `ElementIdCompat` — единственный допустимый класс в Core с RevitAPI-зависимостью |
| 12 | **ЗАМЕЧАНИЕ-2** 🟢 | CI: подтверждён `dotnet-version: '8.0.x'` (не `'8.x'`) |

### v10 (2026-04-15) — Исправления по результатам девятого раунда (критический MSBuild-баг + серьёзные)

> **Контекст:** Независимый аудит выявил критический баг в механизме MSBuild-изоляции тест-проектов.

| # | Код | Суть исправления |
|---|-----|-----------------|
| 1 | **КРИТИЧЕСКИЙ** 🔴 | `IsTestProject` / `IsUpdaterProject` условия в `Directory.Build.props` **НЕ РАБОТАЮТ** — `IsTestProject` определяется в `Microsoft.NET.Test.Sdk.props`, который импортируется ПОСЛЕ `Directory.Build.props`. Убрано `Condition` — `TargetFrameworks=net48;net8.0-windows` теперь безусловный. Override в `.csproj` через `<TargetFrameworks>` (множественное число) |
| 2 | **КРИТИЧЕСКИЙ** 🔴 | `SmartCon.Tests.csproj`: `<TargetFramework>` → `<TargetFrameworks>net8.0-windows</TargetFrameworks>` (plural перекрывает plural). Убран `IsTestProject` — больше не нужен |
| 3 | **КРИТИЧЕСКИЙ** 🔴 | `SmartCon.Updater.csproj`: `<TargetFramework>` → `<TargetFrameworks>net8.0</TargetFrameworks>`. Убран `IsUpdaterProject` — больше не нужен |
| 4 | **СЕРЬЁЗНОЕ** 🟠 | Добавлено условие в `Directory.Build.props`: `RevitVersion=2025` → только `net8.0-windows`. `dotnet build -c Release.R25` без `-f` больше не падает на net48-ветке |
| 5 | **СЕРЬЁЗНОЕ** 🟠 | Фаза 3.2: добавлено предупреждение — `-f net8.0-windows` ОБЯЗАТЕЛЕН для Release.R25 |
| 6 | **СЕРЬЁЗНОЕ** 🟠 | Добавлен grep на `Span<T>`, `Memory<T>`, `ValueTask`, `IAsyncEnumerable`, `[JsonSerializable]` — **не найдено** в проекте. NuGet `System.Memory` / `System.Threading.Tasks.Extensions` не нужны |
| 7 | **СРЕДНЕЕ** 🟡 | `MSB3277` **убран** из `MSBuildWarningsAsMessages` — не подавлять, а изучить после первой сборки |
| 8 | **СРЕДНЕЕ** 🟡 | `ReadLinesFromFile` заменён на `$([System.IO.File]::ReadAllText(...).Trim())` — надёжнее при trailing newline |
| 9 | **СРЕДНЕЕ** 🟡 | Добавлена TLS 1.2 инициализация для net48 в чек-лист (`ServicePointManager.SecurityProtocol`) |
| 10 | **МАЛОЕ** 🟢 | `.addin` GUID placeholder: добавлен комментарий с инструкцией заменить на реальный GUID |
| 11 | **МАЛОЕ** 🟢 | Чек-лист: добавлен пункт про визуальное тестирование WPF на Revit 2021 и 2024

### v11 (2026-04-15) — Исправления по результатам десятого раунда (порядок свойств MSBuild)

> **Контекст:** Независимый аудит выявил критический баг порядка вычисления свойств в `Directory.Build.props`.

| # | Код | Суть исправления |
|---|-----|-----------------|
| 1 | **КРИТИЧЕСКИЙ** 🔴 | **Порядок PropertyGroups в Directory.Build.props:** парсинг `RevitVersion` перемещён ДО `TargetFrameworks`. В v10 `TargetFrameworks` с условием на `$(RevitVersion)` обрабатывался раньше, чем `RevitVersion` задавался из конфигурации → условие всегда было false → оба TFM всегда запускались → `Release.R25` без `-f` падал |
| 2 | **СРЕДНЕЕ** 🟠 | Фаза 3.2: убрано предупреждение «флаг -f ОБЯЗАТЕЛЕН» — после исправления бага `dotnet build -c Release.R25` без `-f` работает корректно |
| 3 | **СРЕДНЕЕ** 🟠 | Фаза 3.3: добавлена явная структура TFM-подпапок (`bin/Release.R21/net48/`) |
| 4 | **СРЕДНЕЕ** 🟠 | DeployToRevit: добавлен комментарий о различии путей локального деплоя (отдельные папки) и продакшн-установки (общая папка 2021-2023) |
| 5 | **СРЕДНЕЕ** 🟠 | ВЫСОКИЙ-2: `Nice3point.Revit.Build.Tasks` — обновлена рекомендация: принять решение ДО начала разработки, а не «оценить после первой сборки» |
| 6 | **МАЛОЕ** 🟢 | CI: добавлен шаг кэширования NuGet-пакетов (`actions/cache@v4`)

### v12 (2026-04-15) — Исправления по результатам одиннадцатого раунда (последние штрихи)

> **Контекст:** Финальное ревью перед передачей в разработку. 2 критических, 4 значимых, 4 информационных замечания.

| # | Код | Суть исправления |
|---|-----|-----------------|
| 1 | **1.1 КРИТИЧЕСКИЙ** 🔴 | Добавлен fallback `VersionOverride="2021.*"` для net48 при пустом `RevitVersion`. Без него CPM не находит PackageVersion → NU1604 ДО срабатывания `ValidateRevitVersion` |
| 2 | **1.2 КРИТИЧЕСКИЙ** 🔴 | `System.Text.Json` добавлен в примеры `SmartCon.Core.csproj` и `SmartCon.Revit.csproj` (Фаза 1.5) с `Condition="'$(TargetFramework)' == 'net48'"` |
| 3 | **2.1 ЗНАЧИМЫЙ** 🟠 | В чеклист Раздела 6 добавлен пункт «РЕШЕНИЕ ДО СТАРТА: Nice3point.Revit.Build.Tasks vs ручной XML» |
| 4 | **2.2 ЗНАЧИМЫЙ** 🟠 | В Directory.Build.props добавлен комментарий про MSB3277: при первой net48-сборке временно добавить в NoWarn для отладки |
| 5 | **2.3 ЗНАЧИМЫЙ** 🟠 | Добавлена заметка про AdWindows: grep подтвердил — не используется. Если появится — добавить пакет + VersionOverride |
| 6 | **2.4 ЗНАЧИМЫЙ** 🟠 | Добавлен подраздел 2.4: ручная проверка ElementIdCompat в Revit 2021–2023 (4 тест-сценария) |
| 7 | **3.1 ИНФО** 🟢 | Фаза 3.1: примечание — `-f net48` рекомендуется с `.R21`–`.R24` для избежания лишней компиляции |
| 8 | **3.2 ИНФО** 🟢 | Добавлен подраздел 5.7: визуальное тестирование WPF на net48 vs net8.0 (таблица рисков + список Views) |
| 9 | **3.4 ИНФО** 🟢 | CI: `dotnet test` теперь с `--no-build` (экономия ~30 сек) |

### v13 (2026-04-15) — Финальные правки (0 критических, 2 средних, 1 малое)

> **Вердикт критика:** ✅ Plan is technically sound. Найдено 3 проблемы, ни одна не блокирует сборку.

| # | Код | Суть исправления |
|---|-----|-----------------|
| 1 | **СРЕДНИЙ-1** 🟡 | Добавлен подраздел 5.8: TLS 1.2 для net48 — инициализация `ServicePointManager.SecurityProtocol` в `App.OnStartup` |
| 2 | **СРЕДНИЙ-2** 🟡 | MSB3277 комментарий в Directory.Build.props расширен: 5-шаговая инструкция по разрешению конфликтов DLL на net48 |
| 3 | **МАЛЫЙ-1** 🟢 | Directory.Packages.props: добавлен WARNING-комментарий к DI — «НЕ обновлять выше 8.x» |
