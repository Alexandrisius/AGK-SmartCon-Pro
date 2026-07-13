# ADR-046: Hybrid WPF Icon Sourcing — PackIconMaterial in Module BAML, PathGeometry in SmartCon.UI

**Date:** 2026-07-10  
**Status:** accepted  
**Related:** Issue #118 (Revit crash when replacing Generic.xaml icons with PackIconMaterial)

## Context

`SmartCon.UI` предоставляет общие WPF-ресурсы через `Generic.xaml`. Этот файл загружается не как обычный BAML, а через `XamlReader.Load` из **embedded resource** (`SingletonResources.cs`). Причина — Revit-плагин не имеет полноценного WPF `Application` контекста, особенно на .NET Framework 4.8 (Revit 2021–2024), где `Application.Current` равен `null`. Embedded-загрузка работает стабильно в обоих таргетах.

Мы попытались заменить ручные `Path`/`PathGeometry` в `SmartCon.UI/Generic.xaml` на `PackIconMaterial` из `MahApps.Metro.IconPacks.Material`. Это вызвало краш Revit при открытии проекта с сообщением:

```
Dispatcher processing has been suspended, but messages are still being processed.
```

Корень проблемы: `PackIconMaterial` — это полноценный `Control` со своим `DefaultStyle`. Стиль живёт в `MahApps.Metro.IconPacks.Core` и загружается через `pack://application:,,,/...` URI. В контексте `XamlReader.Load` из embedded resource без `Application` WPF не может разрешить этот pack URI, что приводит к падению на рендеринге.

В то же время `PackIconMaterial` отлично работает в обычных BAML-вьюхах модулей. Например, `FamilyManagerPaneControl.xaml` уже использует его без проблем, потому что такие вьюхи компилируются в BAML и загружаются штатным WPF-загрузчиком.

Требуется чёткое архитектурное правило: где можно использовать внешние иконочные контролы, а где нужно оставаться на самодостаточных `Path`/`PathGeometry`.

## Decision

### 1. SmartCon.UI — только самодостаточные иконки

`SmartCon.UI/Generic.xaml` и любые другие общие ресурсы, загружаемые через `XamlReader.Load`, **не содержат** внешних иконочных контролов (`PackIconMaterial`, `PackIcon` и аналоги). Общие иконки реализуются как `PathGeometry`, `DrawingImage`, `Path` или простые фигуры, не требующие дополнительных сборок со стилями.

### 2. Модули — внешние иконочные библиотеки в BAML-вьюхах

Модули (`SmartCon.FamilyManager`, `SmartCon.PipeConnect`, `SmartCon.ProjectManagement` и т.д.) **могут** использовать `MahApps.Metro.IconPacks.Material` внутри своих обычных BAML-вьюх (`.xaml` файлы, компилируемые в BAML). Там `PackIconMaterial` работает корректно.

### 3. Разделение зависимостей

- `SmartCon.UI.csproj` **не ссылается** на `MahApps.Metro.IconPacks.Material`.
- Каждый модуль сам решает, нужна ли ему эта зависимость, и сам её подключает.
- `SmartCon.App` транзитивно получает иконки только через модули, в которых они используются.

### 4. Дублирование допустимо

Одна и та же иконка может существовать одновременно:
- как `PathGeometry` в `SmartCon.UI` (для общих стилей);
- как `PackIconMaterial Kind="..."` в модуле (для конкретной вьюхи).

Это приемлемый компромисс между стабильностью shared UI и удобством разработки в модулях.

### 5. Запрет на обходные пути в shared UI

Не использовать workaround'ы типа ручной инициализации `Application.ResourceAssembly` или подмены `pack://` URI в `SmartCon.UI` ради возможности загружать `PackIconMaterial` в embedded resource. Такие workaround'ы хрупки в контексте Revit и нарушают независимость `SmartCon.UI`.

## Consequences

- `SmartCon.UI` остаётся независимой от внешних иконочных библиотек и может использоваться любыми модулями без риска транзитивного падения.
- `SmartCon.FamilyManager` и другие модули сохраняют свободу выбирать иконочную библиотеку в своих вьюхах.
- Возможно небольшое дублирование path-данных между `SmartCon.UI` и модулями — оно считается допустимым.
- Появляется чёткое правило для code review: «`PackIconMaterial` в `SmartCon.UI` — запрещено; в BAML-вьюхах модулей — разрешено».
- Не требуется регистрация `AssemblyResolve` или другие костыли для `MahApps.Metro.IconPacks` в `SmartCon.App`.

## Verification

- Build R19: 0 warnings / 0 errors
- Build R21: 0 warnings / 0 errors
- Build R24: 0 warnings / 0 errors
- Build R25: 0 warnings / 0 errors
- Tests: все проходят
- `SmartCon.UI.csproj` не содержит `MahApps.Metro.IconPacks.Material`
- `SmartCon.UI/Generic.xaml` не содержит `PackIconMaterial`
- Ручной тест в Revit: открытие проекта не вызывает краш `Dispatcher processing has been suspended`

## Notes

- В `SmartCon.UI/Generic.xaml` и `SingletonResources.cs` добавлены комментарии-ссылки на этот ADR, чтобы предотвратить повторную попытку вставить `PackIconMaterial` в shared resource dictionary.
- Если в будущем Microsoft / MahApps изменит механизм загрузки стилей так, что `PackIconMaterial` станет безопасен в `XamlReader.Load`, этот ADR можно пересмотреть. До тех пор правило остаётся в силе.
