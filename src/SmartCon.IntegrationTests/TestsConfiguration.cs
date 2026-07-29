using Nice3point.TUnit.Revit.Executors;
using TUnit.Core.Executors;

// Каждый тест и hook этого сборки выполняется на единственном API-потоке Revit
// (STA + message pumping). Точечный override возможен атрибутом [TestExecutor] на тесте.
// Язык и путь установки Revit по умолчанию: English_USA, C:\Program Files\Autodesk\Revit {version}.
// Для RU-only установки добавить:
//   [assembly: Nice3point.Revit.Injector.Attributes.RevitLanguage("RUS")]
[assembly: TestExecutor<RevitThreadExecutor>]

// Тесты — строго последовательно. TUnit по умолчанию параллелит, но все тесты
// живут в ОДНОМ процессе Revit: async-тесты чередуются на его потоке, и гонка
// «commit транзакции ExtensibleStorage одного документа параллельно с операциями
// над другими документами» даёт нативный AccessViolationException в
// Transaction.Commit (воспроизведено 2026-07-29, изолированный прогон — зелёный).
// Параллелизм здесь и бессмысленен: Revit API однопоточен (I-01), ускорения нет.
[assembly: NotInParallel]
