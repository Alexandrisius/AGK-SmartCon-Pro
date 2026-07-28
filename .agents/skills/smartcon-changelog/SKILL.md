---
name: smartcon-changelog
description: SmartCon release changelog authoring. Use ALWAYS when the user asks to prepare release notes / changelog text / "текст релиза" for a stable release or a beta, before running tools/release.ps1, or when archiving a published release changelog into docs/changelogs/. Covers branch selection (main for stable, develop for beta), commit/issue analysis since the previous tag, user-facing writing style (benefit-first, no file lists, no test counts), exact section structure with emojis matching published GitHub releases, archived file naming, and passing the file to release.ps1 via -ChangelogFile.
---

# SmartCon Changelog

Формирование текста релиза (changelog) для stable и beta версий SmartCon.
Эталон стиля — опубликованные релизы: `https://github.com/Alexandrisius/AGK-SmartCon-Pro/releases`
(архив копий — `docs/changelogs/`).

## When to use

- Пользователь просит «подготовь текст релиза / changelog / release notes»
- Перед запуском `tools/release.ps1` (stable или `-Prerelease`)
- При архивировании changelog уже опубликованного релиза в `docs/changelogs/`

## Архив в репозитории

Все changelog-файлы (stable + beta) хранятся в репозитории: **`docs/changelogs/`**.
Имя файла = тег релиза: `v2.0.0.md`, `v2.0.1-beta.6.md`.
После каждого опубликованного релиза его текст **обязательно** сохраняется сюда
(источник — `gh release view <tag> --json body --jq .body`).

> **Кодировка при выгрузке из `gh` (критично):** перед любыми вызовами `gh`,
> захватывающими русский текст в переменную, выстави
> `[Console]::OutputEncoding = [System.Text.Encoding]::UTF8` — иначе PowerShell
> декодирует UTF-8 вывод как CP1251 и файл запишется кракозябрами.
> Писать файл — `[System.IO.File]::WriteAllText($path, $body, [System.Text.UTF8Encoding]::new($false))`.
> После записи ОБЯЗАТЕЛЬНО перечитай файл с диска и проверь кириллицу глазами.

## Pipeline

### 1. Определи тип релиза и ветку анализа

| Тип | Ветка | База отсчёта |
|---|---|---|
| Stable (`v2.1.0`) | `main` | предыдущий stable-тег |
| Beta (`v2.0.1-beta.7`) | `develop` | предыдущий тег (обычно предыдущая beta) |

### 2. Собери фактуру

```powershell
git tag --sort=-creatordate | Select-Object -First 10
git log <prevTag>..<branch> --oneline --no-merges
gh issue list --state closed --search "closed:>=<дата prevTag>" --limit 100
```

Для **stable** дополнительно просмотри все beta-релизы между stable-тегами
(`gh release list`) — их содержимое входит в stable, но переписывается заново
для широкой аудитории (не копируй beta-списки как есть).

### 3. Напиши changelog по правилам стиля (ниже)

### 4. Покажи пользователю на утверждение → сохрани в `docs/changelogs/v<version>.md`

### 5. Передай файл в релиз-скрипт

```powershell
tools\release.ps1 -ChangelogFile docs\changelogs\v<version>.md                 # stable
tools\release.ps1 -Prerelease -ChangelogFile docs\changelogs\v<version>.md     # beta
```

Без `-ChangelogFile`/`-Changelog` скрипт спросит текст интерактивно — для агента
это тупик. Всегда готовь файл заранее.

## Правила стиля (нарушение = переделка)

Полные шаблоны и эталонные выдержки: `references/templates.md`.

### Пиши для пользователя, не для команды

- Аудитория — MEP-инженеры и дистрибьюторы, НЕ программисты. Язык — русский.
- Каждая запись отвечает на два вопроса: «что изменилось?» и «зачем мне это?».
- Benefit-first: «Теперь семейство можно перетащить прямо в окно Revit»,
  а не «Реализован DragDropService».
- Тест: поймёт ли запись самый нетехнический пользователь? Нет — переписывай.

### Что НЕ включаем никогда

- Списки изменённых файлов, количество пройденных тестов, «build succeeded»
- Рефакторинги, бампы зависимостей, CI/CD-правки, чистку кода
- Внутренние имена классов/сервисов в пользовательском тексте
  (Issue-номера — только как якоря в скобках: `(#168)`)
- Фразу «различные исправления и улучшения» — это признак лени

### Баг-фикс = симптом + следствие

Пользователь сканирует фиксы в поиске СВОЕГО бага — назови его так,
как пользователь его видел:

> **Семейства без типов не загружались в проект** (#172) — регрессия: безымянный
> тип хранился под служебным именем, «Разместить тип» не находил его.

### Breaking changes

Отдельная секция `## ⚠️ Важно перед обновлением` В НАЧАЛЕ файла: что сломается,
почему так сделано, пронумерованные шаги миграции. Эталон — `v2.0.0`
(несовместимость каталогов 1.9.3 → 2.0.0).

### Структура stable-релиза

```
**Дата релиза:** …  **Предыдущий стабильный релиз:** …  **Платформы:** Revit 2019–2026
## ⚠️ Важно перед обновлением   (только при breaking changes)
## 🔥 Главное в этом релизе      (1 абзац + Топ-10 нумерованным списком)
## ✨ Новые возможности           (### подразделы по фичам с эмодзи)
## 🔧 Улучшения
## 🐛 Исправленные ошибки
## 🔄 Как обновиться              (автообновление + шаги после обновления)
## 📋 Совместимость               (таблица Revit 2019–2026 — копируй из эталона)
## 🙏 Благодарности / Обратная связь
*футер: закрыто задач N (#…), период разработки*
```

### Структура beta-релиза

Та же, но:
- заголовок `# SmartCon v<version>` + строка
  `**Статус:** Pre-release (beta) — предназначена для тестирования`
- вместо `🔄 Как обновиться` → `## 🧪 Как тестировать beta`: пронумерованные
  сценарии по каждой фиче/фиксу + просьба прислать `smartcon.log`
  (`%APPDATA%\AGK\SmartCon\smartcon.log`)
- «Главное» короче: Топ-3, а не Топ-10

### Эмодзи и форматирование

- Эмодзи в заголовках секций обязательны (соответствие опубликованным релизам)
- **Bold** на ключевом существительном каждой записи — текст сканируют по диагонали
- Запись = 1–3 предложения; детали — ссылкой, а не простынёй
- Пустые секции удаляются (нет фиксов → нет секции 🐛)

## Checklist перед показом пользователю

- [ ] Ветка верная (stable→main, beta→develop), база — правильный предыдущий тег
- [ ] Каждый закрытый issue за период либо отражён записью, либо осознанно пропущен (внутренний)
- [ ] Нет файлов/тестов/рефакторингов в тексте
- [ ] Каждый фикс назван симптомом пользователя
- [ ] Breaking changes — в секции ⚠️ с шагами миграции
- [ ] Файл сохранён в `docs/changelogs/v<version>.md`
