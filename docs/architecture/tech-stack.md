# Технологический стек

> Загружать: при настройке проекта или добавлении зависимостей.

## Основной стек

| Компонент | Технология | Версия | Примечание |
|---|---|---|---|
| Платформа | .NET | 8.0 | net8.0-windows (WPF требует Windows) |
| Язык | C# | 12 | Nullable enabled, ImplicitUsings enabled |
| UI Framework | WPF | встроен в .NET 8 | MVVM, без code-behind |
| CAD-платформа | Autodesk Revit | 2025 | RevitAPI.dll + RevitAPIUI.dll |
| DI-контейнер | Microsoft.Extensions.DependencyInjection | 8.0.1 | Регистрация в SmartCon.App |
| MVVM Toolkit | CommunityToolkit.Mvvm | 8.4.0 | ObservableObject, RelayCommand, source generators |
| Тестирование | xUnit | 2.9.3 | Unit + ViewModel тесты |
| Моки | Moq | 4.20.72 | Мокирование интерфейсов Revit |
| Сериализация | System.Text.Json | встроен в .NET 8 | JSON маппинга фитингов |
| Хранилище маппинга | JSON-файл | — | Глобальный, в AppData |

## NuGet-пакеты

### SmartCon.Core
- `Autodesk.Revit.API` (2025) — **compile-time only** Reference, CopyLocal = false. Используется для типов-carriers (ElementId, XYZ). См. I-09.

### SmartCon.Revit
- `Autodesk.Revit.API` (2025) — Reference, CopyLocal = false
- `Autodesk.Revit.API.UI` (2025) — Reference, CopyLocal = false

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
- `Autodesk.Revit.API` (2025) — для создания XYZ/ElementId в тестах

## Настройки сборки (Directory.Build.props)

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
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

Revit API **не распространяется через NuGet**. DLL берутся из установки Revit:

```
C:\Program Files\Autodesk\Revit 2025\RevitAPI.dll
C:\Program Files\Autodesk\Revit 2025\RevitAPIUI.dll
```

В `.csproj` подключаются как локальные Reference с **CopyLocal = false**:
```xml
<ItemGroup>
  <Reference Include="RevitAPI">
    <HintPath>$(RevitApiPath)\RevitAPI.dll</HintPath>
    <Private>false</Private>
  </Reference>
  <Reference Include="RevitAPIUI">
    <HintPath>$(RevitApiPath)\RevitAPIUI.dll</HintPath>
    <Private>false</Private>
  </Reference>
</ItemGroup>
```

Переменная `RevitApiPath` задаётся в `Directory.Build.props`.

## Post-Build: копирование в Revit Addins

Для отладки DLL и `.addin` копируются в папку аддинов Revit:
```
%APPDATA%\Autodesk\Revit\Addins\2025\
```

Настраивается через `<PostBuildEvent>` или `<OutputPath>` в `SmartCon.App.csproj`.

## Ограничения Revit API

- **Однопоточность:** все вызовы Revit API — только из main thread или через IExternalEventHandler
- **Revit API DLL:** CopyLocal = false, не включать в дистрибутив (уже есть в установке Revit)
- **Версия .NET:** Revit 2025 поддерживает .NET 8 (первая версия с полной поддержкой)
