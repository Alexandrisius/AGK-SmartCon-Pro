namespace SmartCon.Core.Services.Agent;

public static partial class AgentToolPackSkills
{
    private static IEnumerable<(string Pack, string Markdown)> RelationsSkills() =>
        new[] { ("relations", Relations) };

    private const string Relations = @"## Пак relations: связи, цепи и помещения

### Карта тулов
- `get_element_relations` — все связи до 10 элементов разом: соединения по коннекторам
  (originMm, radiusMm, isFree, соседи с elementId/категорией/семейством/типом/маркой),
  хост (`host` — окно → стена), fromRoom/toRoom дверей и окон раздельно, levelName,
  systemName. Флаги includeConnectors/includeHost/includeRooms режут ответ.
- `trace_system_chain` — обход физической цепи от elementId: chain с bfsLevel и именами
  систем, breaks (пары несоединённых коннекторов разных элементов с gapMm ≤ 300 мм и
  именами систем обеих сторон), systemNameChanges. maxElements=500, gapThresholdMm=300.
- `get_room_contents` — состав помещения по roomId/roomName/roomNumber: карточка комнаты
  (areaM2) + группы семейство|тип с count/sample/elementIds, сортировка по count.

### Кухня Revit
- Соседи коннектора: ConnectorManager → AllRefs → Owner. Внутри AllRefs есть и логические
  ссылки на MEPSystem — они пропускаются; Curve-коннекторы пропускаются (I-08); порядок
  коннекторов отсортирован по id — ответ детерминирован.
- `get_element_relations` — точечный вопрос «что с чем соединено»: один вызов даёт и
  коннекторы, и хост, и помещения. Для «в какой стене окно» это host; для «куда ведёт
  дверь» — fromRoom/toRoom (могут быть null, если дверь наружу или фазы не сходятся).
- `trace_system_chain` идёт BFS только по СОЕДИНЁННЫМ коннекторам: недосынект не
  пересекается обходом, а ловится пост-анализом свободных коннекторов (breaks). Разрыв =
  пара свободных коннекторов разных элементов с зазором origin→origin до gapThresholdMm;
  шум стыков (~1e-6 ft) отсечён нижней границей 0.001 мм. Зазор реального недосынекта —
  единицы миллиметров (gapMm 1..50), поэтому [1;50] — надёжное окно проверки.
- Имя системы читается с элемента (RBS_SYSTEM_NAME_PARAM), не из типа системы: у труб
  одной цепи после соединения оно общее; смена имени вдоль chain (systemNameChanges)
  или across разрыв (fromSystemName/toSystemName в breaks) — это и есть граница «Т11/Т111».
- `get_room_contents`: room-bound элементы — через instance.Room; двери/окна — ещё и через
  FromRoom/ToRoom (дверь на границе попадает в состав обеих комнат — это честно);
  не-room-bound (трубы и пр.) — по центру bbox внутри габарита комнаты; model-группы
  раскрываются только при includeModelGroups=true.

### Рецепты
1. «С чем соединён кран К1»: найди элемент (query_elements по марке) →
   `get_element_relations` → connectors[].neighbors — вход и выход.
2. «Разрыв системы Т11/Т111»: `trace_system_chain` от насоса (или любой трубы ветки) →
   chain доходит до конца, а в breaks лежит пара с gapMm в единицах мм и разными
   fromSystemName/toSystemName → покажи пользователю элементы разрыва (fromElementId,
   toElementId) — «вот где цепь рвётся, поэтому имя системы не переходит».
3. «Что в помещении Офис 1»: `get_room_contents` (roomName или roomNumber) → contents[]
   по семействам с count; полные id — в elementIds/sample.
4. «Окно О1/дверь Д1/насос Н1»: `get_element_relations` закрывает все три — host стены,
   from/to помещения, levelName насоса; trace_system_chain не нужен для точечных вопросов.

### Что нельзя
- Чинить разрыв (соединять коннекторы) и менять состав систем — это write-операции,
  не этот пак; read-туры ничего не меняют в модели.
- Ждать fromRoom/toRoom у произвольного семейства: они только у дверей и окон (null
  в остальных случаях — это не ошибка).
- Искать состав помещения перебором query_elements по всей модели: `get_room_contents`
  делает это одним вызовом с группировкой.
";
}
