# ADR-001: Clean Architecture — Core без зависимостей от Revit/WPF

**Статус:** accepted
**Дата:** 2026-03-25

## Контекст

Плагин для Revit тесно связан с Revit API. Типичная ошибка — размазать вызовы `Document`, `Transaction`, `Element` по всем файлам. Это делает код нетестируемым и хрупким при обновлении Revit API.

## Решение

Разделить Solution на слои по принципу Clean Architecture:

- **SmartCon.Core** — чистый C#. Модели, интерфейсы, алгоритмы. Запрет на `using Autodesk.Revit.DB` и `using System.Windows`.
- **SmartCon.Revit** — реализации интерфейсов Core через Revit API.
- **SmartCon.UI** — WPF-библиотека без Revit.
- **SmartCon.PipeConnect** — плагин, зависит от Core и UI, не от Revit напрямую.

Связывание интерфейсов с реализациями — через DI-контейнер (MEDI) в SmartCon.App.

## Последствия

**Плюсы:**
- Core тестируется без Revit (xUnit + Moq)
- Алгоритмы (ConnectorAligner, FormulaSolver, PathfinderService) переиспользуемы
- Обновление Revit API затрагивает только SmartCon.Revit

**Минусы:**
- Больше файлов и проектов в Solution
- Нужна дисциплина (проверка I-09 при каждом коммите)

## Альтернативы

1. **Monolith:** один проект SmartCon — проще, но нетестируемо и хрупко.
2. **Onion Architecture:** избыточна для плагина Revit, слишком много абстракций.
