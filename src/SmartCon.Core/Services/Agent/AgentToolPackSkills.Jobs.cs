namespace SmartCon.Core.Services.Agent;

public static partial class AgentToolPackSkills
{
    private static IEnumerable<(string Pack, string Markdown)> JobsSkills() =>
        new[] { ("jobs", Jobs) };

    private const string Jobs = @"## Пак jobs: массовые операции

Одна джоба заменяет сотни поштучных вызовов: выбрать набор → превью → применить → отчёт.

### Карта тулов
- `start_job` — старт/превью/резюм. `action:` `rename_views|set_parameters|map_family_parameters`.
- `get_job_status` — статус и прогресс: `waitSeconds` ≤25, `includeSample=false` для экономии контекста.
- `get_job_report` — отчёт: `includeItems=false` (итоги + ошибки), `format:""csv""` пишет файл, `readBack`.
- `rollback_job` — откат по сохранённым undo-данным; `dryRun` по умолчанию true.
- `retry_failed_items` — пере-оценка и повтор только неудачных единиц.
- `cancel_job` — кооперативная остановка между пачками (текущая пачка докатывается), частичный отчёт.

**Не путать:** `map_family_parameters` — это и отдельный тул (одно семейство), и action джобы
(до 200 family units). Для массовой миграции по десяткам/сотням семейств вызывайте
`start_job(action:""map_family_parameters"")`, а не тул в цикле.

### Кухня Revit
- Транзакция в Revit дорога: по замерам сообщества 10 отдельных коммитов ≈7.3 с против 1.4 с у группы; 10×100 элементов — 202 с против 44 с. Поэтому джоба коммитит **пачками** (`batchSize`, по умолчанию 50), а не по элементу.
- Worksharing: правка элемента, занятого другим пользователем, откатывает **всю** транзакцию; try/catch вокруг `Parameter.Set` не спасает. Ошибка такого рода валит текущую пачку, а не только один элемент.
- Отмена в Revit API кооперативная: жёстко прервать чужой вызов нельзя. `cancel_job` выставляет флаг, движок проверяет его между пачками и возвращает частичный отчёт; текущая пачка докатывается целиком.
- `RegenerationFailedException` при коммите означает повреждение документа — это фатально, продолжать джобу нельзя.
- Revit однопоточен: джоба живёт в ExternalEvent-контексте и не может «крутиться в фоне»; прогресс виден только через опрос статуса.

### Аргументы (start_job)
- `spec.select` — селектор: `viewIds/elementIds/nameContains/nameStartsWith/nameEndsWith/viewTypes/types/levelIds/folders/templateId/hasTemplate/familyIds/categoryNames/typeIds/conditions[]/logic/limit`.
- `spec.rename` — `find/replace/prefix/suffix/setName` (`{n}`, `{name}`, `{index}`) и `numbering{start,step,padding,format}`; для переименования видов `excludeSheets` по умолчанию true.
- `spec.mappings` — источник→цель, `mode: values|formula`, `typeNames` (не более 100).
- `spec.parameter/guid/builtInParameter/scope/value/unit` — запись параметра (`value` — строка, `""` очищает).
- `batchSize` 1..500 (50), `onError: continue|abort`, `rollbackOnError`, `maxItems` ≤10000 (1000), `allowLargeJobs`, `savePolicy: after_each_batch|every_n_minutes|never` (**по умолчанию never**: джоба не перезаписывает .rvt на диске — изменения живут в документе, файл сохраняет пользователь; не включайте автосейв без явной просьбы).
- Потолок единиц: `rename_views` и `set_parameters` — 5000, `map_family_parameters` — 200
  family units **жёстко**: `allowLargeJobs` его не поднимает, большие миграции дробятся на
  несколько джоб.

### Инварианты
- `dryRun=true` (по умолчанию) не меняет модель и возвращает `count`, `sample`, `warnings`, `previewDigest`. Применение — повторный вызов с `dryRun=false` и **тем же** `previewDigest`; устаревший (`job_preview_stale`), просроченный (`job_preview_expired`, TTL 30 мин) или чужой digest отклоняется.
- Новая превью вытесняет предыдущую (`job_preview_superseded`), но не во время выполнения: в документе одновременно одна джоба.
- Джоб больше 200 единиц требует `allowLargeJobs=true` в apply/resume (это порог
  подтверждения, а не поднятие per-action потолка); превью этого разрешения не даёт.
  Для `map_family_parameters` потолок 200 всё равно остаётся.
- Опрос — `get_job_status(waitSeconds=...)`, не повторный `start_job`. Без `jobId` статус показывает **активную** джобу (или `state:""idle""` + последние 5).
- `get_job_report(readBack=true)` — не только отчёт: найденные расхождения помечаются в задании как `Mismatch`. Это нормально, но учтите при последующем `retry_failed_items` (он берёт только `Failed`, не `Mismatch`).
- `retry_failed_items` не принимает `allowLargeJobs`: если неудачных больше 200, ретрай может упасть с `job_confirmation_required` — запустите новую превью.
- `undo_last` джобу НЕ откатывает — только `rollback_job(jobId)`.
- Прерванную (Revit закрылся) джобу продолжают `start_job` с `resumeJobId`; уже применённые единицы пере-проверяются, дублей нет.

### Рецепты
1. Переименовать 800 видов: `start_job(action=""rename_views"", spec:{select:{viewTypes:[""FloorPlan""]}, rename:{find:""Копия"", replace:""""}})`, прочитать превью → apply с digest.
2. Проставить параметр 3000 элементам: селектор `conditions` + `spec.parameter/value/unit` → превью → apply → `get_job_report(includeItems=false)`.
3. Часть элементов недоступна (worksharing): `onError=""continue""`, затем `retry_failed_items` для повторной попытки.

### Что нельзя
- Запускать apply без прочитанного превью и без `previewDigest`.
- Обещать «всё откатится» после джобы: откат — отдельный `rollback_job`, и он не воскрешает удалённое.
- Дробить джобу на поштучные write-вызовы: это в разы медленнее и теряет атомарность отчёта.
";
}