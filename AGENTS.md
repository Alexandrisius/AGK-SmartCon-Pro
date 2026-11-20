Отвечай всегда на русском языке.

# SmartCon — Системная инструкция для AI-агентов

Ты работаешь над проектом **SmartCon** — плагином для Autodesk Revit 2019-2027 (net48 / .NET 8 / .NET 10, C# 12 / WPF).

**Тулчейн (важно):** сборка идёт на **.NET SDK 8.0.4xx** (`global.json`). Revit 2027 / net10 **временно выключен** флагом `SmartConEnableRevit2027=false` (`src/Directory.Build.props`): любой SDK 10 собирает этот проект в 10–20 раз медленнее (dotnet/roslyn#81704). `-c Debug.R27`/`Release.R27` падают с явной ошибкой — не запускать.

**Переключатель версий — ровно один:** `<SmartConEnableRevit2027>` в `src/Directory.Build.props`:

| Значение | Что происходит |
|---|---|
| `false` (сейчас) | В сборке 6 конфигураций: R19, R21, R23, R24, R25, R26. R27/net10 нет; `-c Debug.R27` падает с понятной ошибкой |
| `true` | R27 (net10) возвращается: `tools\build-all-configs.ps1`, `tools\release.ps1` и CI-матрица `.github/workflows/build.yml` подхватывают его автоматически. R27 собирается из `tools\sdk10` (свой `global.json` на SDK 10.0.4xx — SDK 10 должен быть установлен) |

Корневой `global.json` при переключении менять **не нужно** — он всегда пинит SDK 8.0.4xx.
Причина и все замеры — `docs/known-workarounds.md` (dotnet/roslyn#81704).
Флагманский модуль — **PipeConnect**: соединение трубных MEP-элементов в 3D двумя кликами.

Исходный код расположен в `src/`. Solution: `src/SmartCon.sln`.

## GitHub Repository

| Параметр | Значение |
|---|---|
| Owner | `Alexandrisius` |
| Repo | `AGK-SmartCon-Pro` |
| Full | `Alexandrisius/AGK-SmartCon-Pro` |

## Quick Reference

| Действие | Команда |
|---|---|
| Карта работ агента | `docs/agent/ROADMAP.md` |
| Открытые задачи агента | `gh issue list --label agent --state open --limit 200` |
| Очередь к работе | `gh issue list --label ready` |
| Прочитать задачу | `gh issue view <N>` |
| Обновить issue/epic | `gh issue edit <N> --body-file <file>` (полная замена тела) |
| Закрыть задачу | `gh issue close <N>` |
| R27 (Revit 2027) | **выключен:** `SmartConEnableRevit2027=false` (`src/Directory.Build.props`), `global.json` пинит SDK 8. Причина — см. `docs/known-workarounds.md` |
| Собрать R26 | `dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R26` |
| Собрать R25 | `dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R25` |
| Собрать R24 | `dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R24` |
| Собрать все 6 (параллельно, без деплоя) | `powershell -NoProfile -ExecutionPolicy Bypass -File tools\build-all-configs.ps1` |
| Тесты | `dotnet test src/SmartCon.Tests/SmartCon.Tests.csproj -c Debug.R25` (проект пиннут на net8.0-windows — НЕ собирать под R27) |
| Интеграционные тесты R27 | **не запускать** — R27/net10 выключен (см. выше) |
| Интеграционные тесты R25 | `dotnet run --project src/SmartCon.IntegrationTests -c Debug.R25 --framework net8.0-windows` |
| Интеграционные тесты net48 (R21-R24) | `dotnet run --project src/SmartCon.IntegrationTests -c Debug.R21 --framework net48 -p:RevitVersion=2023` (версия = любой **установленный** Revit 2021-2024; суть прогона — платформа net48, а не конкретный год) |
| Собрать все + деплой | `build-and-deploy.bat` |
| Release | `tools\release.bat` |

## Обязательная подготовка перед ЛЮБОЙ задачей

**Перед началом работы агент ОБЯЗАН:**

1. **Прочитать обязательные файлы** (в этом порядке):
   - `docs/README.md` — понять контекст проекта
   - `docs/invariants.md` — выучить жёсткие правила I-01..I-17
   - `docs/architecture/dependency-rule.md` — понять куда класть код, карту проектов и конвенции файлов (размер/partial)
   - `docs/known-workarounds.md` — проверить, не существует ли уже workaround для этой проблемы
   - `docs/agent/ROADMAP.md` — **при работе над AI-агентом**: найти эпик/задачу и issue

2. **Провести глубокое исследование через Exa**:
   - **НИКОГДА не предполагай** — Revit API нишевый, нейросети на нём плохо обучены
   - Задай **минимум 5 запросов** в `exa_web_search_exa` на элементарные вопросы:
     * «Нужна ли транзакция для [метод]?»
     * «Jeremy Tammik [метод/функционал]»
     * «open source Revit plugin [функционал] GitHub»
     * «Autodesk Revit API forum [проблема]»
     * «Revit [версия] [метод] best practice»
   - Проверь **каждую гипотезу** в Exa прежде чем её высказать
    - **Цель:** собрать 3-5 авторитетных источников (Autodesk docs, Jeremy Tammik, StackOverflow с accepted answer, GitHub open source plugins) прежде чем писать код

3. **Проверить issue и решить, нужны ли субагенты:**
   - Открой `docs/agent/ROADMAP.md` и найди эпик/задачу; прочитай тело issue (`gh issue view <N>`).
   - Субагенты-исследователи запускаются **только при реальной подготовке к сложной фиче**:
     несколько модулей, незнакомый API, большой объём чтения (>5–7 файлов), независимые направления поиска.
   - **Не запускай субагентов** для локальной правки, поиска одного факта, создания issue,
     правки документации или бага с известным местом — сделай сам, это быстрее.

**ЗАПРЕЩЕНО начинать работу без выполнения пунктов 1-3.**

## Подход к исследованию: сначала факты, потом код

### Правило: никаких предположений без проверки

**Revit API нишевый.** Нейросети плохо обучены на нём. **Любое предположение без Exa-проверки — потенциальный баг.**

**Элементарные вопросы для Exa (задавать всегда):**

| Что ищем | Пример запроса в Exa |
|---|---|
| Нужна ли транзакция? | «Revit API [MethodName] requires transaction» |
| Как правильно использовать метод? | «Jeremy Tammik [MethodName] best practice» |
| Как делают другие? | «open source Revit plugin [feature] GitHub C#» |
| Известные проблемы | «Autodesk Revit API forum [MethodName] crash» |
| Альтернативные API | «Revit [version] [MethodName] vs [AlternativeMethod]» |

**Качество источников (по убыванию авторитетности):**
1. Autodesk official docs / API reference
2. Jeremy Tammik blog (thebuildingcoder.com)
3. StackOverflow с accepted answer
4. GitHub open source plugins (проверенные репозитории)
5. Autodesk Community forums

**Минимальный набор перед кодированием:**
- 3-5 авторитетных источников по каждому незнакомому API
- Подтверждение что метод работает в целевой версии Revit (2025/2024)
- Проверка что паттерн использования совпадает с нашей архитектурой (ITransactionService, ExternalEvent)

### Правило при застревании: углубляй исследование

**Если задача не решается с 1-2 попыток — значит не хватает контекста. НЕ лечи симптом, ищи корень.**

**Что делать:**
1. Составь список гипотез (минимум 3)
2. По каждой гипотезе — отдельный Exa-запрос
3. Ищи GitHub open source плагины с похожей проблемой
4. Ищи Jeremy Tammik blog по ключевым словам
5. Запусти субагента для анализа связанных модулей

**Что НЕ делать:**
- Менять код наугад "а вдруг поможет"
- Повторять один и тот же поиск в Exa
- Строить теории только на статическом анализе
- Предполагать "наверное так работает"

## Архитектура работы агентов

### Главный агент = Разработчик + Оркестратор

Главный агент **пишет весь сложный код**. У него полный контекст проекта, доступ к документации
и прямое взаимодействие с пользователем — это делает его самым надёжным исполнителем.

**Ответственность главного агента:**
- Написание нового кода и сложная логика
- Архитектурные решения и интеграция изменений
- Исправление багов (любой сложности)
- Использование поиска (Exa, Context7, MCP Revit API docs) для сбора контекста
- Приём и валидация результатов субагентов
- Изменения в конфигурационных файлах

### Субагенты = Исследователи (только для сложной подготовки)

Субагенты (Task tool) используются **только для исследования** — они не пишут основной код.
Их главная ценность — сохранение контекста главного агента: каждый файл, каждый поиск,
каждый тупик потребляет токены. Субагенты берут эту нагрузку на себя и возвращают сжатый результат.

**Субагент — инструмент, а не ритуал. Главный агент запускает его только когда задача
действительно требует подготовки. Если задачу можно сделать самому за 1–3 шага — делай сам.**

**Когда запускать:**
- фича затрагивает 3+ модуля или незнакомый Revit API;
- нужно прочитать/сравнить >5–7 файлов;
- есть несколько независимых направлений исследования (параллельно);
- монотонный рефакторинг с чётко заданным scope.

**Когда НЕ запускать:**
- правка одного файла или функции;
- поиск одного факта/символа (`rg` быстрее);
- создание/обновление issue, ROADMAP, документации;
- баг с известной локацией;
- любая задача, которую главный агент закрывает за 1–3 шага.

**Правило:** сомневаешься — сделай сам. Субагент дороже по времени и требует перепроверки результата.

**В арсенале два субагента** (параметр `subagent_type` в Task tool):

| Субагент | Характер | Когда использовать |
|---|---|---|
| `general` | Самая умная, но медленная модель с большим контекстом — помнит всё | **Обязательная валидация этапов** (см. gate ниже), сложный анализ нескольких модулей, исследования, требующие полного контекста |
| `explore` | Менее умная, но очень быстрая модель с небольшим контекстом (достаточно для изучения функционала одной фичи) | Быстрые read-only исследования: найти файлы, изучить функционал одной фичи, поиск паттернов, навигация по кодовой базе |

**Область применения субагентов:**
- Исследование кодовой базы (поиск файлов, чтение, анализ структуры)
- Изучение документации и внешних источников (Exa, Context7, MCP Revit API docs)
- Поиск паттернов и анализ зависимостей между модулями
- Монотонный рефакторинг с чётко заданным scope (переименование, замена паттернов)
- Параллельный поиск в нескольких направлениях

**Почему субагенты НЕ пишут сложный код:**
- Ограниченный контекст — субагент не видит полную картину проекта
- Высокий риск ошибки при сложной логике, даже с детальным промптом
- Нет доступа к интерактивному уточнению у пользователя
- Результат субагента нужно перепроверять — проще написать сразу правильно

**Правила использования субагентов:**
- Запускай субагентов параллельно для независимых исследований
- Каждый субагент — одна конкретная задача с чётким expected output
- Субагент получает полный контекст задачи в prompt, но не весь проект
- Главный агент анализирует результаты и принимает решение о реализации
- Если результат субагента недостаточен — запускай другой, не додумывай

### Обязательный gate: валидация этапа субагентом `general`

Каждый логический этап работы **завершается валидацией субагентом `general`**:
- каждая фаза большого плана,
- каждый issue (баг или фича),
- реализация плана целиком (финальная валидация).

**Главный агент НЕ переходит дальше**, пока `general` не проверил всё сделанное на текущем этапе.

**Что передавать в prompt валидатору:**
- что планировалось сделать (фаза / issue / пункт плана) и критерии приёмки,
- список изменённых файлов (`git status`, `git diff --stat`),
- результаты сборки и тестов,
- просьба проверить: соответствие инвариантам I-01..I-17 и dependency rule, полноту реализации (нет ли забытых мест — например DI-регистрации, локализации, multi-version), регрессии в смежных модулях.

**Если валидатор нашёл проблемы** — исправить и прогнать валидацию повторно.
Переход к следующему этапу только после чистого вердикта `general`.

### Правило стоп-крана: не гадай — наблюдай

**Правило: если за 2-3 итерации не понял причину бага — СТОП. Выбери одно из:**

1. **Добавь логирование** — вставь `SmartConLogger` в ключевых точках и попроси пользователя воспроизвести
2. **Поищи в Exa** — `exa_web_search_exa` занимает 10 секунд и часто даёт ответ сразу
   - Задай **минимум 3 новых запроса** (не те же что раньше!)
   - Ищи по другим ключевым словам
   - Ищи на GitHub open source плагины с похожей проблемой
   - Ищи Jeremy Tammik blog
3. **Спроси пользователя** — через `question` tool

**Антипаттерны:**
- Читать одни и те же файлы по кругу, надеясь заметить что-то новое
- Строить теории на основе статического анализа кода без runtime-данных
- Пытаться «починить вслепую» — менять код наугад и надеяться что поможет
- Думать дольше 5 минут над багом без привлечения внешних источников или пользователя
- Задавать в Exa только 1-2 запроса и считать что "поискал"

**Паттерн отладки (правильный):**
```
Гипотеза → Exa-проверка (3-5 источников) → Логирование → Воспроизведение → Данные → Точечный фикс
```

**Паттерн отладки (неправильный):**
```
Чтение кода → Чтение кода → Ещё чтение кода → Угадывание → Фикс наугад
```

## Коммуникация с пользователем

**ЗАПРЕЩЕНО** задавать вопросы пользователю текстом в ответе.
**ОБЯЗАТЕЛЬНО** использовать `question` tool для уточнения требований, выбора варианта реализации,
запроса недостающей информации, подтверждения архитектурных решений.

### Собирай контекст ДО изменения кода

**Порядок работы при любой задаче:**

1. **Исследование** → прочитать docs, запустить субагентов, поискать в Exa
2. **Уточнение** → задать вопросы через `question` tool если что-то непонятно
3. **Только потом** → вносить изменения в код

**ЗАПРЕЩЕНО** начинать менять код пока не собран весь контекст и не уточнены
все неоднозначности у пользователя. Переделывать дороже чем спросить заранее.

### Ручной тест в Revit + валидация логов — единственный источник истины для UI

**Для UI/E2E-сценариев ЕДИНСТВЕННЫЙ достоверный способ проверки — валидация логов ручного теста в Revit.** Сборка, юнит-тесты и статический анализ НЕ доказывают, что задача реализована.
**Для границы SmartCon ↔ Revit API (DB-уровень) источник истины — интеграционные тесты** (`SmartCon.IntegrationTests`, см. следующий раздел): они выполняют production-код внутри реального Revit и не требуют ручного прогона.

**До подтверждения пользователем главный агент НЕ трогает файлы, не влияющие на работу приложения:**
- документацию `docs/` (включая domain models/interfaces, статусы в `docs/README.md`),
- ADR,
- changelog (`docs/changelogs/`),
- GitHub Issues (Resolution-комментарии, закрытие),
- коммиты, пуши, PR.

**Полный порядок завершения задачи:**
1. Реализация кода + сборка + тесты + валидация каждого этапа субагентом `general` (gate выше)
2. **Интеграционные тесты обязательны**, если задача трогает Revit-boundary код (`SmartCon.Revit`, ExtensibleStorage, транзакции, коннекторы, коллекторы, LoadFamily и т.п.):
   - существующее поведение покрыто? → прогнать сьют (`dotnet run --project src/SmartCon.IntegrationTests -c Debug.R25 --framework net8.0-windows`) и убедиться, что зелёный
   - новая Revit-boundary логика? → **добавить интеграционный тест** по образцу соседнего класса модуля (см. раздел ниже и skill `smartcon-testing`)
3. Перед ручным тестом: убедиться, что ключевые точки покрыты `Debug`-логами (skill `smartcon-logging`) — по логу должно быть видно, что каждый важный сценарий отработал. Нет покрытия → добавить логирование ДО ручного теста
4. Агент предлагает ручной тест: чёткий сценарий (что нажать, что ожидать) + просит прислать лог
5. Пользователь тестирует в Revit и присылает лог: `C:\Users\klim9\AppData\Roaming\AGK\SmartCon\smartcon.log`
6. Агент валидирует лог: нет необъяснимых `Error`/`Warn`, ключевые операции присутствуют и завершились, цепочки `OpId` целые, поведение соответствует ожиданию
7. **Только если лог чистый и поведение верное** — агент решает, что задача реализована на 100%, и дальше (по запросу пользователя): правит docs/ADR, формирует changelog (skill `smartcon-changelog`), делает коммит, закрывает issues

**Причина:** мусорные коммиты с багами плодятся, когда агент спешит закоммитить
до проверки. UI-поведение Revit-плагина нельзя протестировать автотестами —
только ручной запуск в Revit с последующей валидацией логов. Модельную логику
(DB-уровень) при этом покрываем интеграционными тестами — см. следующий раздел.

## Интеграционные тесты (SmartCon.IntegrationTests)

`src/SmartCon.IntegrationTests` — тесты, выполняемые **внутри реального процесса Revit**
(TUnit + Nice3point.TUnit.Revit): инжектор запускает настоящий Revit нужной версии
и маршалит каждый тест на его API-поток. Это НЕ моки — настоящие `Document`,
`Element`, `Connector`, `Transaction`, `FilteredElementCollector`, ExtensibleStorage.

**Полное руководство — skill `smartcon-testing` → `references/integration-testing.md`:**
как запускать, как писать (по образцу соседних классов модуля), **9 жёстких правил**
(lazy-поля, `[assembly: NotInParallel]`, запрет RevitAPIUI, skip-гарды и др. —
нарушение = краш сессии). Перед написанием интеграционного теста читай его обязательно.

### Что покрывать интеграционными тестами (цель: вся граница — для защиты от регрессий при рефакторингах)

| Тестируемо интеграционными тестами (DB-уровень, RevitAPI.dll) | НЕ тестируемо (UI-сессия, RevitAPIUI.dll) |
|---|---|
| Сервисы `SmartCon.Revit`: транзакции, коннекторы, параметры, transform, chain iterator, purge | `UIDocument`, `Selection`, `PickObjects` |
| ExtensibleStorage: маппинг фитингов, настройки Share, stale-маркеры | `TaskDialog`, диалоги, WPF-окна |
| Загрузка/извлечение семейств: `LoadFamily`, `OpenDocumentFile`, type catalog, snapshot/hash | Dockable-панели, Ribbon |
| Геометрия, коллекторы, формулы на реальных элементах | `ISelectionFilter`-классы (RevitAPIUI = краш хоста) |
| Сидированная модель: трубы, стены, виды, листы, ведомости | Worksharing UI (`OpenAndActivateDocument`) |

**Правило покрытия:** любой новый сервис в `SmartCon.Revit` или новая ветка
существующего — с интеграционным тестом в соответствующей папке модуля
(`PipeConnect/`, `FamilyManager/`, `ProjectManagement/`). Рефакторинг
Revit-boundary кода без зелёного интеграционного сьюта не считается завершённым.

**Запуск** (требуется установленный Revit соответствующей версии, ~60 сек на сьют):
```bash
dotnet run --project src/SmartCon.IntegrationTests -c Debug.R25 --framework net8.0-windows
dotnet run --project src/SmartCon.IntegrationTests -c Debug.R21 --framework net48 -p:RevitVersion=2023
# Подмножество: ... -- --treenode-filter "/*/*/*ClassName*/*"  (класс в 3-м сегменте!)
```

R26 компилируется (`-c Debug.R26`), но на машине без установленного Revit 2026 непрогоняем.
Прогон net10/R27 **не запускать** — R27 выключен (`SmartConEnableRevit2027=false`).

**Философия версий прогона (важно, не путать!):** суть сьюта — проверка **платформы**, а не конкретного года Revit. Обязательный минимум: **R25 (net8)** + **любой net48-прогон (Revit 2021-2024, по наличию на машине)** + **R27 (net10, новейший API — когда установлен Revit 2027)** — три платформы (net48/net8/net10). Инжектор Nice3point ищет Revit года `$(RevitVersion)` — поэтому на машине без Revit 2021 прогон `-c Debug.R21` падает с `FileNotFoundException: RevitAPI 21.0.0.0`; лечение — явный `-p:RevitVersion=YYYY` (глобальное свойство перекрывает пин из `Directory.Build.props`, год = любой установленный net48-Revit). По остаточному принципу, если установлено несколько версий, полезно гонять разные API (2021 минимальный / 2023-2024 промежуточные / 2025-2027 новейшие).

**Доказанная ценность:** сьют поймал production-баг #176 в день внедрения
(флаги PurgeSheets/PurgeSchedules не сохраняли листы/ведомости — невидимо для юнит-тестов).

## Работа через GitHub Issues — обязательный процесс

> С 2026-11-19 вся работа ведётся через GitHub Issues — **для всего приложения**.
> Первый контур, полностью размеченный эпиками и задачами, — **AI-агент**
> (`SmartCon.Agent`, `SmartCon.Revit/Agent`, панель агента): его **единственный
> рабочий документ** — `docs/agent/ROADMAP.md`.
> Стратегия, runbook'и, evidence и фидбэк AI-агента — в `docs/agent/archive/`:
> это история, а не руководство к действию.
> Остальные модули переводятся по мере задач: новая работа в них тоже заводится issue
> (метка модуля + `P0..P3`), но отдельной карты у них пока нет.
> Новых «планов» и runbook-файлов не создаём: план живёт в теле эпика.

### Иерархия источников правды

| Что | Где | Правило |
|---|---|---|
| Карта и контекст AI-агента | `docs/agent/ROADMAP.md` | **Единственный рабочий документ:** north star, приоритеты, `ID ↔ issue # ↔ статус` |
| Конкретная работа | GitHub Issue | **SSOT**: scope, критерии, статус, журнал |
| История и обоснования | `docs/agent/archive/` | Только исторический контекст; не руководство |
| Архитектурное решение | `docs/adr/` | Только необратимый выбор, не рабочие задачи |
| Инварианты проекта | `docs/invariants.md` | Жёсткие правила I-01…I-17 |

### Обязательный цикл работы

**Перед работой:**

1. Открой `docs/agent/ROADMAP.md` и найди эпик/задачу.
2. `gh issue view <N>` — прочитай тело целиком; оно самодостаточно.
3. Проверь зависимости и блокеры: `gh issue view <N>`; при `gh >= 2.94` — `--json blockedBy,parent,subIssues`.
4. Переведи issue в `in-progress` **полной заменой тела**: metadata `status: in-progress` + строка журнала.

**После работы:**

1. Прогони гейты: build / unit / integration / ручной тест в Revit.
2. Полностью замени тело: статус, что сделано, verification, коммиты, что не проверено.
3. Закрой issue: `gh issue close N`.
4. Обнови `docs/agent/ROADMAP.md` (статус задачи и эпика). Состояние сессии — в журнале issue, не в отдельном файле.

### Полная замена тела — единственный способ обновления

- Любое изменение issue или эпика: `gh issue edit N --body-file <file>`.
- **Запрещено** обновлять статус комментариями, дописывать «+1», прятать результат только в комментарий.
- Тело всегда самодостаточно: новый агент с нулевым контекстом читает только тело и понимает состояние.
- Комментарии — только для временной координации и вопросов; статус в них не живёт.
- При закрытии секция `Resolution` добавляется в тело (полная замена), а не комментарием.

### Обязательные шаблоны

**Задача** — метки `agent`, `task`, `P0..P3`:

```markdown
<!-- agent-meta
id: A.1
kind: task
epic: A
priority: P0
status: backlog
updated: YYYY-MM-DD
-->

## Контекст
## Что сделать
## Out of scope
## Критерии приёмки
- [ ] ...
## Тесты
## Указатели по коду
## Ссылки
## Журнал
- YYYY-MM-DD — ...
```

**Эпик** — метки `agent`, `epic`, `P0..P3`: metadata, Цель, Scope, Out of scope,
таблица задач с номерами issue, критерии завершения, метрики, зависимости, ссылки, журнал.

**Баг** — метки `agent`, `bug`, `P0..P3`: Симптом, Воспроизведение, Ожидаемое/Фактическое,
Root cause, Fix, Verification, Scope/Notes, Журнал.

### Метки и приоритеты

| Метка | Значение |
|---|---|
| `agent` | всё, что относится к AI-агенту |
| `epic` / `task` | уровень работы |
| `bug` / `enhancement` | тип |
| `P0`..`P3` | приоритет |
| `ready` / `in-progress` / `blocked` | состояние работы (дублирует metadata `status` для фильтра) |
| `needs-owner-acceptance` | требует ручной приёмки владельцем |

- **P0** — блокер: без него нельзя доверять массовым операциям или релизу.
- **P1** — следующий слой: реальные кейсы, надёжность, стоимость.
- **P2** — расширение после P0/P1.
- **P3** — по метрике или позже.

### Нумерация и ROADMAP

- Эпик: `A`, `B`, `C`, `P`, `R`, `S`, `G`, `Q`, `H2`… ID выдаётся один раз и не переиспользуется.
- Задача: `<EPIC>.<N>` (`A.1`, `A.2`).
- Вставка: `<EPIC>.<N>.<M>` (`A.1.1`) или суффикс (`A.1a`).
  **Никогда не перенумеровывай существующие ID.**
- Порядок задаётся строкой в `ROADMAP.md`, а не номером.
- Удалённая задача — `dropped`, поглощённая — `absorbed by <ID>`; ID остаётся занятым.
- Новая фича/баг без issue **не начинается**. Найденный по ходу дефект сразу заводится issue
  (или добавляется в существующий эпик), а не откладывается в `HANDOFF.md`.
- Если фича меняет состав работ — в том же изменении обнови `ROADMAP.md`.

### GitHub — только `gh` CLI, не MCP

- Все операции: `gh issue`, `gh api`, `gh pr`, `gh run`.
- **GitHub MCP не использовать**: в нём меньше операций, нет полного контроля над body/labels/sub-issues.
- Связь эпик → задача — native sub-issues:
  - `gh >= 2.94`: `gh issue create ... --parent N`, `gh issue edit N --add-sub-issue M`;
  - текущий `gh 2.83`: `POST /repos/{owner}/{repo}/issues/{parent}/sub_issues` с `sub_issue_id` (database id);
  - проверка: `gh api repos/{owner}/{repo}/issues/N/sub_issues` (gh 2.83) или `gh issue view N --json parent,subIssues,subIssuesSummary` (gh >= 2.94).
- Рекомендуется обновить `gh` до ≥2.94 (первоклассные флаги `--parent`, `--blocked-by`).

### Использование Issues как источник ссылок

- В коммитах: `Closes #N` / `Refs #N`.
- В коде: `// See #N for root cause and workaround rationale`.
- В ADR и документах — номер issue как официальная ссылка.
- Поиск: `gh issue list --label agent --state open`, `gh issue list --search "..."`.

**Когда issue не нужна:** тривиальная опечатка, форматирование, чисто внутренний рефакторинг
без изменения поведения, работа, уже отслеживаемая другим issue/PR.

### Workaround'ы и их документирование

**Единый реестр:** `docs/known-workarounds.md` — таблица всех активных и удалённых
workaround'ов с указанием Issue, файла, платформы и описания. **Загружай ВСЕГДА**
перед началом работы над багом — возможно workaround уже существует.

**Где искать известные workaround'ы агенту:**
1. `docs/known-workarounds.md` — единый реестр (Issue, файл, платформа, статус)
2. Кодовые комментарии вида `// See #95 for root cause and workaround rationale`
3. ADR (для архитектурных workaround'ов) — `docs/adr/README.md`
4. GitHub Issues с лейблом `bug` — полный root cause + Resolution

**Когда создавать Issue для workaround'а:**
- Каждый workaround, который «лечит» симптом (не root cause), должен быть
  задокументирован в отдельном Issue с секцией `Resolution` в теле.
- В Resolution укажи: что делает workaround, почему выбран этот подход,
  какие альтернативы отвергнуты, verification, scope.
- В коде добавь комментарий-ссылку: `// See #N for root cause and workaround rationale`
- Обнови `docs/known-workarounds.md` — добавь строку в таблицу активных workaround'ов.
- Если workaround удалён — перенеси его в секцию «Устаревшие workaround'ы»
  с указанием коммита удаления.

**Когда НЕ нужен ADR для workaround'а:**
- Temporary workaround (живёт пока баг upstream не фиксят) — **GitHub Issue достаточно**
- Архитектурное решение «как обойти системное ограничение навсегда» — **нужен ADR**
- Правило: если откат занимает <2 недель → Issue. Если >2 недель → ADR.
- Best practice (Spotify, Microsoft): temporary workarounds → **SKIP ADR**.
  Use GitHub Issue + `Resolution` в теле + code comment + `known-workarounds.md`.

## Точка входа в документацию

**Единый источник правды (SSOT)** находится в папке `docs/`. Начинай с:

0. **`docs/agent/ROADMAP.md`** — единая дорожная карта AI-агента: эпики, задачи, ID ↔ issue #, статусы. **Загружай ВСЕГДА при работе над агентом.**
1. **`docs/README.md`** — индекс всех документов, карта навигации
2. **`docs/invariants.md`** — жёсткие правила (I-01..I-17). **Загружай ВСЕГДА.**
3. **`docs/architecture/dependency-rule.md`** — куда класть код. **Загружай ВСЕГДА.**
4. **`docs/known-workarounds.md`** — реестр workaround'ов. **Загружай перед фиксами багов.**

Остальные документы — по контексту задачи (см. карту в `docs/README.md`).

## Code Style

- Комментарии: **НЕ добавлять** если не попросят
- Размер `.cs`-файлов: цель **≤500 строк**, жёсткий лимит **600** (#260). Больше —
  partial-разбивка `Class.Topic.cs` по зонам ответственности; поля с инициализаторами,
  primary constructor и базовые типы остаются в ядровом файле; `#if`-блоки — только целиком
- XAML code-behind: только `DataContext = viewModel` (I-10)
- MVVM строго: `.xaml.cs` не содержит логики
- Revit API из WPF → только через `IExternalEventHandler` (I-01)
- Транзакции → только через `ITransactionService`. Запрещено `new Transaction(doc)` (I-03)
- Не хранить `Element`/`Connector` между транзакциями. Только `ElementId` (I-05)
- `SmartCon.Core` НЕ вызывает Revit API — compile-time only. Запрет `using System.Windows` (I-09)
- Новые доменные классы → обнови `docs/domain/models/<module>.md` (для крупных модулей — тематический файл в `docs/domain/models/<module>/<topic>.md`, см. `docs/domain/models/README.md`)
- Новые интерфейсы → обнови `docs/domain/interfaces/<module>.md` (для крупных модулей — тематический файл в `docs/domain/interfaces/<module>/<topic>.md`, см. `docs/domain/interfaces/README.md`)
- Архитектурные решения → создай ADR в `docs/adr/`
- **Логирование** → **НИКОГДА** не пиши `$"[Cat] message"` — используй
  `using var _scope = SmartConLogger.BeginScope("Cat", ("Method", nameof(M)));`
  Подробно — skill `smartcon-logging` (SKILL.md → `references/logging-cookbook.md`)
  и `docs/adr/026-logging-migration.md`. Три правила без исключений:
  1. **`FilePath` / `FileName` в scope = `Path.GetFileName()`, не full path** (L8).
  2. **`Warn(...)` всегда заканчивается `[Action: ...]`** — оператору нужен следующий шаг (L9).
  3. **Не оборачивай `BeginScope` методы, которые живут > 1 сек с тяжёлой inner работой** — это даёт 2 МБ логов за один прогон (C15). Пусть inner work откроет свой scope.

## Обязательные навыки (загружай перед задачей)

| Навык | Когда загружать |
|---|---|
| `smartcon-build-guide` | **ВСЕГДА** перед сборкой / деплоем / релизом |
| `revit-api-best-practice` | Работа с Revit API, ExternalEvent, Threading, MVVM+Revit |
| `revit-wpf-compat` | WPF-диалоги, Dispatcher, `Application.Current`, net48/net8/net10 совместимость |
| `smartcon-logging` | **ВСЕГДА** при работе с логами (чтение, добавление вызовов, миграция prefix→scope, аудит `smartcon.log`) |
| `smartcon-testing` | **ВСЕГДА** при написании/обновлении тестов (unit + integration) и перед запуском интеграционного сьюта (9 жёстких правил в `references/integration-testing.md`) |
| `smartcon-db-actualization` | **ВСЕГДА** при добавлении миграции/актуализации старых `catalog.db` (critical/optional задача, `IDatabaseActualizationTask`, команда «Обновить базу», баннер/гейт/точка) |
| `smartcon-changelog` | **ВСЕГДА** при подготовке текста релиза / changelog (stable→анализ `main`, beta→анализ `develop`, архив в `docs/changelogs/`, передача в `release.ps1` через `-ChangelogFile`) |

## Частые ловушки (gotchas)

- **`dotnet restore` без `-p:Configuration`** → fallback на RevitAPI 2021.* → ложные ошибки CS0618; кроме того, пишет assets в `obj/`, а не в per-config `obj\<Configuration>\`. Собирай через `dotnet build -c Debug.RXX` (implicit restore в правильный путь)
- **Per-config NuGet assets:** у каждой конфигурации свой `obj\<Configuration>\project.assets.json` (`MSBuildProjectExtensionsPath` в `src/Directory.Build.props`). Смена `-c` больше не перезаписывает чужой граф: R25 после полного sweep R19 — no-op 1–2 с (замерено 2026-11-17). Общего `obj/project.assets.json` для Debug.R* больше нет
- **Debug.\* без анализаторов:** `EnableNETAnalyzers=false` + `EnforceCodeStyleInBuild=false` только для `Debug.*`; `Release.*` (CI, `release.ps1`) — полный набор. Генераторы исходников (`[ObservableProperty]`, PolySharp) остаются включены
- **Антивирус:** Kaspersky/Defender замедляет полные сборки в разы. Исключить репозиторий, `%USERPROFILE%\.nuget\packages`, `%LOCALAPPDATA%\NuGet\v3-cache`, `%TEMP%\NuGetScratch`, процессы `dotnet.exe` / `VBCSCompiler.exe`
- **Собирай `SmartCon.App.csproj`**, НЕ `SmartCon.sln` (подтянет лишние TFM)
- **BG1002 «не удается найти `*.baml`»** — гонка CLI-сборки с фоновыми design-time сборками IDE (VS Code C# / Roslyn LanguageServer) за общий `obj/` (механизм dotnet/wpf#4299, msbuild#6170; провоцируется доказательно — #281). НЕ ошибка конфигурации и НЕ бэнд SDK (A/B 10.0.201 vs 10.0.112 — ловится на любом). Лечение: повторить `dotnet build`; если залипло (LS продолжает собирать фоном) — закрыть/перезагрузить окно VS Code (или убить `Microsoft.CodeAnalysis.LanguageServer` + `dotnet build-server shutdown`), затем `git clean -xfd src` и пересобрать. **Полные пересборки / `build-and-deploy.bat` — при закрытом VS Code.** Профилактика: локальный `.vscode/settings.json` → `files.watcherExclude` на `**/obj/**`, `**/bin/**`.
- **При переходе net8 ↔ net48 ↔ net10** — ВСЕГДА `dotnet build` без `--no-restore` (implicit restore в per-config assets)
- **НЕ создавать PR** без явного запроса пользователя
- **NEVER commit** если не попросили — только `git add/commit/push` по запросу
- **`.GetAwaiter().GetResult()` на UI thread** → DEADLOCK. Используй `AsyncBridge.RunSync(() => async())` из `SmartCon.Core.Threading` (обёртка над `Task.Run + GetResult`). См. skill `revit-api-best-practice` + skill `smartcon-logging` §"Threading".
- **`async void`** — exception swallowing, используй `async Task` + try/catch. Для event handlers: `Func<T, Task>` (caller обязан await).
- **Stopwatch в production** — не используй `System.Diagnostics.Stopwatch` напрямую; используй `using var ms = SmartConLogger.Measure("Op")` + `ms.GetElapsedMilliseconds()`.
- **Логирование**:
  - `Op=X Op=X` дубль — не добавляй `("Op", X)` если operation уже `X` (D1 fix в `LogScope.FormatPrefix`)
  - `BeginScope` + `Measure` в одном методе → двойной `OpId=`. Выбери одно
  - `using var _` (discard) + `() => _ = …` lambda → CS0136 collision. Используй `_scope`
  - Hot loops (>100 iter) с `Debug($"...")` → используй `HotLoopCounter` (см. skill `smartcon-logging` §"Counter pattern")
  - **`FilePath` в scope = `Path.GetFileName()`, не full path** — full path в scope + повтор в message = 100+ chars spam в каждой строке. Убери path из message (L8).
  - **`Warn` без `[Action: ...]`** — оператор читает лог и не знает что делать. Каждый Warn должен заканчиваться конкретным actionable предложением (L9). Примеры: см. skill `smartcon-logging` → `references/recent-patterns.md` §"L9".
  - **`BeginScope` вокруг долгого метода (>1 сек с тяжёлой inner работой)** — 7803 строк лога из-за одного scope в PipeConnect Editor. Убирай внешний scope, оставь только inner (C15).
- **Format specifiers `{x:F0}`, `{date:format}` в interpolated strings ЗАПРЕЩЕНЫ** в файлах, транзитивно ссылающихся на HelixToolkit/SharpDX (FamilyManager, App/Diagnostics) на net48 — CS1739. Используй `.ToString("F0", CultureInfo.InvariantCulture)`. См. #97.
- **Revit 2027 API удаляет `ConnectorType.MasterSurface`** (deprecated в 2026) → в коде `#if REVIT2027_OR_GREATER` использует `MainSurface` (в API 2021 `MainSurface` ещё нет — поэтому именно 2027+, не раньше). Прецедент: при сборке R27 всегда проверять removed-API (revitapidocs /2027/news).
- **`SmartCon.Tests` НЕ собирать под R27** — проект пиннут на net8.0-windows, net10-сборка даёт NU1201/NU1202. В sln его R27-конфигурации замапены на R25 (маппинг honored только в VS). Юнит-тесты — всегда `-c Debug.R25`.
- **Интеграционные тесты (SmartCon.IntegrationTests)**:
  - `dotnet run` требует `--framework` явно (`net8.0-windows` / `net10.0-windows` / `net48`) — иначе «проект для нескольких платформ».
  - **RevitAPIUI-типы в тестах = краш всей сессии** (`FileLoadException` + нативный AVE у последующих тестов). DB-уровень only.
  - Поля с Revit-типами — только ленивые; `= null!`/`static readonly XYZ` ломают инжектор (#78).
  - Параллелизм отключён (`[assembly: NotInParallel]`) — не включать: один процесс Revit = AVE.
  - Подмножество: класс `-- --treenode-filter "/*/*/*ClassName*/*"`, метод `-- --treenode-filter "/*/*/*/*MethodName*"` (дерево = сборка/ns/класс/метод; имя класса во 2-м сегменте молча даёт «Запущено ноль тестов» — всегда проверяй счётчик `всего: N` > 0). Подробно — skill `smartcon-testing`.
  - **Итерации (жёстко):** правка → точечный прогон класса (секунды) → ОДИН полный R25 → ОДИН net48. R27/net10 не запускать (выключен). Полный сьют — gate, а не диагностика. **НИКОГДА не перезапускай прогон ради имён упавших** — имена/причины извлекай из того же запуска (консоль: `сбой TestName`, `--report-trx`, или TestResults/*-report.html). Новые скипы относительно baseline = сигнал регрессии наравне с падениями.
  - **TUnit на net10 (R27):** TUnit 1.44 дизамбигуит перегрузки `IsEqualTo`/`IsNotEqualTo` атрибутом `[OverloadResolutionPriority]`, который honoured только C# 13+ (thomhurst/TUnit#5765/#6282). С глобальным `LangVersion=12` на net10 — 91×CS0121. Фикс: `LangVersion=latest` ТОЛЬКО для net10.0-windows в `SmartCon.IntegrationTests.csproj` — **НЕ удалять этот пин**; production-код остаётся на C# 12.

## Инструменты поиска

### Иерархия (от простого к сложному)

```
1. MCP Revit API docs — быстрая проверка сигнатуры, свойств, методов Revit API
   ↓ (если нужен контекст, примеры, best practices)
2. Exa — поиск примеров кода, форумов, Jeremy Tammik, GitHub, StackOverflow
   ↓ (если нужна официальная документация библиотеки с примерами)
3. Context7 — актуальная документация библиотек и фреймворков (.NET, NuGet, и т.д.)
```

### GitHub — только `gh` CLI, не MCP

- Issues, epics, sub-issues, PR, checks — только через `gh`.
- **GitHub MCP не использовать**: меньше операций, нет полного контроля над body/labels/sub-issues.
- `gh` не заменяет Exa: Exa ищет в вебе, `gh` работает с GitHub.

### MCP Revit API docs (быстрый справочник)

**Когда использовать:**
- Проверить сигнатуру метода/конструктора
- Посмотреть список свойств/методов класса
- Уточнить Exceptions и Remarks
- Проверить версию API (2025/2026)

**Доступные tools:**
- `revit-api-docs_search-docs` — поиск классов/методов по ключевым словам
- `revit-api-docs_retrieve-docs` — полная документация по запросу
- `revit-api-docs_retrieve-doc` — документация по точному URL

**Пример workflow:**
```
1. search-docs "ElementTransformUtils.MoveElement" → находим метод
2. retrieve-doc по URL → получаем полную документацию с параметрами и Exceptions
3. Если нужны примеры использования → Exa: "Jeremy Tammik MoveElement example"
```

**Важно:** MCP даёт **справочную информацию** (сигнатуры, Remarks, Exceptions).  
**Exa даёт контекст** (примеры кода, best practices, известные проблемы).  
**НЕ заменяй Exa MCP-ом** — для написания кода нужны оба инструмента.

### Context7 (официальная документация библиотек)

**Когда использовать:**
- Официальная документация библиотеки или фреймворка
- Актуальные примеры кода из документации
- Сигнатуры, API reference, version-specific поведение
- Проверка использования конкретного метода/класса в официальных docs

**Когда НЕ использовать:**
- Revit API (используй Exa и MCP Revit API docs)
- Поиск по форумам, блогам, GitHub, StackOverflow
- Общие вопросы программирования
- «Что-то где-то видел» — это всегда Exa

**Доступные tools:**
- `context7_resolve-library-id` — найти Context7-compatible library ID по имени
- `context7_query-docs` — запросить документацию по library ID

**Workflow:**
```
1. resolve-library-id "CommunityToolkit.Mvvm" → /websites/learn_microsoft_en-us_dotnet_communitytoolkit_mvvm
2. query-docs /websites/learn_microsoft_en-us_dotnet_communitytoolkit_mvvm "ObservableProperty example" → примеры кода
```

**Важно:** Context7 — это **не Google**. Перед query-docs всегда делается `resolve-library-id`. Не пытайся искать через Context7 «всё подряд».

### Exa (глубокий поиск)

**Когда использовать:**
- Примеры кода с Revit API
- Jeremy Tammik blog (thebuildingcoder.com)
- StackOverflow с accepted answer
- GitHub open source plugins
- Autodesk Community forums
- Известные проблемы и краши
- Best practices и паттерны
- Версии NuGet-пакетов

| Нужно | Инструмент |
|---|---|
| Сигнатура + Remarks Revit API | MCP Revit API docs |
| Примеры кода, форумы, best practices, edge cases | Exa |
| Официальная документация библиотеки с примерами | Context7 |
| .NET/WPF/DI паттерны из официальных docs | Context7 → Exa |
| Версия NuGet-пакета | Exa |
| Любой веб-поиск | Exa |

**Context7 — НЕ поисковик. Для любого веб-поиска используй Exa.**

## Сборка и CI/CD

**Агент ОБЯЗАН загрузить навык `smartcon-build-guide` перед ЛЮБОЙ задачей связанной со сборкой, деплоем или релизом.**

```
skill: smartcon-build-guide
```

### Промежуточная сборка (для тестирования)
Build individual configurations — does NOT deploy. Fastest full check — all 6 in parallel:
```bash
powershell -NoProfile -ExecutionPolicy Bypass -File tools\build-all-configs.ps1
```
```bash
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R26
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R25
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R24
```

### Финальная сборка + деплой (только после ручного тестирования!)
**WARNING:** `build-and-deploy.bat` копирует файлы в Revit addins. Use ONLY after user confirms everything works:
```bash
build-and-deploy.bat
```

### Критические правила (агенты постоянно забывают):
1. **Собирай ВСЕ включённые версии:** R19, R21, R24, R25, R26 (а не только R25!). Быстрее всего — `tools\build-all-configs.ps1` (параллельно; конфигурации изолированы). R27 выключен флагом `SmartConEnableRevit2027` — `-c Debug.R27` теперь падает с явной ошибкой.
2. **Restore требует Configuration:** per-config assets лежат в `obj\<Configuration>\project.assets.json` (`MSBuildProjectExtensionsPath` в `src/Directory.Build.props`); смена `-c` не перезаписывает чужой граф
3. **При смене TFM (net8 ↔ net48 ↔ net10):** ВСЕГДА `dotnet build` без `--no-restore` (implicit restore в per-config assets)
4. **Собирай `SmartCon.App.csproj`, НЕ `SmartCon.sln`**
5. **Per-version бинарники:** каждая версия Revit получает СВОЮ сборку (R25→2025, R26→2026, R27→2027). Эпоха «one binary для 2025+2026» закончилась с #233: в API 2026 у `WireType.WireMaterial`/`TemperatureRating`/`Insulation` сменились типы на `ElementId`, `MaxSize` → `string` — старый бинарник на 2026 = `MissingMethodException`.
6. **SDK:** `global.json` — пол 10.0.100, rollForward latestPatch (ТОЛЬКО бэнд 10.0.1xx, stable-only — консервативная политика: у бэнда 10.0.2xx есть отдельные подтверждённые регрессии, dotnet/wpf#11678 / dotnet/sdk#53508; BG1002 к бэнду отношения НЕ имеет, см. ловушку ниже) — ВСЕ конфигурации, включая net48, собираются под SDK 10. SDK 8 больше не нужен для сборки, но .NET 8 RUNTIME нужен для запуска юнит-тестов (`SmartCon.Tests` = net8.0-windows).
7. **Debug.* vs Release.*:** Debug-сборки идут без NET/IDE-анализаторов (`EnableNETAnalyzers=false`, `EnforceCodeStyleInBuild=false`) — быстрый цикл; Release/CI (`release.ps1`, GitHub Actions) — полный набор. Генерируемый IL одинаков.

**Подробная документация в skill:**
- `references/build-configurations.md` — мульти-версионная сборка
- `references/ci-cd-workflow.md` — GitHub Actions, branch protection
