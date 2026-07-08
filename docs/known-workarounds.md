# Known Workarounds

> Единый реестр всех workaround'ов в проекте SmartCon.
> Загружать: при вопросе «почему этот код делает что-то странное?»
> Каждый workaround ссылается на Issue с полным root cause и rationale.

## Активные workaround'ы

| Issue | Workaround | Файл | Платформа | Описание |
|---|---|---|---|---|
| [#97](https://github.com/Alexandrisius/AGK-SmartCon-Pro/issues/97) | CS1739 format specifier workaround | All .cs files transitively referencing HelixToolkit/SharpDX | net48 (R19–R24) | HelixToolkit.Wpf.SharpDX 3.1.2 transitively pulls System.Runtime 4.3.0 via SharpDX, конфликтует с PolySharp DefaultInterpolatedStringHandler. `$"{x:F0}"` → `.ToString("F0", CultureInfo.InvariantCulture)`. + SharpGLTF.Core pinned to 1.0.4 (не 1.0.6 — v1.0.6 требует System.Text.Json 10.x, ломает net48). Permanent workaround. |
| [#95](https://github.com/Alexandrisius/AGK-SmartCon-Pro/issues/95) | Off-screen render recovery | `src/SmartCon.App/Diagnostics/BatchDialogRenderRecovery.cs` | net48 (R19–R24) | Белый batch-диалог после OpenDocumentFile + family upgrade. Revit убивает WPF render thread. Окно показывается off-screen, лечится WM_ENTERSIZEMOVE + SetWindowPos + RedrawWindow, затем перемещается на центр при WM_PAINT. |
| — | Retired→Deprecated status mapping | `src/SmartCon.Core/Models/FamilyManager/ContentStatusParser.cs`, `src/SmartCon.FamilyManager/Services/LocalCatalog/LocalCatalogProvider.cs:447` | All | `ContentStatus` enum сохраняет legacy значение `Retired` (3-й статус из старой 3-state модели) для backward compat с существующими DB-строками. UI показывает только 2 статуса (Active/Deprecated). При чтении из БД строка `"Retired"` маппится в `Deprecated` через `ContentStatusParser.Parse`. Колонка `content_status` в `catalog_items` сохранена без миграции. Без маппинга `Enum.Parse` упал бы на legacy-записях. |
| [#96](https://github.com/Alexandrisius/AGK-SmartCon-Pro/issues/96) | BalloonNudge (DockablePane freeze) | `src/SmartCon.Revit/Util/RevitBalloonNudge.cs` | All (net48 + net8) | REVIT-236376 / REVIT-237190: DockablePane freezes после OpenDocumentFile+Close (bake-in). InfoCenter balloon через AdWindows.dll форсирует Win32 focus event. Не gated to net48 — применяется и на net8 т.к. баг non-deterministic. Финальный балун «Импорт завершён». |
| [#77](https://github.com/Alexandrisius/AGK-SmartCon-Pro/issues/77) | Shared nested persist fallback | `src/SmartCon.Revit/FamilyManager/RevitFamilyTypeCatalogBaker.cs` | All | REVIT-198137: Revit 2023/2024.2 теряет shared nested family names после SaveAs. Имена извлекаются до закрытия документа и сохраняются отдельно. |
| — | VendorId workaround | `src/SmartCon.Revit/.../*Schema*.cs` | All | `SchemaBuilder.SetVendorId` требует ≥4 символа, но `.addin` VendorId="AGK" (3 символа). Решение: использовать `AGKSMARTCON` в Schema + `AccessLevel.Public/Public`. |
| — | PipeConnect modal (Revit API limitation) | `src/SmartCon.PipeConnect/Commands/PipeConnectCommand.cs:33` (`view.ShowDialog()`) | All | Revit принудительно откатывает `TransactionGroup` при возврате из `IExternalCommand.Execute` и `IExternalEventHandler.Execute` (Löbel). Долгоживущий `TransactionGroup` для live real-element preview + single-undo cancel возможен **только** в modal command context. Modeless невозможен. См. [ADR-043](adr/043-pipeconnect-modal-justification.md). Permanent workaround (пока UX не сменится на DirectContext3D preview). |
| [#114](https://github.com/Alexandrisius/AGK-SmartCon-Pro/issues/114) | System.Text.Encoding.CodePages pinned to 8.0.0 | `src/Directory.Packages.props:58` | All | Dependabot bump to 10.0.9 crashes SmartCon on load — `TypeInitializationException` in `LocalFamilyImportService` static constructor (`CodePagesEncodingProvider.Instance` incompatible with net8.0/net48). Pinned to 8.0.0. Add to `.github/dependabot.yml` ignore list. Temporary until SmartCon drops net8.0/net48. |

## Устаревшие workaround'ы (удалены)

| Issue | Workaround | Почему удалён | Коммит |
|---|---|---|---|
| — | BalloonNudge pre-ShowDialog | Не предотвращал белый диалог на net48. См. #95. | `f2465f0` |
| — | BalloonNudge в ExtractGeometryPerType | Лишний шум, не решал проблему. | `2ff43d1` |
| — | NullUiFreezeRecoveryService | Заменён на RevitUiFreezeRecoveryService для финального балуна. | `2ff43d1` |
