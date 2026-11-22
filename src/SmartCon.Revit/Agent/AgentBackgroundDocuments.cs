using Autodesk.Revit.DB;

namespace SmartCon.Revit.Agent;

/// <summary>
/// Session registry of background (hidden) documents opened by the agent tools
/// (case 9 v2 multi-model reads, real-cases benchmark: «посмотри, как сделано в
/// модели-эталоне»). Maps documentId → live <see cref="Document"/> and
/// path → documentId so read tools can route by either.
///
/// I-05 exception (deliberate, read-only donors): the registry stores live
/// Document references of the current Revit session/process only — it is
/// process-local memory, never persisted between sessions, and the registered
/// documents are read-only donors on which the agent never opens write
/// transactions. The rule "do not store Document between transactions" still
/// applies to every write path; this registry exists so read tours can address
/// a background donor without re-opening the file per call.
///
/// Threading (I-01): all members must be called from the Revit API thread —
/// the same ExternalEvent callback the agent tools already run on. The lock is
/// defensive only (a tool could in theory be invoked from a test harness on
/// the same thread).
/// </summary>
internal static class AgentBackgroundDocuments
{
    private sealed class Entry
    {
        public string DocumentId { get; set; } = string.Empty;

        public Document Document { get; set; } = null!;

        public string? PathName { get; set; }
    }

    private static readonly object Sync = new();
    private static readonly Dictionary<string, Entry> ById =
        new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> IdByPath =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers (or refreshes) a background document. Re-registering the same
    /// documentId updates the path mapping; a path previously mapped to another
    /// documentId is re-pointed (the old document was closed and re-opened).
    /// </summary>
    internal static void Register(string documentId, Document document, string? pathName)
    {
        if (string.IsNullOrEmpty(documentId) || document is null)
            return;

        lock (Sync)
        {
            ById[documentId] = new Entry
            {
                DocumentId = documentId,
                Document = document,
                PathName = pathName
            };

            if (!string.IsNullOrEmpty(pathName))
                IdByPath[pathName!] = documentId;
        }
    }

    /// <summary>
    /// Resolves a background document by documentId. Stale entries (the wrapped
    /// document was closed outside the registry) are dropped on sight.
    /// </summary>
    internal static bool TryResolve(string documentId, out Document document)
    {
        document = null!;

        if (string.IsNullOrEmpty(documentId))
            return false;

        lock (Sync)
        {
            if (!ById.TryGetValue(documentId, out var entry)
                || !IsAlive(entry.Document))
            {
                RemoveLocked(documentId);
                return false;
            }

            document = entry.Document;
            return true;
        }
    }

    /// <summary>Resolves a background document by file path (case-insensitive).</summary>
    internal static bool TryResolveByPath(
        string path,
        out string documentId,
        out Document document)
    {
        documentId = string.Empty;
        document = null!;

        if (string.IsNullOrEmpty(path))
            return false;

        lock (Sync)
        {
            if (!IdByPath.TryGetValue(path, out var id)
                || !ById.TryGetValue(id, out var entry)
                || !IsAlive(entry.Document))
            {
                IdByPath.Remove(path);
                return false;
            }

            documentId = id;
            document = entry.Document;
            return true;
        }
    }

    /// <summary>Removes a registry entry (after Close). Returns true when it existed.</summary>
    internal static bool Unregister(string documentId)
    {
        if (string.IsNullOrEmpty(documentId))
            return false;

        lock (Sync)
        {
            return RemoveLocked(documentId);
        }
    }

    /// <summary>All live registry entries (for close_model without arguments).</summary>
    internal static IReadOnlyList<(string DocumentId, string? PathName)> ListAll()
    {
        lock (Sync)
        {
            var result = new List<(string, string?)>(ById.Count);
            foreach (var entry in ById.Values)
            {
                if (IsAlive(entry.Document))
                    result.Add((entry.DocumentId, entry.PathName));
            }

            return result;
        }
    }

    internal static void ClearForTests()
    {
        lock (Sync)
        {
            ById.Clear();
            IdByPath.Clear();
        }
    }

    private static bool RemoveLocked(string documentId)
    {
        if (!ById.TryGetValue(documentId, out var entry))
            return false;

        ById.Remove(documentId);
        if (!string.IsNullOrEmpty(entry.PathName)
            && IdByPath.TryGetValue(entry.PathName!, out var mappedId)
            && string.Equals(mappedId, documentId, StringComparison.Ordinal))
        {
            IdByPath.Remove(entry.PathName!);
        }

        return true;
    }

    private static bool IsAlive(Document document) =>
        document is not null && document.IsValidObject;
}
