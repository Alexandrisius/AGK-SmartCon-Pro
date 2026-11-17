# Handoff — 2026-11-18 (Phase 0 + P1 read-layer checkpoint)

## Состояние

- **Панель работает в Revit 2025 и Revit 2023** (LLM отвечает, инструменты вызываются).
- UI: современный чат — пузыри с аватарами, Enter/Shift+Enter, шестерёнка настроек (провайдер/модель/ключ/прокси), миниатюры скриншотов + полноэкранный просмотр, три прыгающие точки, Markdown через **Markdig** (FlowDocument, без WebView), компактные tool-карточки с шевроном и раскрытием сырого результата.
- Реализованы **12 read-инструментов**: `get_project_overview`, `list_views`, `list_levels`, `list_categories`, `get_selected_elements`, `query_elements`, `get_element_details`, `summarize_elements`, `list_view_templates`, `get_view_settings`, `get_view_template_details`, `get_current_view_info`.
- Есть `QuerySpec`-трансляция (категории/уровни/типы/вид/комната + условия, AND/OR, единицы mm), `ElementDetails`, `ViewSettings` (view range, crop, скрытые категории/worksets, controlled params).
- Phase 0 hardening: осиротевшие tool_calls при Stop, guard смены документа, санитизация control/bidi, SQLite pragmas, resilient startup.
- Деплой: Revit 2023 получает отдельную сборку **Debug.R23** (`%APPDATA%\SmartCon\2023`); `tools\build-all-configs.ps1` собирает **7** конфигураций; R19/R21/R22 Agent исключён (нужен Revit 2023+ API).
- Markdig 1.6.0 добавлен централизованно (`Directory.Packages.props`), `Markdig.dll` копируется в output.

## Проверено

- R25 build 0/0; R25 задеплоен в `%APPDATA%\SmartCon\2025`.
- R23 build 0/0; задеплоен в `%APPDATA%\SmartCon\2023`.
- R24 App build 0/0 (net48, до Markdig — требует повторного прогона).
- R19 build 0/0 после исключения Agent.
- Unit Agent: 35/35 (до последних UI-правок, логика не менялась).
- Integration R25: 14/14 (`AgentReadToolsTests`, `AgentQueryToolsTests`) — до Markdig/UI-правок.
- Ручной тест UI: Revit 2025 — панель, ответ, скриншот, tool cards (идёт приёмка).

## Не проверено / долг

1. **Multi-version sweep после Markdig и UI-правок**: R24, R26, R27 (R23/R25 уже зелёные; R19/R21/R22 Agent не содержат, но Core менялся).
2. Повторный прогон unit + integration после последних правок.
3. Ручная приёмка новых элементов UI: скролл колесом над Markdown, полноэкранный просмотр скриншота, раскрытие tool-карточек.
4. **P2 write-tools отсутствуют** — вся запись (rename_views, copy_view_settings, configure_views, apply_view_template, set_parameters, remap_parameter_values), dry-run, change sets, undo, L0–L6 валидация.
5. Токен-бюджет/compaction, token_usage/tool_calls в БД не заполняются.
6. Skills/MCP/Exa не подключены; plan validation gates не реализованы.
7. DPAPI для ключей не сделан — используется `%APPDATA%\AGK\SmartCon\Agent\secrets.env`.
8. I-14 формально требует зафиксировать `AgentDatabase` как второго SQLite-владельца (ADR/инвариант) — docs не менять без разрешения.

## Ночной прогон: рекомендуемый порядок

1. Прочитать `docs/invariants.md`, `docs/architecture/dependency-rule.md`, `docs/agent/00-strategy/mvp-plan.md` (§5.2, §7, §7A, §9).
2. Сборки: `Debug.R19, Debug.R21, Debug.R24, Debug.R26, Debug.R27` по одной (не чередовать в цикле; `build-all-configs.ps1` тоже подходит). Исправить multi-version ошибки.
3. Тесты: `dotnet test src/SmartCon.Tests -c Debug.R25 --filter "FullyQualifiedName~Agent"`; integration `dotnet run --project src/SmartCon.IntegrationTests -c Debug.R25 --framework net8.0-windows -- --treenode-filter "/*/*/*Agent*/*"`.
4. **P2 write-layer** через `ITransactionService` (I-03):
   - `rename_views` (items, dryRun, atomic) + уникальность имён + change set.
   - `copy_view_settings` / `configure_views` (crop/viewRange/visibility/template) — главный кейс R23.
   - `set_parameters` / `remap_parameter_values` с dry-run и change items.
   - change set пишется в `agent.db`; `undo_last` откатывает последний применённый set (только созданные нами изменения, без delete-all).
   - подтверждение: Risk=Write/Destructive → orchestrator требует явного user-confirm (plan digest), dry-run по умолчанию.
5. Unit-тесты на plan/digest/change-set/undo; интеграционные тесты write-tools на сидированной модели (transaction + read-back).
6. Обновить HANDOFF в конце; остановиться перед ручной приёмкой записи (Revit + пользователь).

## Команды

```bash
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R25
dotnet test src/SmartCon.Tests/SmartCon.Tests.csproj -c Debug.R25 --filter "FullyQualifiedName~Agent"
dotnet run --project src/SmartCon.IntegrationTests -c Debug.R25 --framework net8.0-windows -- --treenode-filter "/*/*/*Agent*/*"
```

Деплой вручную после сборки (Revit закрыт):
- 2025: copy `src\SmartCon.App\bin\Debug.R25\net8.0-windows\win-x64\*.dll` → `%APPDATA%\SmartCon\2025`
- 2023: copy `src\SmartCon.App\bin\Debug.R23\net48\win-x64\*.dll` → `%APPDATA%\SmartCon\2023`

## Ограничения

- НЕ коммитить/пушить, не менять docs/ADR/changelog вне `docs/agent/` без разрешения.
- Ключи не логировать; `secrets.env` вне репозитория.
- Exa — только обезличенные технические запросы.
- Брандмауэрные правила Revit 2025 `Revit2025_BlockNetwork_*` отключены (не удалены) — при проблемах с сетью проверить их.
- Write-tools MVP не пишут в workshared central.