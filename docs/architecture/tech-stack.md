# Технологический стек

> Загружать: при настройке проекта или добавлении зависимостей.

## Основной стек

| Компонент | Технология | Версия | Примечание |
|---|---|---|---|
| Платформа | .NET | Framework 4.8 + 8.0 | Multi-target: `net48;net8.0-windows` (WPF) |
| Язык | C# | 12 | Nullable enabled, ImplicitUsings enabled |
| UI Framework | WPF | встроен в .NET | MVVM, без code-behind |
| CAD-платформа | Autodesk Revit | 2021-2025 | RevitAPI.dll + RevitAPIUI.dll (multi-version) |
| DI-контейнер | Microsoft.Extensions.DependencyInjection | 8.0.1 | Регистрация в SmartCon.App |
| MVVM Toolkit | CommunityToolkit.Mvvm | 8.4.0 | ObservableObject, RelayCommand, source generators |
| Тестирование | xUnit | 2.9.3 | Unit + ViewModel тесты |
| Моки | Moq | 4.20.72 | Мокирование интерфейсов Revit |
| Сериализация | System.Text.Json | встроен в .NET 8 / NuGet для net48 | JSON маппинга фитингов |
| Хранилище маппинга | JSON-файл | — | Глобальный, в AppData |

## NuGet-пакеты

### SmartCon.Core
- `Nice3point.Revit.Api` — **compile-time only** Reference, CopyLocal = false. Используется для типов-carriers (ElementId, XYZ). См. I-09.
- `PolySharp` — polyfills для C# 12 language features на net48
- `System.Text.Json` — только для net48 target (встроен в net8.0)

### SmartCon.Revit
- `Nice3point.Revit.Api` — Reference, CopyLocal = false
- `Nice3point.Revit.Api.UI` — Reference, CopyLocal = false

### SmartCon.App
- `Microsoft.Extensions.DependencyInjection` 8.0.1

### SmartCon.UI
- `CommunityToolkit.Mvvm` 8.4.0 — ObservableObject, RelayCommand, [ObservableProperty], [RelayCommand] source generators

### SmartCon.PipeConnect
- (зависит от SmartCon.UI → транзитивно CommunityToolkit.Mvvm)

### SmartCon.Tests
- `xunit` 2.9.3 + `xunit.runner.visualstudio` 2.8.2
- `Moq` 4.20.72
- `Microsoft.NET.Test.Sdk` 17.12.0
- `Autodesk.Revit.API` (Nice3point.Revit.Api) — для создания XYZ/ElementId в тестах

## Настройки сборки (Directory.Build.props)

```xml
<Project>
  <PropertyGroup>
    <TargetFrameworks>net48;net8.0-windows</TargetFrameworks>
    <LangVersion>12</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>
```

## Путь хранения конфигурации

```
%APPDATA%/SmartCon/
├── fitting-mapping.json       <- маппинг фитингов (глобальный)
├── connector-types.json       <- типы коннекторов (глобальный)
└── settings.json              <- настройки приложения (если нужны)
```

## Подключение Revit API

Revit API распространяется через NuGet-пакеты [Nice3point.Revit.Api](https://www.nuget.org/packages/Nice3point.Revit.Api):

```
Nice3point.Revit.Api       <- RevitAPI.dll
Nice3point.Revit.Api.UI    <- RevitAPIUI.dll
```

Пакеты обеспечивают compile-time reference для всех поддерживаемых версий Revit (2021-2025).
Версия пакета выбирается соответственно целевой версии Revit.

В `.csproj` подключаются как PackageReference с **ExcludeAssets=runtime**:
```xml
<ItemGroup>
  <PackageReference Include="Nice3point.Revit.Api" Version="*" ExcludeAssets="runtime" />
  <PackageReference Include="Nice3point.Revit.Api.UI" Version="*" ExcludeAssets="runtime" />
</ItemGroup>
```

## Post-Build: копирование в Revit Addins

Для отладки DLL и `.addin` копируются в папку аддинов Revit:
```
%APPDATA%\Autodesk\Revit\Addins\2021\
...
%APPDATA%\Autodesk\Revit\Addins\2025\
```

Настраивается через `<PostBuildEvent>` или `<OutputPath>` в `SmartCon.App.csproj`.

## Ограничения Revit API

- **Однопоточность:** все вызовы Revit API — только из main thread или через IExternalEventHandler
- **Revit API DLL:** ExcludeAssets=runtime, не включать в дистрибутив (уже есть в установке Revit)
- **Multi-target:** Revit 2021-2024 использует .NET Framework 4.8, Revit 2025 — .NET 8
- **ElementId:** 32-bit на Revit 2021-2023, 64-bit на Revit 2024+ — см. `ElementIdCompat` (I-11)
