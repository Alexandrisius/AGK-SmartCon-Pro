# ADR-013: Модуль ProjectManagement — Share Project

**Статус:** accepted
**Дата:** 2026-04-23

## Контекст

Проект SmartCon (плагин для Revit) нуждается в модуле для автоматизации процесса
перемещения моделей из рабочей зоны (WIP) в зону Shared по стандарту ISO 19650.
Существующий прототип (ShareProject в репозитории iAmBIM) рабочий, но имеет
серьёзные архитектурные проблемы:

- Вся логика в одном файле (StartPlugin.cs — 120+ строк)
- Прямые вызовы Revit API из UI (code-behind)
- SQL-зависимость для проверки нейминга (избыточна)
- Нет настройки категорий очистки
- Жёстко захардкожены пути (замена "02_WIP" на "03_Shared")
- Нет мульти-версионной поддержки
- Нет unit-тестов

Нужен модуль, следующий Clean Architecture и инвариантам I-01..I-13.

## Решение

### Отдельный csproj: SmartCon.ProjectManagement

По аналогии с `SmartCon.PipeConnect` — изолированный модуль с собственными
Commands, ViewModels, Views. Зависит от SmartCon.Core и SmartCon.UI.

### Хранение настроек: ExtensibleStorage в .rvt

Per-project storage по паттерну ADR-012:
- Schema: `SmartConProjectManagementSchema` (уникальный GUID)
- DataStorage: `"SmartCon.ProjectManagement"`
- Payload: JSON-string с полной конфигурацией ShareProjectSettings
- Import/Export JSON для переноса между файлами

**Обоснование:** Каждый .rvt файл находится в своей папке раздела со своей
папкой Shared. Координатор настраивает параметры для конкретного файла,
при этом пользуется Import/Export чтобы не заполнять вручную каждый файл.
Настройки путешествуют с моделью.

### Настраиваемый список очистки (PurgeOptions)

Каждая категория очистки — отдельный boolean-флаг:
- RVT-связи, CAD-импорты, изображения, облака точек
- Группы, сборки, MEP-пространства
- Арматура (Rebar + FabricReinforcement)
- Неиспользуемые элементы (Purge через PerformanceAdviser)

Все флаги включены по умолчанию. Координатор может отключить нужные.

### Универсальный парсер нейминга (FileNameTemplate)

Вместо жёсткой проверки имени — настраиваемая система:
- Кастомный разделитель (тире, подчёркивание, точка и т.д.)
- Блоки с ролями (project, discipline, status, milestone и т.д.)
- Пары маппинга статусов: WIP → Shared

Это позволяет адаптировать плагин под любой мировой стандарт (ISO 19650,
локальные стандарты, кастомные договорённости).

### Динамический выбор видов

Вместо префикса "TASK_" (костыль из прототипа) — координатор выбирает
конкретные виды из файла через чекбоксы. Виды хранятся по имени.

### UI: модальное окно настроек + немодальный прогресс

- **ShareSettingsView** — модальное окно с 4 табами (Общие, Очистка, Виды, Нейминг)
- **ShareProgressView** — немодальное TopMost-окно с прогресс-баром
- Оба следуют I-10 (MVVM, code-behind только DataContext)

## Архитектура

| Слой | Компонент | Назначение |
|---|---|---|
| Core | `ShareProjectSettings` + вложенные модели | Модели данных настроек |
| Core | `IShareProjectSettingsRepository` | CRUD интерфейс |
| Core | `IShareProjectService` | Операция Share |
| Core | `IModelPurgeService` | Очистка модели |
| Core | `IFileNameParser` | Парсинг/трансформация имён |
| Core | `IViewRepository` | Получение видов из документа |
| Core | `ShareSettingsJsonSerializer` | JSON сериализация (pure C#) |
| Revit | `ProjectManagementSchema` | ExtensibleStorage schema |
| Revit | `RevitShareProjectSettingsRepository` | ExtensibleStorage CRUD |
| Revit | `RevitShareProjectService` | Реализация Share (8 шагов) |
| Revit | `RevitModelPurgeService` | Реализация очистки |
| Revit | `RevitFileNameParser` | Реализация парсера |
| Revit | `RevitViewRepository` | Реализация списка видов |
| ProjectManagement | Commands / VMs / Views | UI-слой модуля |

### Зависимости

```
SmartCon.Core ← SmartCon.ProjectManagement
SmartCon.Core ← SmartCon.Revit (реализации интерфейсов)
SmartCon.UI   ← SmartCon.ProjectManagement
SmartCon.Core ← SmartCon.App (DI-регистрация)
```

ProjectManagement НЕ ссылается на Revit напрямую — только через интерфейсы Core.

## Purge неиспользуемых — PerformanceAdviser

Revit API не предоставляет прямого метода "Purge Unused". Обходной путь —
использование встроенного PerformanceAdviser rule с GUID
`e8c63650-70b7-435a-9010-ec97660c1bda`.

Алгоритм вызывает `ExecuteRules` в цикле пока возвращаются элементы.
Цикл нужен из-за cascade-эффекта: после удаления одной партии элементов
другие могут стать неиспользуемыми.

## Последствия

### Плюсы

- Clean Architecture: Core не зависит от Revit API (I-09)
- MVVM: тестируемые ViewModel, unit-тесты на Core-логику
- Per-project storage: настройки привязаны к файлу (ADR-012 pattern)
- Универсальность: любой стандарт нейминга через конфигурацию
- Расширяемость: новые категории очистки, новые роли блоков

### Минусы

- Отдельный csproj = больший solution
- Настройки нужно создавать для каждого .rvt (митигация: Import/Export JSON)
- PerformanceAdviser purge — undocumented workaround, может сломаться в будущих версиях Revit

## Тестирование

Unit-тесты (SmartCon.Tests):
- `FileNameParserTests` — парсинг, трансформация, валидация имён
- `ShareSettingsJsonSerializerTests` — сериализация/десериализация JSON
- `PurgeOptionsTests` — модели данных
- `ShareSettingsViewModelTests` — VM логика с моками

Интеграционные тесты НЕ создаются (Revit-специфичные типы нельзя тестировать
без запущенного Revit).

## Иконки

- ShareProject: `ShareProject_16x16.png` / `ShareProject_32x32.png` (уже есть в Resources/Icons)
- Settings: `Settings_16x16.png` / `Settings_32x32.png` (переиспользуем с панели PipeSystems)

## Связанные ADR

- [ADR-008](008-external-event-action-queue.md) — ExternalEvent pattern для ShareProgressView
- [ADR-012](012-per-project-extensible-storage.md) — паттерн ExtensibleStorage для настроек
