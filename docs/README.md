# SmartCon — SSOT (Single Source of Truth)

> **Версия:** 1.3.0 (см. `Version.txt`) | **Платформа:** Revit 2019-2025 / .NET Framework 4.8 + .NET 8 / C# 12 / WPF
> **Последнее обновление:** 2026-04-19

Этот файл — **единая точка входа** в документацию проекта SmartCon.
AI-агент должен загрузить этот файл первым, затем подгружать нужные разделы по контексту задачи.

---

## Что такое SmartCon

SmartCon — плагин для Autodesk Revit, автоматизирующий рутинные MEP-операции.
Флагманский модуль — **PipeConnect**: соединение трубных элементов на любых видах двумя кликами с автоматическим подбором фитингов, параметров и типов соединений.

**Целевой пользователь:** MEP-инженер-проектировщик.

**Ключевая боль:** Revit не умеет удобно соединять элементы в 3D-виде — приходится тягать коннекторы и надеяться на совпадение координат в пространстве. Нет системы типов соединений (резьба/сварка/раструб). Нет умного подбора фитингов-переходников.

---

## Карта документации

Загружай документы по мере необходимости. Колонка «Когда загружать» — подсказка.

### Архитектура

| Документ | Описание | Когда загружать |
|---|---|---|
| [`architecture/solution-structure.md`](architecture/solution-structure.md) | Проекты, папки, файлы каждого слоя | Всегда при создании/перемещении файлов |
| [`architecture/dependency-rule.md`](architecture/dependency-rule.md) | Правило зависимостей между слоями | Всегда |
| [`architecture/tech-stack.md`](architecture/tech-stack.md) | Стек технологий, версии, NuGet-пакеты | При настройке проекта или добавлении зависимостей |

### Домен

| Документ | Описание | Когда загружать |
|---|---|---|
| [`domain/models.md`](domain/models.md) | Все доменные классы с полными сигнатурами | При работе с моделями данных |
| [`domain/interfaces.md`](domain/interfaces.md) | Все интерфейсы-контракты с сигнатурами методов | При реализации или вызове сервисов |
| [`domain/glossary.md`](domain/glossary.md) | Единый словарь терминов проекта | При любых сомнениях в терминологии |

### PipeConnect (флагманский модуль)

| Документ | Описание | Когда загружать |
|---|---|---|
| [`pipeconnect/state-machine.md`](pipeconnect/state-machine.md) | Диаграмма состояний, переходы, правила | При работе с логикой PipeConnect |
| [`pipeconnect/algorithms.md`](pipeconnect/algorithms.md) | Алгоритмы: выравнивание, параметры, фитинги, цепочки | При реализации алгоритмов |
| [`pipeconnect/ui-spec.md`](pipeconnect/ui-spec.md) | Спецификация UI: окна, layout, MVVM-паттерны | При работе с UI |
| [`pipeconnect/business-cases.md`](pipeconnect/business-cases.md) | Бизнес-кейсы: логика при разных сценариях коннекта, reducer, размеры | При реализации логики соединения |

### ProjectManagement (модуль шаринга проектов)

| Документ | Описание | Когда загружать |
|---|---|---|
| [`projectmanagement/README.md`](projectmanagement/README.md) | Индекс модуля, список файлов, ключевые решения | При любой работе с модулем |
| [`projectmanagement/share-algorithm.md`](projectmanagement/share-algorithm.md) | Алгоритм Share: 8 шагов, обработка ошибок, категории очистки | При реализации ShareProjectService |
| [`projectmanagement/ui-spec.md`](projectmanagement/ui-spec.md) | Спецификация UI: ShareSettingsView (4 таба), ShareProgressView | При работе с UI |
| [`projectmanagement/naming-template.md`](projectmanagement/naming-template.md) | Парсер имён файлов: блоки, роли, маппинг статусов, JSON-формат | При реализации FileNameParser |

### Правила и решения

| Документ | Описание | Когда загружать |
|---|---|---|
| [`invariants.md`](invariants.md) | Жёсткие правила I-01..I-13. Нарушение = баг. | **ВСЕГДА** |
| [`multi-version-guide.md`](multi-version-guide.md) | Стандарт multi-version: 10 правил, шаблоны, чеклист | При создании нового функционала |
| [`adr/README.md`](adr/README.md) | Индекс Architecture Decision Records | При вопросах «почему так сделано?» |
| [`roadmap.md`](roadmap.md) | Фазы разработки, зависимости, критерии приёмки | При планировании работ |
| [`references.md`](references.md) | Внешние ссылки на документацию Revit API | При работе с конкретными API |
| [`future-work.md`](future-work.md) | Tracking TODO-комментариев и planned features ([ChainV2], [Phase 6B]) | При работе над новыми фичами |

---

## Быстрый старт для AI-агента

1. **Загрузи** этот файл (`docs/README.md`)
2. **Загрузи** [`invariants.md`](invariants.md) — жёсткие правила, обязательные всегда
3. **Загрузи** [`architecture/dependency-rule.md`](architecture/dependency-rule.md) — чтобы понимать куда класть код
4. **По задаче** загружай нужные документы из карты выше
5. **Не создавай** новые доменные классы без обновления [`domain/models.md`](domain/models.md)
6. **Не создавай** новые интерфейсы без обновления [`domain/interfaces.md`](domain/interfaces.md)

---

## Текущий статус

| Модуль | Статус | Примечание |
|---|---|---|
| SmartCon.Core | ✅ Полный | Модели, интерфейсы, алгоритмы, FormulaSolver |
| SmartCon.Revit | ✅ Полный | Все Revit API реализации |
| SmartCon.UI | ✅ Полный | Тема, стили, контролы, конвертеры |
| SmartCon.App | ✅ Полный | Ribbon, DI, ExternalEvents, Updater |
| SmartCon.PipeConnect | ✅ Полный | PipeConnect: 5 partial VM, 12 сервисов, 6 окон |
| SmartCon.ProjectManagement | 📋 Планирование | Share Project: ISO 19650, ADR-013 |
| SmartCon.Tests | ✅ 676 тестов, 0 ошибок | Unit + ViewModel тесты (xUnit + Moq) |

**OSS-perfection refactor (2026-04-19):** Фазы A-J по плану `oss-perfection-plan.md` завершены.
Clean Architecture, MVVM compliance, XML-docs, ExtensibleStorage, CI matrix, dependency updates.
