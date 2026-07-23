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
   Для крупных модулей — тематический файл внутри подпапки, напр. `models/family-manager/import.md`.
3. Добавь секцию `## <TypeName>` (для моделей) или `## I<Name>` (для интерфейсов) с сигнатурой.
4. Запусти `powershell -File tools\validate-docs.ps1` — должно быть `PASSED`.

## Правило разбиения крупных модулей

Файл документации **не должен превышать 1000 строк**. Когда модульный файл приближается к порогу:

1. Создай подпапку `models/<module>/` (или `interfaces/<module>/`).
2. Разбей содержимое на тематические файлы (`catalog.md`, `import.md`, `stale-detection.md`, ...).
   Один файл = одна связная тема; ориентир — 100-600 строк на файл.
3. Создай в подпапке `README.md` с таблицей-индексом тематических файлов.
4. Каждый тематический файл начинается с frontmatter `module: <module>` и заголовка H1,
   типы документируются секциями `## TypeName` как обычно.
5. Удали исходный `<module>.md` и обнови ссылки в `models/README.md` (или `interfaces/README.md`).

Пример: `models/family-manager/` — 10 тематических файлов вместо одного файла на 2700+ строк.
