namespace SmartCon.Core.Services.Agent;

public static partial class AgentToolPackSkills
{
    private static IEnumerable<(string Pack, string Markdown)> JobsSkills() =>
        new[] { ("jobs", Jobs) };

    private const string Jobs = @"## Пак jobs: массовые операции

Одна джоба заменяет сотни поштучных вызовов: выбрать набор → превью → применить → отчёт.

### Карта тулов
- `start_job` — старт/превью/резюм. `action:` `rename_views|set_parameters|map_family_parameters|replace_family_parameter|add_family_parameter|set_family_parameter_scope`.
- `get_job_status` — статус и прогресс: `waitSeconds` ≤25, `includeSample=false` для экономии контекста.
- `get_job_report` — отчёт: `includeItems=false` (итоги + ошибки), `format:""csv""` пишет файл, `readBack`.
- `rollback_job` — откат по сохранённым undo-данным; `dryRun` по умолчанию true.
- `retry_failed_items` — пере-оценка и повтор только неудачных единиц.
- `cancel_job` — кооперативная остановка между пачками (текущая докатывается), частичный отчёт.

**Не путать:** `map_family_parameters` и `replace_family_parameter` — и поштучные тулы (одно
семейство), и action'ы джобы (до 200 family units). Массовая миграция или замена общего
параметра (старый общий параметр → параметр заказчика) по многим семействам = один `start_job`, не тул в цикле.
`add_family_parameter` — тоже тул и action: массовое ДОБАВЛЕНИЕ параметров (дубликаты ФОП,
`parameters[].formula` — авторская формула сразу после создания) = джоба, не сотни одиночных вызовов.
`set_family_parameter_scope` — только action: смена тип↔экземпляр параметра семейства
(MakeType/MakeInstance, значения и формулы сохраняются); параметр задаётся ключом
`parameterRef:{name|guid}` (объект — НЕ parameter, тот строковый и занят set_parameters);
отказ при формульной зависимости — подсказка уйдёт на replace_family_parameter.
Проектный уровень — тул `rebind_project_parameter` (значения сбрасываются, тул предупреждает).

### Кухня Revit
- Транзакция дорога: 10 коммитов ≈7.3 с против 1.4 с у группы, 10×100 элементов — 202 с против 44 с (замеры сообщества). Джоба коммитит **пачками** (`batchSize`, 50), не по элементу.
- Worksharing: движок берёт ownership пачки до транзакции; юнит, чей элемент занят другим пользователем или устарел, помечается `skipped` с причиной, остальные юниты пачки пишутся. Пропущенные повторить после relinquish/Reload Latest (`retry_failed_items` их не берёт — они не failed).
- Отмена в Revit API кооперативная: прервать чужой вызов нельзя. `cancel_job` выставляет флаг, движок проверяет его между пачками и возвращает частичный отчёт.
- `RegenerationFailedException` при коммите = повреждение документа — фатально, продолжать нельзя.
- Revit однопоточен: джоба живёт в ExternalEvent-контексте; прогресс виден только через опрос статуса.

### Аргументы (start_job)
- `spec.select` — селектор, состав ЗАВИСИТ от action: `rename_views` — `viewIds/viewTypes/types/levelIds/folders/template*` и др.; `set_parameters` — `elementIds/categoryIds/categoryNames/categories/levelIds/typeIds/familyName/familyNames/typeName/nameContains/conditions[]/logic/systemName/includeSubComponents/limit`; семейные — `familyIds/familyNames (стабильнее id)/typeNames/categoryIds/categoryNames (алиас categories)/placedOnly`. Точный список допустимых ключей для твоего action вернёт отказ `job_spec_invalid`.
- Объём «все семейства» = СНАЧАЛА размещённые: семейные джобы принимают `select.placedOnly:true` (только семейства с экземплярами в модели); неразмещённая библиотека — отдельным вторым этапом после явного решения пользователя. При двусмысленности объёма (AutoApprove) зафиксируй трактовку в отчёте, не расширяй молча.
- `spec.rename` — `find/replace/prefix/suffix/setName` (`{n}`,`{name}`,`{index}`) и `numbering{start,step,padding,format}`; у видов `excludeSheets` по умолчанию true.
- `spec.mappings` — источник→цель, `mode: values|formula|formula_repointed`, `typeNames` (≤100), `createTarget:true` — цель создать из ФОП (definition в файле не откатывается), `skipIfSourceMissing:true` — у семейства нет источника = skip маппинга (один список для разных сигнатур). `formula_repointed` переписывает формулу источника на ИМЕНА ЦЕЛЕЙ всех маппингов (дубликат живёт своей логикой, не цепью к старым параметрам); литералы в кавычках не трогаются; при несовпадении типов источника и цели — `formulaUnit` (×1/÷1-трюк Autodesk: Length→Number = «(F) / 1 mm» — число миллиметров; Number→Length = «(F) * 1 mm»; голая 1 заставляет Revit угадывать единицы — указывайте явно).
- Ожидание: после apply `get_job_status(waitSeconds:2700)` — ОДИН вызов держится до 45 минут, пока задание не завершится (петля мгновенных опросов → rate limit провайдера → смерть сессии). Семейная правка одного тяжёлого семейства может идти десятки минут: `updatedAt` застыл + state Running = Revit занят вычислением, длинный wait сам вернётся по завершении.
- `spec.replacements` — `replace_family_parameter`: `{source:{name|guid}, target:{name|guid}}` (≤50); цель — существующий definition из ФОП, scope/значения/формулы сохраняются.
- `spec.parameter/guid/builtInParameter/scope/value/unit` — запись параметра (`value` — строка, `""` очищает).
- `set_parameters` + `select.systemName` (точное имя из list_mep_systems, опционально `systemDomain`) — члены MEP-системы: Elements + BaseEquipment + Piping/DuctNetwork; `includeSubComponents:true` добирает вложенные shared-семейства (фланцы, глубина ≤3) — паттерн «общий параметр имени системы», чтобы фильтр вида показал и субкомпоненты.
- `batchSize` 1..500 (50), `onError: continue|abort`, `rollbackOnError`, `maxItems` ≤10000 (1000), `allowLargeJobs`, `savePolicy: after_each_batch|every_n_minutes|never` (**по умолчанию never**: джоба не перезаписывает .rvt — файл сохраняет пользователь; не включайте автосейв без явной просьбы).
- Потолок единиц: `rename_views`/`set_parameters` — 5000, family-action'ы (`map`/`replace_family_parameter`) — 200 **жёстко**: `allowLargeJobs` не поднимает, большие миграции дробятся на несколько джоб.

### Инварианты
- `dryRun=true` (по умолчанию) не меняет модель и возвращает `count`, `sample`, `warnings`, `previewDigest`. Применение — повторный вызов с `dryRun=false` и **тем же** `previewDigest`; устаревший (`job_preview_stale`), просроченный (`job_preview_expired`, TTL 30 мин) или чужой digest отклоняется.
- Новая превью вытесняет предыдущую (`job_preview_superseded`), но не во время выполнения: в документе одновременно одна джоба.
- Джоб больше 200 единиц требует `allowLargeJobs=true` в apply/resume (порог подтверждения, не поднятие per-action потолка); для family-action'ов потолок 200 всё равно остаётся.
- Опрос — `get_job_status(waitSeconds=...)`, не повторный `start_job`. Без `jobId` статус показывает **активную** джобу (или `state:""idle""` + последние 5).
- `get_job_report(readBack=true)` — найденные расхождения помечаются как `Mismatch`; `retry_failed_items` берёт только `Failed`, не `Mismatch`.
- `retry_failed_items` не принимает `allowLargeJobs`: неудачных больше 200 → `job_confirmation_required` — запустите новую превью.
- `undo_last` джобу НЕ откатывает — только `rollback_job(jobId)`.
- Прерванную (Revit закрылся) джобу продолжают `start_job` с `resumeJobId`; применённые единицы пере-проверяются, дублей нет.

### Рецепты
1. Переименовать 800 видов: `start_job(action=""rename_views"", spec:{select:{viewTypes:[""FloorPlan""]}, rename:{find:""Копия"", replace:""""}})`, прочитать превью → apply с digest.
2. Проставить параметр 3000 элементам: селектор `conditions` + `spec.parameter/value/unit` → превью → apply → `get_job_report(includeItems=false)`.
3. Заменить общий параметр на параметр заказчика по всем семействам серии: `start_job(action=""replace_family_parameter"", spec:{select:{nameContains:""<часть старого имени>""}, replacements:[{source:{name:""<старый параметр>""}, target:{name:""<новый параметр>""}}]})` — превью (blastRadius по формулам) → apply. Имена — из задачи, не из примера.
4. Часть элементов недоступна (worksharing): юниты уже в `skipped` с причиной; после relinquish/Reload Latest запусти ту же джобу в preview, чтобы пропущенные снова вошли в apply.
5. Юнит упал по другой причине: `onError=""continue""` + `retry_failed_items`.

### Что нельзя
- Запускать apply без прочитанного превью и без `previewDigest`.
- Обещать «всё откатится» после джобы: откат — отдельный `rollback_job`, и он не воскрешает удалённое.
- Дробить джобу на поштучные write-вызовы: это в разы медленнее и теряет атомарность отчёта.
";
}
