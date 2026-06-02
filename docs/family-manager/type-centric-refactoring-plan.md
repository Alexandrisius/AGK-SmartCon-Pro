# FamilyManager Type-Centric Refactoring Plan

> **Status:** Draft v2 (SHA256-safe)  
> **Phase:** Planning  
> **Target:** SmartCon.FamilyManager module  
> **Date:** 2026-06-02

---

## Введение (для агента без контекста)

Модуль **FamilyManager** — это dockable panel внутри плагина SmartCon для Autodesk Revit. Он позволяет проектировщикам управлять библиотекой семейств Revit (.rfa): импортировать, каталогизировать, искать и размещать в проекте.

**Текущая проблема:** Модуль работает с семействами как с неделимыми сущностями. Пользователь может загрузить всё семейство целиком (все типоразмеры) или DnD'ить тип, но при DnD загружается всё семейство. Это приводит к:
- Раздутию модели — загружаются сотни ненужных типов
- Путанице в UI — пользователь не понимает что загружено, а что нет
- Отсутствию поддержки Type Catalog — внешние .txt файлы с типоразмерами игнорируются

**Цель рефакторинга:** Перейти к работе с типоразмерами как с первичными сущностями. Пользователь должен мыслить и работать с типами, а не с семействами. Семейство — это контейнер, тип — это то что загружается в проект.

**Ключевые принципы:**
1. **DnD типа = загрузка только этого типа** (через `LoadFamilySymbol`)
2. **Type Catalog (.txt) = полноценный источник типов** — парсим при импорте, храним в SQLite
3. **Виртуальный тип (семейство без типов)** — отображаем в UI, но НЕ модифицируем .rfa (SHA256-safe)
4. **Семейство остаётся в контекстном меню** — команда «Загрузить в проект» загружает всё семейство как раньше
5. **Никаких индикаторов загрузки** — пользователь не должен думать о состоянии типа

---

## Проблемы и ограничения

### P-01. SHA256 и дедупликация
Наша система использует SHA256 для дедупликации файлов при импорте. Если мы модифицируем .rfa (например, создаём тип через `FamilyManager.NewType` + `Save`), SHA256 меняется и при следующем импорте файл считается новым.

**Решение:** НЕ модифицируем .rfa. Виртуальный тип создаётся только в UI (не в БД, не в файле).

### P-02. Type Catalog и Revit API
Revit API НЕ предоставляет прямого доступа к Type Catalog файлам. Нет метода «прочитать все типы из .txt». Мы должны парсить CSV самостоятельно.

**Решение:** Pure C# парсер в `SmartCon.Core` (без Revit API). Заголовок: `,Param##Type##Units`, строки: `TypeName,Value1,Value2...`.

### P-03. LoadFamilySymbol и Type Catalog
`LoadFamilySymbol(filename, typeName)` официально поддерживает Type Catalog: «This function supports loading of types/symbols stored in the family, or those available in the family Type Catalog file». Но .txt должен лежать рядом с .rfa с точно таким же именем.

**Решение:** Копируем .txt в managed storage рядом с .rfa при импорте. При переименовании семейства — переименовываем и .txt.

### P-04. Семейство без типов
Если у семейства нет типоразмеров, Revit при загрузке в проект создаёт default type с именем файла. Мы не можем использовать `LoadFamilySymbol` — в .rfa нет типа для загрузки.

**Решение:** Для виртуальных типов используем `LoadFamily` (загрузка всего семейства). Revit сам создаст default type. Это edge-case, происходит прозрачно.

### P-05. Dependency Rule (I-09)
`SmartCon.Core` не может вызывать Revit API. `SmartCon.FamilyManager` не может ссылаться на `SmartCon.Revit`. Вся работа с Revit API — только через ExternalEvent.

**Решение:** Парсер Type Catalog — в Core (pure C#). Загрузка через Revit API — в `SmartCon.Revit` через ExternalEvent.

---

## Бизнес Pipeline

### BP-01. DnD типа в модель (основной сценарий)

**Актор:** Проектировщик.

**Поток:**
1. Пользователь выбирает тип в дереве FamilyManager
2. Начинает drag (PreviewMouseLeftButtonDown + MouseMove)
3. Отпускает на холсте Revit (Drop)
4. Система загружает только этот тип в проект
5. Тип активируется для размещения
6. Пользователь кликает в модели — элемент размещается

**Разветвление:**
- **Тип реальный (из .rfa или Type Catalog):** `LoadFamilySymbol(filename, typeName)` загружает только этот тип
- **Тип виртуальный (семейство без типов):** `LoadFamily(filename)` загружает всё семейство, Revit создаёт default type

**Важно:** Для `LoadFamilySymbol` Type Catalog .txt должен лежать рядом с .rfa. Если семейство уже загружено — `LoadFamilySymbol` может вернуть false (тип уже существует), это не ошибка. Обязательно использовать `IFamilyLoadOptions` для обработки перезагрузки семейства.

### BP-02. Загрузка всего семейства (контекстное меню)

**Актор:** Проектировщик.

**Поток:**
1. Пользователь кликает ПКМ на семействе (FamilyLeafNode)
2. Выбирает «Загрузить в проект»
3. Система загружает весь .rfa со всеми типами (как сейчас)
4. Статус «Загружено»

**Ограничение:** Команда остаётся без изменений. Используется `LoadFamily`.

### BP-03. Размещение типа через контекстное меню

**Актор:** Проектировщик.

**Поток:**
1. Пользователь кликает ПКМ на типе (FamilyTypeNode)
2. Выбирает «Разместить тип»
3. Если тип реальный — `LoadFamilySymbol` + `ActivateAndPlaceType`
4. Если тип виртуальный — `LoadFamily` + `ActivateAndPlaceType` (первый доступный тип)

### BP-04. Type Catalog (.txt файл)

**Актор:** Система (при импорте).

**Поток:**
1. Пользователь импортирует .rfa в каталог
2. Система проверяет наличие {sameName}.txt рядом с исходным .rfa
3. Если найден:
   - Копирует .txt в managed storage рядом с .rfa
   - Парсит CSV: заголовок `,Param##Type##Units`, строки `TypeName,Value1,Value2...`
   - Создаёт `FamilyTypeDescriptor` для каждого типа → таблица `family_types`
   - Создаёт `ExtractedAttributeValue` для каждого параметра каждого типа → таблица `extracted_attribute_values`
4. В дереве показываются **только** типы из Type Catalog (встроенные типы из .rfa игнорируются)

**Управление жизненным циклом:**
- При создании новой версии семейства — .txt копируется в папку новой версии
- При переименовании семейства в FM — .txt переименовывается вместе с .rfa

### BP-05. Виртуальный тип (семейство без типоразмеров)

**Актор:** Система (отображение) + Проектировщик (DnD).

**Поток отображения:**
1. При построении дерева система проверяет: есть ли у семейства типы в `family_types`?
2. Если нет — добавляет виртуальный `FamilyTypeNodeViewModel`
3. Имя виртуального типа = имя файла .rfa (без расширения)
4. `IsVirtual = true`

**Поток DnD:**
1. Пользователь DnD'ит виртуальный тип
2. Система определяет `IsVirtual = true`
3. Использует `LoadFamily` (не `LoadFamilySymbol`)
4. Revit загружает семейство и создаёт default type с именем файла
5. Тип активируется для размещения

**Ограничения:**
- Виртуальный тип НЕ записывается в `family_types`
- Атрибуты (untypedValues) хранятся в `extracted_attribute_values` с `type_id = null` (как сейчас)
- При переименовании семейства в FM — имя виртуального типа обновляется в UI

### BP-06. Атрибуты из Type Catalog в окне свойств

**Актор:** Проектировщик.

**Поток:**
1. Пользователь открывает окно свойств типа (двойной клик или контекстное меню)
2. Система читает `ExtractedAttributeValue` из SQLite
3. Окно показывает параметры и значения из Type Catalog
4. Данные доступны без загрузки в проект Revit

**type_id:** Используется имя типа (string), как сейчас для обычных типов. Для виртуального типа — `type_id = null`.

### BP-07. Никаких индикаторов загрузки

**Принцип:** Пользователю не важно, загружен ли тип в проект. Он DnD'ит тип — система сама решает: загрузить или активировать.

**Реализация:** В дереве НЕ показываются галочки, цветовые индикаторы или текстовые метки «загружен».

---

## Технический план

### Этап 1. Type Catalog Parser

**Цель:** Парсинг CSV-файла Type Catalog без зависимости от Revit API.

**Файлы (новые):**
- `SmartCon.Core/Models/FamilyManager/TypeCatalogEntry.cs` — модель одной записи (имя типа + словарь параметров)
- `SmartCon.Core/Models/FamilyManager/TypeCatalogParseResult.cs` — результат парсинга (заголовки + записи)
- `SmartCon.Core/Services/Implementation/TypeCatalogParser.cs` — pure C# парсер

**Алгоритм:**
1. Читаем первую строку файла
2. Первый символ = разделитель (delimiter, обычно запятая)
3. Парсим заголовок: `,Param1##Type##Units,Param2##Type##Units...`
4. Парсим строки типов: `TypeName,Value1,Value2...`
5. Обрабатываем кавычки (двойные двойные кавычки для дюймов)
6. Возвращаем структурированный результат

**Важно:** Robust CSV parsing — использовать `TextFieldParser` или ручной парсер с учётом экранирования.

### Этап 2. Обновление FamilyTypeNodeViewModel

**Цель:** Поддержка виртуальных типов.

**Файл:** `SmartCon.FamilyManager/ViewModels/FamilyTypeNodeViewModel.cs`

**Изменения:**
- Добавить свойство `IsVirtual: bool`
- Обновить конструктор

### Этап 3. Отображение виртуального типа в дереве

**Цель:** Показывать виртуальный тип для семейств без типоразмеров.

**Файл:** `SmartCon.FamilyManager/ViewModels/FamilyManagerMainViewModel.Tree.cs`

**Изменения:**
- Убрать фильтрацию `type_name <> ''` из SQL-запросов
- В `AttachTypesToNodes`: после добавления всех реальных типов проверить `leaf.Children.Count`
- Если пусто — добавить виртуальный `FamilyTypeNodeViewModel` с `IsVirtual = true`
- Имя виртуального типа = имя файла .rfa (без расширения)

**Файл:** `SmartCon.FamilyManager/Services/LocalCatalog/LocalFamilyTypeRepository.cs`
- Убрать `AND type_name <> ''` из всех SQL-запросов

### Этап 4. Интеграция Type Catalog в импорт

**Цель:** Автоматический импорт Type Catalog при загрузке .rfa.

**Файлы:**
- `SmartCon.FamilyManager/Services/LocalCatalog/LocalFamilyImportService.cs`
- `SmartCon.FamilyManager/Services/LocalCatalog/FamilyDataImportService.cs`

**Алгоритм при импорте:**
1. Проверить наличие `{sameName}.txt` рядом с исходным .rfa
2. Если найден:
   - Копировать .txt в managed storage рядом с .rfa
   - Парсить через `TypeCatalogParser`
   - Создать `FamilyTypeDescriptor` для каждого типа → `family_types`
   - Создать `ExtractedAttributeValue` для каждого параметра каждого типа → `extracted_attribute_values`

**Важно:** `type_id` = имя типа (string). Атрибуты сохраняются через `ReplaceSnapshotAsync`.

### Этап 5. DnD с разветвлением (виртуальный vs реальный)

**Цель:** Разная логика загрузки для реальных и виртуальных типов.

**Файлы:**
- `SmartCon.Core/Services/Interfaces/IFamilyLoadService.cs` — добавить `LoadFamilySymbolAsync`
- `SmartCon.Revit/FamilyManager/RevitFamilyLoadService.cs` — реализация `LoadFamilySymbolAsync`
- `SmartCon.Revit/FamilyManager/FamilyPlacementDropHandler.cs` — разветвление по `IsVirtual`

**Логика:**
- **Реальный тип:** `LoadFamilySymbol(filename, typeName, IFamilyLoadOptions)`
- **Виртуальный тип:** `LoadFamily(filename, IFamilyLoadOptions)` (Revit создаёт default type)

**Важно:** `IFamilyLoadOptions` обязателен для `LoadFamilySymbol` — Revit может попытаться перезагрузить семейство даже при загрузке одного типа.

### Этап 6. Обновление FamilyPlacementDragData

**Цель:** Передача флага `IsVirtual` в DropHandler.

**Файл:** `SmartCon.Core/Models/FamilyManager/FamilyPlacementDragData.cs`

**Изменения:**
- Добавить поле `IsVirtual: bool`

### Этап 7. Обновление CanStartPlacementDrag

**Цель:** Разрешить DnD для всех типов (включая виртуальные).

**Файл:** `SmartCon.FamilyManager/ViewModels/FamilyManagerMainViewModel.LoadPlace.cs`

**Изменения:**
- Убрать проверку на виртуальный тип из `CanStartPlacementDrag`
- Все типы доступны для DnD если семейство Active и есть права

### Этап 8. Окно свойств — виртуальный тип

**Цель:** Показывать виртуальный тип в списке типов окна свойств.

**Файл:** `SmartCon.FamilyManager/ViewModels/FamilyPropertiesViewModel.cs`

**Изменения:**
- В `LoadAttributesDataAsync`: если `HasTypes == false`, добавить виртуальный `FamilyTypeSelectorItem` с `TypeId = null`
- В `OnSelectedTypeChanged`: если `TypeId == null` — показывать `untypedValues` (атрибуты без типа)

### Этап 9. Управление Type Catalog при версионировании

**Цель:** Type Catalog должен «путешествовать» с семейством.

**Файлы:**
- `SmartCon.FamilyManager/Services/LocalCatalog/LocalFamilyImportService.cs` — при создании новой версии копировать .txt
- `SmartCon.FamilyManager/Services/LocalCatalog/LocalFamilyStorageRenameService.cs` — при переименовании переименовывать .txt

**Правила:**
- .txt всегда лежит рядом с .rfa в managed storage
- Имена файлов совпадают (только расширение отличается)
- При создании новой версии — копировать .txt из предыдущей версии
- При переименовании — переименовывать .txt вместе с .rfa

### Этап 10. Миграция БД

**Цель:** Добавить индекс для оптимизации поиска типов по имени.

**Файл:** `SmartCon.FamilyManager/Services/LocalCatalog/LocalCatalogMigrator.cs`

**Миграция V10:**
- Создать индекс `ix_family_types_name` на `family_types(type_name)`

### Этап 11. Тестирование

**Unit-тесты:**
- `TypeCatalogParserTests` — парсинг различных форматов CSV, кавычки, спецсимволы, кодировки
- `TypeCatalogEntryTests` — корректность структуры данных

**Интеграционные тесты:**
- Импорт .rfa с Type Catalog — проверка `family_types` и `extracted_attribute_values`
- Импорт .rfa без типов — проверка отображения виртуального типа
- DnD реального типа — проверка вызова `LoadFamilySymbol`
- DnD виртуального типа — проверка вызова `LoadFamily`
- Переименование семейства — проверка переименования .txt

---

## Ключевые архитектурные решения

| Вопрос | Решение | Обоснование |
|---|---|---|
| Создавать тип при импорте если нет типов? | **Нет** | Модификация .rfa меняет SHA256 → ломает дедупликацию |
| Где хранить Type Catalog (.txt)? | **Рядом с .rfa** | Revit API требует чтобы .txt лежал рядом с .rfa с тем же именем |
| Показывать типы из .rfa если есть Type Catalog? | **Нет, только Type Catalog** | Type Catalog = единственный источник типов |
| type_id для Type Catalog типов? | **Имя типа (string)** | Как сейчас для обычных типов. GUID не используем — нечитаемо в UI |
| Индикаторы загрузки типов? | **Нет** | Пользователю не важно состояние, главное — результат DnD |
| Парсинг Type Catalog — когда? | **При импорте (eagerly)** | Данные должны быть доступны сразу для отображения в дереве |

---

## Сводка изменений по проектам

| Проект | Файлы | Тип изменения |
|---|---|---|
| **SmartCon.Core** | `Models/FamilyManager/TypeCatalogEntry.cs` | Новый |
| **SmartCon.Core** | `Models/FamilyManager/TypeCatalogParseResult.cs` | Новый |
| **SmartCon.Core** | `Services/Implementation/TypeCatalogParser.cs` | Новый |
| **SmartCon.Core** | `Models/FamilyManager/FamilyPlacementDragData.cs` | Добавить `IsVirtual` |
| **SmartCon.Core** | `Services/Interfaces/IFamilyLoadService.cs` | Добавить `LoadFamilySymbolAsync` |
| **SmartCon.Revit** | `FamilyManager/RevitFamilyLoadService.cs` | Реализация `LoadFamilySymbolAsync` |
| **SmartCon.Revit** | `FamilyManager/FamilyPlacementDropHandler.cs` | Разветвление virtual/real |
| **SmartCon.FamilyManager** | `ViewModels/FamilyTypeNodeViewModel.cs` | Добавить `IsVirtual` |
| **SmartCon.FamilyManager** | `ViewModels/FamilyManagerMainViewModel.Tree.cs` | + виртуальный тип, - фильтрация |
| **SmartCon.FamilyManager** | `ViewModels/FamilyManagerMainViewModel.LoadPlace.cs` | DnD для всех типов |
| **SmartCon.FamilyManager** | `ViewModels/FamilyPropertiesViewModel.cs` | + виртуальный тип в AvailableTypes |
| **SmartCon.FamilyManager** | `Services/LocalCatalog/LocalFamilyImportService.cs` | + Type Catalog импорт |
| **SmartCon.FamilyManager** | `Services/LocalCatalog/FamilyDataImportService.cs` | + Type Catalog атрибуты |
| **SmartCon.FamilyManager** | `Services/LocalCatalog/LocalFamilyTypeRepository.cs` | Убрать `type_name <> ''` |
| **SmartCon.FamilyManager** | `Services/LocalCatalog/LocalCatalogMigrator.cs` | Миграция V10 |
| **SmartCon.Tests** | `FamilyManager/TypeCatalogParserTests.cs` | Новый |

---

## Порядок реализации (рекомендуемый)

1. **Этап 1** — TypeCatalogParser (независимый, unit-testable)
2. **Этап 2-3** — Виртуальный тип в дереве (изменения в ViewModel)
3. **Этап 4** — Интеграция Type Catalog в импорт (изменения в ImportService)
4. **Этап 5-7** — DnD с разветвлением (изменения в Revit-слое)
5. **Этап 8** — Окно свойств (изменения в PropertiesViewModel)
6. **Этап 9** — Версионирование и переименование (изменения в RenameService)
7. **Этап 10** — Миграция БД
8. **Этап 11** — Тестирование и отладка

---

## Связанные документы

- `docs/invariants.md` — I-01 (ExternalEvent), I-03b (Transaction для family doc), I-09 (Dependency Rule), I-16 (ReadOnly Storage)
- `docs/architecture/dependency-rule.md` — Правило зависимостей между слоями
- `docs/domain/models.md` — FamilyTypeDescriptor, ExtractedAttributeValue
- `docs/domain/interfaces.md` — IFamilyLoadService, IFamilyPlacementDragService
- `docs/family-manager/README.md` — Архитектура модуля FamilyManager

---

*План составлен на основе глубокого анализа кодовой базы, исследования Revit API (Exa, Jeremy Tammik, Autodesk Docs) и бизнес-уточнений.*
