namespace SmartCon.Core.Services.Agent;

/// <summary>
/// Shared pieces of a list tool payload: field projection of a record, the paging
/// envelope and the hints that tell the model how to narrow a long list.
/// </summary>
public static class AgentListPayload
{
    /// <summary>Pages with at least this many records get the compact-fields hint.</summary>
    public const int CompactHintThreshold = 50;

    /// <summary>
    /// Resolves the requested field names against the fields a record can carry.
    /// A missing (or blank) request selects <paramref name="defaultFields"/>; names
    /// are matched case-insensitively and returned in the requested order.
    /// </summary>
    public static FieldSelection SelectFields(
        IReadOnlyList<string> availableFields,
        IReadOnlyList<string>? requestedFields,
        IReadOnlyList<string> defaultFields)
    {
        var isDefault = requestedFields is null
                        || requestedFields.All(field => string.IsNullOrWhiteSpace(field));
        var selected = new List<string>();
        var unknown = new List<string>();
        foreach (var entry in isDefault ? defaultFields : requestedFields!)
        {
            if (string.IsNullOrWhiteSpace(entry))
                continue;

            var canonical = FindCanonical(availableFields, entry.Trim());
            if (canonical is null)
            {
                if (!unknown.Contains(entry.Trim(), StringComparer.OrdinalIgnoreCase))
                    unknown.Add(entry.Trim());
                continue;
            }

            if (!selected.Contains(canonical, StringComparer.OrdinalIgnoreCase))
                selected.Add(canonical);
        }

        return new FieldSelection(selected, availableFields, unknown, isDefault);
    }

    /// <summary>
    /// Copy of the record limited to <see cref="FieldSelection.Fields"/>. A field the
    /// record does not carry is omitted rather than emitted as null.
    /// </summary>
    public static Dictionary<string, object?> Project(
        IReadOnlyDictionary<string, object?> record,
        FieldSelection selection)
    {
        var projected = new Dictionary<string, object?>(selection.Fields.Count);
        foreach (var field in selection.Fields)
        {
            if (record.TryGetValue(field, out var value))
                projected[field] = value;
        }

        return projected;
    }

    /// <summary>Paging envelope of a page that starts at <paramref name="offset"/>.</summary>
    public static PageEnvelope BuildPage(int total, int offset, int returned)
    {
        var next = offset + returned;
        return new PageEnvelope(total, returned, offset, next < total ? next : null);
    }

    /// <summary>Adds <c>fields</c> (what the records carry) and <c>availableFields</c>.</summary>
    public static void AddFieldInfo(
        IDictionary<string, object?> payload,
        IReadOnlyList<string> fields,
        IReadOnlyList<string> availableFields)
    {
        payload["fields"] = fields;
        payload["availableFields"] = availableFields;
    }

    /// <summary>
    /// How to narrow a truncated page: <c>" Narrow with a, b, or c."</c> — the last
    /// way is joined with "or", one or two ways are joined without a comma.
    /// </summary>
    public static string TruncatedHint(IReadOnlyList<string> ways)
    {
        if (ways is null || ways.Count == 0)
            return string.Empty;

        if (ways.Count == 1)
            return " Narrow with " + ways[0] + ".";

        if (ways.Count == 2)
            return " Narrow with " + ways[0] + " or " + ways[1] + ".";

        var text = new System.Text.StringBuilder(" Narrow with ");
        for (var index = 0; index < ways.Count - 1; index++)
        {
            text.Append(ways[index]);
            text.Append(", ");
        }

        text.Append("or ").Append(ways[ways.Count - 1]).Append('.');
        return text.ToString();
    }

    /// <summary>
    /// What the compact records show and how to ask for other fields.
    /// <paramref name="more"/> is appended as the last sentence.
    /// </summary>
    public static string CompactFieldsHint(IReadOnlyList<string> fields, string more = "")
    {
        if (fields is null || fields.Count == 0)
            return string.Empty;

        return " Compact records show " + string.Join(", ", fields) +
               "; pass fields=[...] for other fields (availableFields)." + (more ?? string.Empty);
    }

    private static string? FindCanonical(IReadOnlyList<string> availableFields, string name)
    {
        foreach (var available in availableFields)
        {
            if (string.Equals(available, name, StringComparison.OrdinalIgnoreCase))
                return available;
        }

        return null;
    }

    /// <summary>Resolved field projection of one record shape.</summary>
    public sealed class FieldSelection
    {
        internal FieldSelection(
            List<string> fields,
            IReadOnlyList<string> availableFields,
            List<string> unknownFields,
            bool isDefault)
        {
            Fields = fields;
            AvailableFields = availableFields;
            UnknownFields = unknownFields;
            IsDefault = isDefault;
        }

        /// <summary>Fields of a compact record, in the order to emit them.</summary>
        public IReadOnlyList<string> Fields { get; }

        /// <summary>Every field the record can carry.</summary>
        public IReadOnlyList<string> AvailableFields { get; }

        /// <summary>Requested names the record does not carry (empty when valid).</summary>
        public IReadOnlyList<string> UnknownFields { get; }

        /// <summary>True when no fields were requested and the default set is used.</summary>
        public bool IsDefault { get; }

        public bool IsValid => UnknownFields.Count == 0;

        /// <summary>Error text listing the unknown and the allowed names; null when valid.</summary>
        public string? Error => UnknownFields.Count == 0
            ? null
            : "Unknown field(s): " + string.Join(", ", UnknownFields) +
              ". Allowed fields: " + string.Join(", ", AvailableFields) + ".";
    }

    /// <summary>Paging envelope: total, returned, offset, nextOffset, truncated.</summary>
    public readonly struct PageEnvelope
    {
        internal PageEnvelope(int total, int returned, int offset, int? nextOffset)
        {
            Total = total;
            Returned = returned;
            Offset = offset;
            NextOffset = nextOffset;
        }

        public int Total { get; }

        public int Returned { get; }

        public int Offset { get; }

        public int? NextOffset { get; }

        public bool Truncated => NextOffset is not null;

        /// <summary>Writes the five paging fields into the tool payload.</summary>
        public void AddTo(IDictionary<string, object?> payload)
        {
            payload["total"] = Total;
            payload["returned"] = Returned;
            payload["offset"] = Offset;
            payload["nextOffset"] = NextOffset;
            payload["truncated"] = Truncated;
        }
    }
}
