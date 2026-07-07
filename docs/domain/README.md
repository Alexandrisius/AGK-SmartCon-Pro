---
module: root
---
# Домен: модели и интерфейсы

> **Загружать:** при работе с моделями данных или контрактами сервисов.
> **Правила:**
> - Не создавай новые доменные классы без добавления их в `models/<module>.md`.
> - Не создавай новые интерфейсы без добавления их в `interfaces/<module>.md`.
> - Валидатор `tools/validate-docs.ps1` проверяет, что каждый `.cs` в `SmartCon.Core/Models/`, `SmartCon.Core/Math/`, `SmartCon.Core/Services/Interfaces/` задокументирован.

Структура каталога отражает структуру кода в `src/SmartCon.Core/`:

| Документ | Содержимое |
|---|---|
| [`models/`](models/README.md) | Все доменные классы (records, enums, value-типы) |
| [`interfaces/`](interfaces/README.md) | Все контракты сервисов |
| [`glossary.md`](glossary.md) | Глоссарий терминов проекта |

## Принципы

- **Используются только типы из .NET и Core.** Исключение: `ElementId`, `XYZ`, `Domain`, `BuiltInParameter`, `ForgeTypeId` — value-типы Revit, допустимые в Core через compile-time ссылку на API (без runtime-зависимости, I-09).
- **Для чистой математики (VectorUtils, ConnectorAligner) используется `Vec3`** вместо `XYZ` (ADR-009). Конвертация `XYZ <-> Vec3` — в `SmartCon.Revit/Extensions/XYZExtensions.cs`.
- **Файлы моделей** живут в `SmartCon.Core/Models/` (включая подпапку `FamilyManager/`).
- **Файлы интерфейсов** живут в `SmartCon.Core/Services/Interfaces/`.

## Как добавить новую модель/интерфейс

1. Создай `.cs` файл в правильной директории Core.
2. Найди подходящий `models/<module>.md` или `interfaces/<module>.md` по домену.
3. Добавь секцию `## <TypeName>` (для моделей) или `## I<Name>` (для интерфейсов) с сигнатурой.
4. Запусти `powershell -File tools\validate-docs.ps1` — должно быть `PASSED`.
