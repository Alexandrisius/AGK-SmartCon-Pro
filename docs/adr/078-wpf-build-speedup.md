# ADR-078: Ускорение WPF-сборок — устранение local-type XAML double-compile

**Date:** 2026-11-19  
**Status:** accepted  
**Related:** ADR-051 (ILRepack-изоляция зависимостей), [known-workarounds](../known-workarounds.md) (#95 render-recovery, ref-assembly workaround dotnet/wpf#11579), skill `smartcon-build-guide` §6

## Context

Разработка фич упиралась в ожидание сборок. Замеры (Debug.R25, worktree) показали:
правка одного `.cs` в `SmartCon.FamilyManager` занимала **169 с**, из них
`GenerateTemporaryTargetAssembly` = **141 с**, основной `Csc` ≈ 26 с.

Root cause подтверждён исходным кодом WPF (`IncrementalCompileAnalyzer`,
PresentationBuildTasks) и upstream-issues:

1. **Local-type XAML → двойная компиляция.** Если XAML проекта ссылается на типы,
   объявленные в том же проекте (elements / `x:Type` / `x:Static` / attached
   properties), `MarkupCompilePass1` откладывает их в `MarkupCompilePass2`, который
   требует `GenerateTemporaryTargetAssembly`: создаётся временный проект
   `<random>_wpftmp` и **полностью перекомпилирует все исходники проекта второй раз**.
   Анализатор консервативен: `IsFileListChanged(SourceCodeFiles)` (любая правка .cs)
   при наличии `.lref` (local-type XAML) → `RecompileCategory.PagesWithLocalType` →
   temp-проект на каждой итерации.
2. **Каскад по implementation-ссылкам.** `MarkupCompilePass1` получает
   `References="@(ReferencePath)"` (implementation dll). `IsFileListChanged(References)`
   → `RecompileCategory.All` при любой перекомпиляции смежного проекта (Core/UI),
   даже без изменения публичного API (dotnet/wpf#11579, #5458). Temp-проект вдобавок
   пересобирает referenced-проекты (#5448), обновляя их timestamps → цикл
   самоподдерживается.

`SmartCon.FamilyManager` (26 XAML, 57k строк) и `SmartCon.Agent` (AgentPaneControl)
имели local-type XAML; `SmartCon.UI` / `PipeConnect` / `ProjectManagement` — нет
(их XAML ссылается только на `assembly=SmartCon.UI`), поэтому собирались быстро.

## Decision

### 1. Ref-assembly workaround (триггер каскада)

В `src/Directory.Build.targets` добавлены таргеты `_SmartConUseRefAssembliesForMarkupCompile`
(`BeforeTargets="MarkupCompilePass1"`) и `_SmartConRestoreRefsAfterMarkupCompile`
(`AfterTargets`): на время pass1 identity `@(ReferencePath)` подменяются на
`%(ReferenceAssembly)` (ref-сборки стабильны при impl-only перекомпиляции), затем
восстанавливаются. Дословно соответствует workaround из dotnet/wpf#11579. На net48
ref-сборки не производятся → no-op; действует только на WPF-проектах. Выходной IL
идентичен (подмена влияет лишь на инкрементальное решение pass1). **Temporary** —
до фикса upstream; запись в `known-workarounds.md`.

### 2. Устранение local-type XAML в FamilyManager и Agent (триггер double-compile)

XAML-referenced типы вынесены так, чтобы XAML ссылался на них как на **внешние**
(`assembly=SmartCon.Core` / `assembly=SmartCon.UI`):

- **Core presentation-контракты (WPF-free, I-09):** `ICatalogTreeNode` / `ICategoryNode`
  (`Models/FamilyManager/ICatalogTreeNode.cs`), `ICropAvatarController`
  (`Services/FamilyManager/ICropAvatarController.cs`), `CropViewportMetrics`,
  `StatusNoticeSeverity`, `DatabaseListItem`. ViewModels реализуют контракты
  (`CatalogTreeNodeViewModel : ICatalogTreeNode`, `CategoryNodeViewModel : ICategoryNode`,
  `CropAvatarViewModel : ICropAvatarController`); `Parent`/`Children` — explicit-реализация
  через ковариантность.
- **Helpers → SmartCon.UI:** crop- и sticky-поведения, `FamilyTreeItemStyleSelector`,
  `DatabaseListItemIconSelector`, новый `CatalogTreeNodeTemplateSelector`,
  `ContextDonut`, `MarkdownToFlowDocumentConverter` + `MarkdownFlowDocumentRenderer`
  (Markdig перенесён Agent→UI), `FamilyTooltipView`. Поведения/селекторы рефакторены
  на Core-интерфейсы вместо конкретных VM.
- **XAML:** дерево FM переведено с implicit `DataType={x:Type vm:...}` на keyed-шаблоны
  (`CategoryNodeTemplate` / `FamilyLeafNodeTemplate` / `FamilyTypeNodeTemplate`) +
  `TreeView.ItemTemplateSelector` (порядок ресурсов как у существующего
  `ItemContainerStyleSelector`, без forward-ссылок). Селекторы выбирают по
  дискриминатору `IsCategory`/`IsType` (эквивалентно прежнему switch по конкретным типам).

Результат: у FM и Agent **нет `.lref` и `_wpftmp`** ни в одной из 7 конфигураций.

### 3. Осознанный trade-off: SmallTemp в SmartCon.UI

`FamilyTooltipView` переехал в UI и использует UI-конвертеры/локализацию как local-типы
(`loc:` / `converters:` без `assembly=`), поэтому **SmartCon.UI получила небольшой
temp** (4.7k строк, редко редактируется). Налог перенесён из часто редактируемого FM
(57k) в маленькую UI. FM/Agent temp устранены полностью.

### 4. Render-recovery (#95) распространён на net8/net10

`BatchDialogRenderRecovery` был `#if NET48`. Баг «белый WPF-диалог после апгрейда
семейства» (Revit убивает render thread) воспроизводится и на Revit 2025/net8
(10/10 при импорте семейств 2023; ручной resize оживляет рендер). `#if NET48` снят,
recovery подключён к modal- и modeless-диалогам на всех TFM. Подтверждено
`freeze-diagnostic.log`: `[RenderRecovery] WM_PAINT received → MoveToScreen`.

## Consequences

- Итерация `.cs` FamilyManager: **169 с → 40 с** (одна компиляция); Agent: **→ 1.4 с**;
  правка Core больше не каскадит в пересборку XAML FM/Agent (22 с, FM не пересобирается).
- Dependency rule сохранён: Core без WPF/Revit, UI-хелперы не ссылаются на FM/Agent.
- Холодный sweep 7 конфигураций остаётся неустранимо тяжёлым (компиляция всего проекта
  7×); рефактор бил по инкрементальному циклу разработки, не по холодному sweep.
- Дальнейшее ускорение FM-итераций (< 40 с) — только hot-reload (RevitAddInManager)
  или дробление FM; это отдельные инструменты, не данный рефактор.

## Verification

- Unit R25: **3814/3814** passed.
- Debug sweep 7/7: exit 0; Release.R25 (полные анализаторы): 0 ошибок / 0 предупреждений.
- Интеграционные (реальный Revit): R25/net8 — 416 passed; net48/Revit2023 — 246 passed,
  0 сбоев; R27/net10 — 402 passed. Единственный сбой во всех платформах —
  `Eval_Catalog_ReplayOrLive` (внешний LLM API `MissingSessionID`, окружение, не код).
- Валидация general-субагентом: OVERALL PASS (инварианты, dependency rule, полнота,
  behavior-preserving рефакторы, порядок XAML-ресурсов).
- Ручной UI-тест в Revit 2025 подтверждён владельцем (batch-импорт 2023-семейств,
  диалоги FM/Agent рендерятся).