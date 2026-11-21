namespace SmartCon.Core.Services.Agent;

public static partial class AgentToolPackSkills
{
    private static IEnumerable<(string Pack, string Markdown)> JobsSkills() =>
        new[] { ("jobs", Jobs) };

    private const string Jobs = @"## Пак jobs: массовые операции

Одна джоба заменяет сотни поштучных вызовов: выбрать набор → превью → применить → отчёт.

### Карта тулов
- `start_job` — старт/превью/резюм. `action:` `rename_views|set_parameters|map_family_parameters|replace_family_parameter`.
- `get_job_status` — статус и прогресс: `waitSeconds` ≤25, `includeSample=false` для экономии контекста.
- `get_job_report` — отчёт: `includeItems=false` (итоги + ошибки), `format:""csv""` пишет файл, `readBack`.
- `rollback_job` — откат по сохранённым undo-данным; `dryRun` по умолчанию true.
- `retry_failed_items` — пере-оценка и повтор только неудачных единиц.
- `cancel_job` — кооперативная остановка между пачками (текущая докатывается), частичный отчёт.

**Не путать:** `map_family_parameters` и `replace_family_parameter` — и поштучные тулы (одно
семейство), и action'ы джобы (до 200 family units). Массовая миграция или замена общего
параметра (ADSK → параметр заказчика) по многим семействам = один `start_job`, не тул в цикле.

### Кухня Revit
- Транзакция дорога: 10 коммитов ≈7.3 с против 1.4 с у группы, 10×100 элементов — 202 с против 44 с (замеры сообщества). Джоба коммитит **пачками** (`batchSize`, 50), не по элементу.
- Worksharing: правка элемента, занятого другим пользователем, откатывает **всю** транзакцию (try/catch не спасает); ошибка валит пачку, а не один элемент.
- Отмена в Revit API кооперативная: прервать чужой вызов нельзя. `cancel_job` выставляет флаг, движок проверяет его между пачками и возвращает частичный отчёт.
- `RegenerationFailedException` при коммите = повреждение документа — фатально, продолжать нельзя.
- Revit однопоточен: джоба живёт в ExternalEvent-контексте; прогресс виден только через опрос статуса.

### Аргументы (start_job)
- `spec.select` — селектор: `viewIds/elementIds/nameContains/nameStartsWith/nameEndsWith/viewTypes/types/levelIds/folders/templateId/hasTemplate/familyIds/familyNames (стабильнее id)/categoryNames/typeIds/conditions[]/logic/limit`.
- `spec.rename` — `find/replace/prefix/suffix/setName` (`{n}`,`{name}`,`{index}`) и `numbering{start,step,padding,format}`; у видов `excludeSheets` по умолчанию true.
- `spec.mappings` — источник→цель, `mode: values|formula`, `typeNames` (≤100), `createTarget:true` — цель создать из ФОП (definition в файле не откатывается).
- `spec.replacements` — `replace_family_parameter`: `{source:{name|guid}, target:{name|guid}}` (≤50); цель — существующий definition из ФОП, scope/значения/формулы сохраняются.
- `spec.parameter/guid/builtInParameter/scope/value/unit` — запись параметра (`value` — строка, `""` очищает).
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
3. Заменить общий параметр ADSK на параметр заказчика по всем семействам серии: `start_job(action=""replace_family_parameter"", spec:{select:{nameContains:""ТДСК_""}, replacements:[{source:{name:""ADSK""}, target:{name:""Заказчик""}}]})` — превью (blastRadius по формулам) → apply.
4. Часть элементов недоступна (worksharing): `onError=""continue""`, затем `retry_failed_items`.

### Что нельзя
- Запускать apply без прочитанного превью и без `previewDigest`.
- Обещать «всё откатится» после джобы: откат — отдельный `rollback_job`, и он не воскрешает удалённое.
- Дробить джобу на поштучные write-вызовы: это в разы медленнее и теряет атомарность отчёта.
";
}
