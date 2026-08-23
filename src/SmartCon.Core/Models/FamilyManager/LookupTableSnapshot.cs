namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// FHV11 (Issue #238): snapshot of one lookup table (таблица поиска /
/// <c>FamilySizeTable</c>) embedded in a loadable family. The content is the
/// raw CSV produced by <c>FamilySizeTableManager.ExportSizeTable</c>,
/// normalized (CRLF→LF, trailing whitespace trimmed) — the CSV is Revit's own
/// machine-oriented format with <c>##spec##unit</c> header annotations and raw
/// values, so it is locale-invariant by construction (unlike
/// <c>FamilySizeTable.AsValueString</c> display formatting). Part of the
/// content hash (LOOKUP section): an edit of lookup-table values shifts the
/// family hash and a re-import is accepted as a new version instead of a
/// false Duplicate.
/// </summary>
/// <param name="Name">Table name (CSV file base name at import time).</param>
/// <param name="CsvContent">Normalized raw CSV export of the table.</param>
public sealed record LookupTableSnapshot(string Name, string CsvContent);
