# #209: Обновление общих вложенных семейств внутри family-документа — полный отчёт об исследовании

**Дата:** 2026-08-07 — 2026-08-11
**Связанные:** EPIC #207, E2 #209, ADR-066, ADR-067, future-issue #217
**Статус:** РЕШЕНО (2026-08-11, раунд 6): обновление вложенных работает на любой глубине через poke + doc-to-doc; верификация FHV8V без ParameterGroup; marker-first резолюция версии при импорте. Ручной тест владельца пройден (дрифт-блок снят, drift lookup 0/9). Разделы §4-§6 описывают историю — «стена» из раундов 1-5 ОТМЕНЕНА, см. §0.

> **Корректировка 2026-08-12 (вечер):** двухгрейдовая схема (FHV8V/FHV9)
> упразднена в тот же день — зонд `DrivenEmbeddedPollutionProbeTests`
> опроверг «host pollution» embedded-документа (метрики/phantom чисты), а
> владелец убрал группы параметров из identity. Текущее состояние — единый
> хэш **FHV10** на всё (группы не хэшируются нигде). Все упоминания FHV8V
> ниже читать как «FHV10»; рассуждения о «разных мирах raw vs embedded»
> (§172) — отвергнутая гипотеза, не факт. См. ADR-068 §A1.

Документ написан для агента с нулевым контекстом: здесь ВСЁ, что пробовали, что получилось, что нет, и почему.

---

## 0. ИТОГ (раунд 6, 2026-08-11): настоящий root cause и решение

### Root cause (доказан зондами + ручными тестами владельца на реальной библиотеке)

1. **Revit решает «семейство не изменилось» по dirty-флагу, а не по diff контента.** Истории хэшей у Revit нет: любая транзакция (даже net-zero «добавил параметр → удалил параметр», два commit, БЕЗ сохранения файла) переворачивает внутренний штамп. Если штамп не перевёрнут — `LoadFamily` молча no-op'ит, даже когда колбэки `IFamilyLoadOptions` вызываются и возвращается `true` (API систематически врёт об успехе — подтверждено Autodesk, источник №1).
2. **Merge (замена дефиниции) через API doc-to-doc работает, когда источник «грязный»** — новый тип приземлился в embedded-дефиниции гайки внутри пары (depth-2 hoisted, in use). Ранние попытки doc-to-doc были с НЕтронутым источником — ранний выход по неперевёрнутому штампу, поэтому казалось, что «всё возвращает success, но ничего не меняется».
3. **ParameterGroup не переносится НИКАКИМ merge** — ни API, ни UI, даже полной перезаписью с приземлением новых типов (доказано дважды: фланец через API, гайка через ручной UI-overwrite). Embedded-дефиниция навсегда хранит группу хоста; поменять её можно только delete+fresh load (запрещено владельцем — убивает привязки) или пересборкой родителя. Значит, строгая верификация «embedded == файл» с группой недостижима математически.
4. **Вторая половина бага (найдена ручным тестом 2026-08-11 вечер):** даже после успешного «Обновить» импорт-диалог резолвил версию embedded-вложенного по identity-хэшу FHV8 (с группой) → гайка матчилась на stored v1, фланец0104 (контент v2, группа v1) — вообще ни на что → связи `family_dependencies.child_version_label=v1` → SQL drift-гейт (`child_version_label <> current_version_label`) блокировал родителя вечно.

### Решение (три части)

1. **Poke + doc-to-doc — принудительный путь обновления** (`RevitFamilyLoadService.ReloadNestedInFamilyDocument`): фоновое `OpenDocumentFile(resolved)` → net-zero poke в памяти (`AddParameter("__SmartConPoke__")` commit → `RemoveParameter` + `Regenerate` commit, мультиверсионно `#if REVIT2022_OR_GREATER`; файл НИКОГДА не сохраняется, managed storage не трогаем) → `sourceDoc.LoadFamily(pairDoc, options)` ВНЕ транзакции (API-требование) → `Close(false)`. Fallback на legacy path-load, если poke невозможен. Гарды: наличие nested, Title-гард редактора, НОВЫЙ PathName-гард (переименованный открытый файл — иначе poke вольёт фантомные правки в живой документ пользователя). Post-verify остаётся арбитром — лживый маркер невозможен.
2. **FHV8V исключает ParameterGroup** (`FamilyContentHasher.BuildLoadableCanonicalString`, `verificationGrade=true`). Identity FHV8 — без изменений (группа остаётся → group-only diff по-прежнему создаёт версию в каталоге; свежая загрузка в проект несёт правильную группу). FHV8V нигде не персистится → миграций БД не нужно. Это НЕ послабление: группа в той же колонке исключений, что volumes/bounds, — физически непереносимые при reload поля.
3. **Marker-first резолюция версии при импорте** (`EmbeddedMarkerMatchResolver` + `FamilyImportPreparationService`): «Обновить» пишет ES-маркер на embedded-семейство ТОЛЬКО после успешной FHV8V-верификации → маркер строго сильнее хэш-матча. При подготовке nested-строки читаем маркер (`ReadFromLoadedFamily` на hoisted Family в документе родителя) и, если он указывает на тот же item что и name-match, берём его label: строка = Duplicate v2 → default Skip → связь пишется с `child_version_label=v2` → drift-гейт чист. Без маркера — прежний hash-путь.

### Побочный баг, найденный валидацией лога ручного теста

**Янтарная точка актуализации БД:** `AttributesActualizationTask` (`attributes-v1`) детектил pending по «нет строк `family_types`» — но с FHV8 phantom-тип всегда пропускается при извлечении, и phantom-only семейство легально извлекает НОЛЬ именованных типов. Детект поправлен: no-types = pending только когда успешный run ЗАЯВИЛ типы (`family_data_import_runs.types_count > 0`), а строк нет (семантика #152 сохранена). Также убран ложный Warn «POST-RELOAD VERIFICATION FAILED» из pre-verify (расхождение на pre-verify — нормальная ветка «нужен reload», Warn теперь только для настоящих post-reload падений).

### Верификация раунда 6

- 4 сборки (R25/R24/R21/R19) 0/0; юнит 2693/2693; интеграционные R25 149/149; net48 (Revit 2021) 134+14 skip, 0 fail.
- Контракты на реальной библиотеке (`NestedReloadPokeContractTests`): poke+doc-to-doc приземляет тип; production-обновление гайки (group-only) и фланца (реальный diff) — Success + verify сошёлся.
- Ручной тест владельца: «Проверить»→«Обновить» (6/6 verified/маркеры) → «Импорт активного файла» (marker override на всех 4 проблемных) → **drift lookup 0/9** (было 1/9) → блок снят. Лог без Error; единственный Warn эпохи — устранённый pre-verify false alarm.
- Юнит: `EmbeddedMarkerMatchResolverTests` (9), `AttributesActualizationTaskTests` (+2: phantom-run healthy / claimed-types pending). Интеграционный: ES-маркер roundtrip на nested Family в family-документе.

### Known limitation (зафиксировано валидатором)

- Rename nested-строки в batch-диалоге пересчитывает dedup без маркера (строка не несёт `EmbeddedMarker*`) — override молча сбрасывается до refresh. Не регрессия против pre-fix.
- Маркер может «устареть» при ручной правке embedded после «Обновить» — тот же trust-boundary, что у stale-detection в проектах.

---

## 1. Контекст и цель

EPIC #207 делает общие (shared) вложенные семейства полноценными элементами каталога FamilyManager: у родителя (напр. «Фланцевая пара») вложенные (фланцы, прокладка, болт/гайка/шайба) хранятся как отдельные catalog items со связями `family_dependencies` (kind='shared_nested').

E2 (#209) добавляет: collector вложенных, авто-импорт, связи, и **обновление устаревших вложенных внутри открытого в редакторе семейства** (команда «Проверить» → «Обновить» в FamilyManager, когда активный документ — .rfa).

Тестовый кейс владельца: «Фланцевая пара» содержит:
- **depth-1:** фланцы (2 шт) и прокладка — shared, прямые дети;
- **depth-2:** болт/гайка/шайба — shared, но вложены через **необщую** сборку `PPR-C0880-S-Болт_7798+Гайка_5915+Шайба_11371-PIEC-FC-0110-G3` (файла на диске нет — живёт только внутри .rfa пары).

Каталог: `d:\Project\dotNET\00_Архив\Библиотеки семейств\Тест\catalog.db` (управляемое хранилище `files\<itemId>\<label>\*.rfa`).

## 2. Что реализовано (работает, в проде)

- **FHV8** (`FamilyContentHashFormat.CurrentVersion = 8`): композитный хэш — секция `NESTEDHASH` в canonical string; изменение контента вложенного транзитивно сдвигает хэш всех предков (переимпорт родителя = новая версия). Прямые рёбра вычитанием из плоских subtree-сканов, композиция снизу вверх, маркер `UNREADABLE`. Только shared; non-shared — name-only (#217 «Компоненты»).
- **Phantom-тип всегда пропускается** при извлечении типов (Revit синтезирует безымянный дефолтный тип при LoadFamily; raw .rfa Types.Size=0, EditFamily-копия Size=1 — хэш зависел от контекста до фикса).
- **UC-2 («Импорт активного файла»)**: дети активного семейства извлекаются через override `_activeFamilyDoc`, импортируются отдельным batch, связи пишутся.
- **Обновление вложенных в family-документе** (`RevitFamilyLoadService.ReloadNestedInFamilyDocument`): прямой overwrite `Document.LoadFamily(path, IFamilyLoadOptions)` — работает для depth-1 (доказано в проде: фланец обновился, 56→55 параметров).
- **Tri-state верификация** (`StaleUpdateVerificationPolicy` + `VerifyEmbeddedMatchesResolvedFileAsync`): сравнение verification-хэша embedded (EditFamily из активного family-документа) с verification-хэшем resolved-файла — оба в одной live-сессии (контекстно-согласовано). `true`=совпало, `false`=не совпало (update падает, маркер НЕ пишется), `null`=неприменимо (проект-контекст, файл открыт в редакторе, семейство не вложено). Pre-verify до reload (идемпотентные ретраи), арбитраж failed-reload (LoadFamily false на unchanged).
- Гарды: семейство открыто в редакторе (по Title И OwnerFamily.Name), resolved-файл уже открыт (по PathName — иначе OpenDocumentFile вернёт живой документ и Close(false) убьёт правки пользователя).
- Лог резолвера включает relative_path (`v1/`, `v2/` различимы).

Гейт на момент коммита: 4 сборки (R25/R24/R21/R19) 0/0, юнит 2682/2682, интеграционные R25 144/144, net48 (Revit 2023) 135+8 skip.

## 3. Probe-факты о структуре вложенности (доказано тестами)

- **P1:** все shared nested видны ПЛОСКО (hoisted) в family-документе любого предка — `FilteredElementCollector.OfClass(Family)` в паре показывает и фланцы, и болт/гайку/шайбу.
- **P2:** `EditFamily` из family-документа возвращает независимую копию (PathName пуст), НО если вложенное открыто как top-level документ — возвращает ЖИВОЙ документ пользователя (гард обязателен, Close убьёт правки).
- **P3:** nested-doc переживает Close родителя. `OpenDocumentFile` уже открытого пути возвращает тот же документ (двойной Close → InvalidObjectException).

## 4. Хронология исследования reload (5 раундов)

### Раунд 1-2 (2026-08-07): «стена depth-N»
Пробовали обновить болт ВНУТРИ промежуточного семейства: LoadFamily в unsaved EditFamily-копию → false без колбэков; doc-to-doc → возвращает существующий Family без изменений; SaveAs-backed копия → то же. Вывод той ночи: «depth-2+ — стена, fast-fail с каскадной инструкцией». **Ошибочный вывод** — смотрели не туда.

### Раунд 3 (2026-08-10): hoisted-семантика
Доказано контрактными тестами (`NestedFamilyReloadTests`):
- Общее вложенное любого уровня существует в host-документе как **одна hoisted-дефиниция**; прямой overwrite reload в host обновляет её; **в проект уезжает именно hoisted-дефиниция** (проект получил обновлённого внука).
- Внутренняя копия внутри EditFamily-вью промежуточного остаётся старой при ЛЮБОМ варианте (прямая загрузка, temp-SaveAs копия, push-back) — косметика, из проектов недостижимая.
- Chain-discovery/fast-fail удалены из production как ненужные.

### Раунд 3 ручной тест (2026-08-11, 00:02): 4 семейства «не обновились»
- Батч 1: reload всех 6 «успешен» (OnFamilyFound fired, overwrite=True), но verify упал для 4 (болт/гайка/шайба/фланец0104): embedded-хэш == stored **v1**, цель — v2.
- Батч 2 (ретрай): все 4 «LoadFamily returned false» (= unchanged).
- Вывод той ночи (ОШИБОЧНЫЙ): «reload не работает для этих семейств».

### Раунд 4 (2026-08-11, 01:36): настоящая картина из логов
- **Фланец 0104: reload РАБОТАЛ** (56→55 параметров — контент v2). Падала верификация: embedded-хэш (host-driven контекст) сравнивался со stored-хэшем (raw-файл) — контексты несравнимы.
- **Болт/гайка/шайба: reload — настоящий no-op** (params и хэш идентичны до/после).
- **v1↔v2 болт/гайка/шайба отличаются ТОЛЬКО ParameterGroup** (`ADSK_Материал обозначение`: materials → identityData). У фланца: группа + формула size_lookup + удалённый параметр `Тип_Уплотнительной_Поверхности`.
- **Revit НЕ пропагирует ParameterGroup при overwrite-reload**: embedded после reload имеет формулы/параметры v2, но группы хоста (v1).

### Раунд 5 (2026-08-11): полный инвентарь стены + решение владельца «СТРОГО»
Все варианты для гайки (каждый возвращает «успех», контент остаётся v1):

| Способ | Результат |
|---|---|
| LoadFamily(path) в family-документ (depth-1, fresh host) | true, контент v1 |
| LoadFamily(path) в пару (depth-2 hoisted) | true, v1 |
| doc-to-doc `v2doc.LoadFamily(host)` | Family, v1 |
| Обёртка-контейнер + OnSharedFamilyFound(source=Family) | колбэк fired, v1 |
| LoadFamily(path) в проект | true, v1 |
| LoadFamilySymbol(path, type) в проекте (путь #101) | true, v1 |
| Файл v3 = v2 + новый тип (TypeProbe) | true, v1, тип не появился |
| **Открыть сборку EditFamily** | **InvalidOperationException «Loaded Family Editing failed»** (IsEditable=true лжёт; первая попытка и изолированная — одинаково) |

Lookup-таблицы проверены через `FamilySizeTableManager` — присутствуют и здоровы во всех копиях (v1, v2, embedded) — НЕ причина.

Синтетические семейства (именованные типы, phantom-тип, добавление параметра/типа) — все варианты reload работают. Фланец (Pipe Fittings) — reload работает. Гайка (Pipe Accessories) — нигде. Файлоспецифично, не структурно.

## 5. Источники (авторитетность по убыванию)

1. **Autodesk Revit API forum, 2026-04-30 (Revit 2026.4)** — официально подтверждённое расхождение: «LoadFamily API method does not replicate manual family reload behavior... The API method proves to be unreliable for reloading families in a project, as it only upgrades the parent family. We kindly request that the LoadFamily API method be updated to behave identically to the interface-based loading method.» — https://forums.autodesk.com/t5/revit-api-forum/loadfamily-api-method-does-not-replicate-manual-family-reload/td-p/14111950
2. **Jeremy Tammik, The Building Coder #660** — reload работает только если Revit считает семейство «modified»; иначе молчаливый no-op, колбэки могут не вызываться: https://jeremytammik.github.io/tbc/a/0660_reload_family.htm
3. **Jeremy Tammik #597** — паттерн reload через перегрузку с IFamilyLoadOptions: https://jeremytammik.github.io/tbc/a/0597_reload_family.htm
4. **Autodesk forum «Shared Nested Family»** — shared nested не обновляются при перезагрузке хоста; workaround «удалить и загрузить заново»: https://forums.autodesk.com/t5/revit-api-forum/shared-nested-family/td-p/9455681
5. **RevitForum (Steve Stafford)** — «The only nested families that update in a host family when loaded into a project individually are shared families»: https://www.revitforum.org/forum/revit-architecture-forum-rac/architecture-and-general-revit-questions/17580-revit-nested-families-not-updating
6. **Autodesk help** — правила загрузки семейств с shared nested (каждый shared nested доступен в Project Browser; при конфликте — выбор версии): https://help.autodesk.com/cloudhelp/2016/ENU/Revit-Model/files/GUID-8C3FF1C9-8002-4686-9C12-725FF2C0DF46.htm
7. **Autodesk blog «Pet Change»** — swap nested families только через SaveAs в реальные файлы (Dynamo): https://blog.autodesk.io/pet-change-python-and-dynamo-swap-nested-families/
8. **revitapidocs LoadFamily(Document)** — reload обратно в исходный документ через эту перегрузку всегда падает (подавляет промпты); нужен overload с IFamilyLoadOptions: https://www.revitapidocs.com/2025.3/6a91dc8e-6c2b-52b9-dfc4-d56fa472852b.htm

## 6. Итоговая стена — ОТМЕНЕНА раундом 6 (см. §0; оставлено как история)

> Раунд 6 доказал: «стена» была не в merge-механизме, а в changedness dirty-флаге + группе в verification-хэше. Рабочие механизмы — в §0. Ниже — состояние знаний на конец раунда 5.

1. **Depth-2+ для «проблемных» семейств:** ни один из 8 API-путей не заменяет дефиницию гайки/болта/шайбы (все возвращают success). UI — по наблюдению владельца — обновляет (расхождение подтверждено Autodesk, источник №1).
2. **Открыть промежуточную сборку нельзя:** EditFamily падает («Loaded Family Editing failed»), файла сборки на диске не существует → рецепт «обновить внутри сборки и протолкнуть сборку» невозможен через API для этого семейства.
3. **ParameterGroup не пропагируется** при любом overwrite reload → strict-верификация (группа = контент) честно фейлит обновление семейств, чей version-diff включает перегруппировку, даже когда остальной контент приземлился.
4. **Удаление + fresh load ЗАПРЕЩЕНО владельцем** (уничтожает геометрические привязки) — и не проверялось.

## 7. Production-семантика раунда 5 — ЗАМЕЩЕНА раундом 6 (см. §0; оставлено как история)

> Раунд 6: группа исключена из FHV8V, reload = poke + doc-to-doc, импорт резолвит версию по маркеру. Ниже — состояние на конец раунда 5.

**Verification-grade хэш** (`IFamilyContentHasher.ComputeForEmbeddedVerification`, canonical `FHV8V`):
- **Включено (строго):** определения параметров (имя, storage, **группа**, instance/type, shared, формула, reporting, guid, builtin), типы+значения, топология геометрии (FormKind/IsSolid/счётчики/faces/edges/subcategory), GEOM2D счётчики, NESTED/NONSHARED имена, FACTS, FLAGS, коннекторы (Domain/Shape/Classification/IsPrimary/LinkedIndex).
- **Исключено (физически хост-зависимо, не контент):** volumes, bounds, surface areas, длины кривых, размеры/координаты коннекторов — хост пушит instance-параметры во вложенное, геометрия регенерируется на host-driven размерах. Без этого исключения сравнение embedded-vs-файл невозможно в принципе (даже полностью успешный reload даст mismatch).
- Сортировка форм в verification-grade — только по эмитированным полям (не по Volume — иначе host-driven swap порядка даёт ложный FAIL, validator H1).
- Никогда не хранится в каталоге (SourceKind="loadable-verify"); identity/dedup/versioning — полный FHV8 как раньше.

**Поток обновления (family-документ):**
1. Pre-verify: embedded verification-хэш == файла → skip reload, маркер (идемпотентные ретраи).
2. Reload (overwrite). LoadFamily false (unchanged) → арбитраж verify (== true → успех).
3. Post-verify: != → честный fail, Warn с инструкцией ручного UI-обновления; маркер НЕ пишется, семейство остаётся stale.
4. Проект-контекст: verify неприменим (null) → reload всегда выполняется, отказ — честный failure (preserve-types путь #101 — проверенный).

**Ожидаемое поведение на библиотеке владельца:** прокладка/фланец0352 — успех (v1 == v1); фланец0104 — reload приземляется, но verify фейлит (группа v1 осталась); болт/гайка/шайба — fail (no-op). Блок импорта («вложенные устарели») консистентен: identity-хэш видит v1 ≠ v2.

## 8. Открытый вопрос (решается 2-минутной проверкой владельца в UI)

Перезагрузить гайку v2 в паре вручную (Вставка → Загрузить семейство, перезапись) и проверить **группу параметра** `ADSK_Материал обозначение` у вложенной гайки:

- **Группа стала v2 (identityData)** → UI переносит группы → ручной путь — полный remediation: честный API-fail + инструкция (уже реализовано), pre-verify подхватит результат и запишет маркер.
- **Группа осталась v1 (materials)** → группу не переносит даже UI → вложенные в паре НИКОГДА не достигнут v2-identity без пересборки пары → тогда продуктовое решение: группа — метаданные, а не обновляемый контент, и проверку «outdated nested» (и drift) надо считать без ParameterGroup. **Это единственная точка, где «строгость» математически недостижима — не по нашей вине.**

## 9. Ключевые файлы

- `src/SmartCon.Core/Services/Implementation/FamilyContentHasher.cs` — FHV8 canonical + `ComputeForEmbeddedVerification` (FHV8V).
- `src/SmartCon.Core/Services/Implementation/CompositeFamilyHashComposer.cs` — FHV8 композиция (прямые рёбра вычитанием subtree).
- `src/SmartCon.FamilyManager/Services/Stale/StaleFamilyUpdater.cs` — поток обновления: pre-verify → reload → арбитраж/verify → маркер (`WriteMarkerBestEffortAsync`).
- `src/SmartCon.FamilyManager/Services/Stale/StaleUpdateVerificationPolicy.cs` — tri-state политика (pure, юнит-покрыта).
- `src/SmartCon.Revit/FamilyManager/RevitFamilyLoadService.cs` — `ReloadNestedInFamilyDocument` (прямой overwrite в host, любой depth).
- Контрактные тесты: `NestedFamilyReloadTests` (hoisted-семантика), `RealLibraryNestedReloadReproTests` (стена на реальных файлах, в т.ч. strict-verify контракт), `CompositeFamilyHashTests` (транзитивность).
- Юнит: `FamilyContentHasherTests` (verification-grade семантика), `StaleUpdateVerificationPolicyTests` (tri-state), `CompositeFamilyHashComposerTests`.

## 10. Уроки для будущих агентов

1. **«LoadFamily вернул true» ≠ «дефиниция заменена».** Единственный арбитр — контент после операции (хэш/структура). Revit врёт об успехе систематически (источник №1).
2. **Хэши сравнивать только в одном контексте.** raw-файл vs embedded-в-хосте — разные миры (host-driven instance-параметры регенерируют геометрию; группы параметров хост не отдаёт).
3. **Phantom-тип** (семейство без именованных типов) синтезируется при LoadFamily — всегда skip в извлечении, иначе хэш зависит от контекста.
4. **EditFamily лжёт дважды:** возвращает живой документ если семейство открыто (гард по Title+OwnerFamily.Name), и падает «Loaded Family Editing failed» несмотря на IsEditable=true.
5. **OpenDocumentFile открытого пути** возвращает тот же документ — Close(false) в finally убьёт чужие правки (гард по PathName).
6. **Не выводить «стену» из одного слоя.** Раунд 2 объявил depth-N стеной, глядя на промежуточную копию; раунд 3 доказал, что важна только hoisted-дефиниция; раунд 4 показал, что reload фланца работал всю дорогу. Каждый вывод — только контрактным тестом на реальных файлах.
