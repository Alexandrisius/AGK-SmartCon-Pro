using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Core.Threading;
using SmartCon.Revit.Agent.Tools.Families;

namespace SmartCon.Revit.Agent.Tools.Read;

/// <summary>
/// Read-only model health checks (G.10): visually duplicate sheet numbers,
/// invisible characters in names, plan/section views without a template, empty
/// schedules, unplaced loadable families and applied change sets that can still
/// be reverted. Nothing is modified — fixes belong to the matching write tools.
/// </summary>
public sealed class AuditModelTool : AgentReadToolBase
{
    internal const string DuplicateSheetNumbers = "duplicate_sheet_numbers";
    internal const string InvisibleCharacters = "invisible_characters";
    internal const string ViewsWithoutTemplate = "views_without_template";
    internal const string EmptySchedules = "empty_schedules";
    internal const string UnplacedFamilies = "unplaced_families";
    internal const string PendingChangeSets = "pending_change_sets";

    private static readonly string[] AllChecks =
    {
        DuplicateSheetNumbers, InvisibleCharacters, ViewsWithoutTemplate,
        EmptySchedules, UnplacedFamilies, PendingChangeSets
    };

    private readonly IAgentChangeSetStore _changeSets;

    public AuditModelTool(IRevitContext revitContext, IAgentChangeSetStore changeSets)
        : base(revitContext)
    {
        _changeSets = changeSets ?? throw new ArgumentNullException(nameof(changeSets));
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "audit_model",
        Description =
            "Runs read-only health checks on the active model and reports findings without " +
            "changing anything: duplicate_sheet_numbers (numbers that differ only by invisible " +
            "characters), invisible_characters in view/sheet/schedule names, " +
            "views_without_template (plans and sections), empty_schedules (0 data rows), " +
            "unplaced_families (loadable families with no placed instances) and " +
            "pending_change_sets (applied SmartCon change sets that can still be undone). " +
            "checks selects a subset; limit/offset page the items of every check. Fixes are NOT " +
            "applied — sanitize_sheet_numbers (only after the user confirms the invisible " +
            "characters are not intentional album numbering), apply_view_template, " +
            "delete_schedules, purge_families, undo_until.",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        InputSchemaJson = @"{""type"":""object"",""properties"":{" + DocumentIdPropertyJson + @"""checks"":{""type"":""array"",""description"":""Subset of checks; omit to run all"",""items"":{""type"":""string"",""enum"":[""duplicate_sheet_numbers"",""invisible_characters"",""views_without_template"",""empty_schedules"",""unplaced_families"",""pending_change_sets""]}},""limit"":{""type"":""integer"",""default"":50,""maximum"":200},""offset"":{""type"":""integer"",""default"":0}},""additionalProperties"":false}"
    };

    protected override ToolResult Execute(
        Document doc,
        JsonElement args,
        CancellationToken ct)
    {
        var requested = AgentJson.GetStringArray(args, "checks");
        var limit = Math.Max(1, Math.Min(AgentJson.GetInt(args, "limit", 50), 200));
        var offset = Math.Max(0, AgentJson.GetInt(args, "offset", 0));

        var known = new HashSet<string>(requested, StringComparer.Ordinal);
        known.IntersectWith(new HashSet<string>(AllChecks, StringComparer.Ordinal));
        var unknown = requested.Where(name => !known.Contains(name)).ToList();

        if (requested.Count > 0 && known.Count == 0)
        {
            return ToolResult.Fail(
                "unknown_check",
                "None of the requested checks exist: " + string.Join(", ", unknown) +
                ". Valid checks: " + string.Join(", ", AllChecks) + ".");
        }

        var checksToRun = requested.Count == 0 ? AllChecks : AllChecks.Where(known.Contains).ToArray();
        var reports = new List<object>(checksToRun.Length);
        var parts = new List<string>(checksToRun.Length);
        var totalFindings = 0;

        foreach (var check in checksToRun)
        {
            ct.ThrowIfCancellationRequested();
            var findings = check switch
            {
                DuplicateSheetNumbers => FindDuplicateSheetNumbers(doc, ct),
                InvisibleCharacters => FindInvisibleCharacters(doc, ct),
                ViewsWithoutTemplate => FindViewsWithoutTemplate(doc, ct),
                EmptySchedules => FindEmptySchedules(doc, ct),
                UnplacedFamilies => FindUnplacedFamilies(doc, ct),
                _ => FindPendingChangeSets(doc, ct)
            };

            totalFindings += findings.Count;
            parts.Add(check + "=" + findings.Count);
            reports.Add(new
            {
                name = check,
                count = findings.Count,
                items = findings.Skip(offset).Take(limit).ToList()
            });
        }

        var payload = AgentJson.Serialize(new
        {
            documentId = AgentDocumentIdentity.GetDocumentId(doc),
            checks = reports,
            totalFindings,
            unknownChecks = unknown.Count > 0 ? unknown : null
        });

        return ToolResult.Ok(
            payload,
            totalFindings + " finding(s) (" + string.Join(", ", parts) +
            "). Nothing was changed.");
    }

    private static List<AuditFinding> FindDuplicateSheetNumbers(Document doc, CancellationToken ct)
    {
        var sheets = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSheet))
            .Cast<ViewSheet>()
            .ToList();

        var duplicates = AgentSheetReader.FindVisualDuplicateNumbers(sheets);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var findings = new List<AuditFinding>();
        foreach (var sheet in sheets)
        {
            ct.ThrowIfCancellationRequested();
            var visual = AgentTextAudit.Visual(sheet.SheetNumber);
            if (!duplicates.Contains(visual))
                continue;

            counts.TryGetValue(visual, out var seen);
            counts[visual] = seen + 1;
            findings.Add(new AuditFinding
            {
                Kind = DuplicateSheetNumbers,
                TargetId = sheet.Id.GetValue(),
                Name = sheet.SheetNumber,
                Detail = "Sheet number looks like '" + visual + "' together with " +
                         (counts[visual] - 1) + " other sheet(s); the values differ only by " +
                         "invisible characters. Invisible characters are also a legitimate way " +
                         "to keep continuous numbering across albums (every Revit number stays " +
                         "unique); do NOT sanitize before the user confirms the trick is not " +
                         "intentional — check get_browser_organization(target:\"sheets\") for the " +
                         "album grouping first."
            });
        }

        return findings;
    }

    private static List<AuditFinding> FindInvisibleCharacters(Document doc, CancellationToken ct)
    {
        var findings = new List<AuditFinding>();

        foreach (var sheet in new FilteredElementCollector(doc)
                     .OfClass(typeof(ViewSheet))
                     .Cast<ViewSheet>())
        {
            ct.ThrowIfCancellationRequested();
            AddIfInvisible(findings, sheet.Id.GetValue(), "sheet number", sheet.SheetNumber);
            AddIfInvisible(findings, sheet.Id.GetValue(), "sheet name", sheet.Name);
        }

        foreach (var view in CollectListableViews(doc))
        {
            ct.ThrowIfCancellationRequested();
            if (view is ViewSchedule)
                AddIfInvisible(findings, view.Id.GetValue(), "schedule name", view.Name);
            else
                AddIfInvisible(findings, view.Id.GetValue(), "view name", view.Name);
        }

        return findings;
    }

    private static void AddIfInvisible(
        List<AuditFinding> findings,
        long id,
        string what,
        string? value)
    {
        if (!AgentTextAudit.HasInvisibleCharacters(value))
            return;

        findings.Add(new AuditFinding
        {
            Kind = InvisibleCharacters,
            TargetId = id,
            Name = value,
            Detail = "The " + what + " contains invisible character(s); code points: " +
                     string.Join(",", AgentTextAudit.CodePoints(value))
        });
    }

    private static List<AuditFinding> FindViewsWithoutTemplate(Document doc, CancellationToken ct)
    {
        var planish = new HashSet<ViewType>
        {
            ViewType.FloorPlan, ViewType.CeilingPlan, ViewType.AreaPlan, ViewType.Section
        };

        var findings = new List<AuditFinding>();
        foreach (var view in CollectListableViews(doc))
        {
            ct.ThrowIfCancellationRequested();
            if (view.IsTemplate || !planish.Contains(view.ViewType))
                continue;

            if (view.ViewTemplateId != ElementId.InvalidElementId)
                continue;

            findings.Add(new AuditFinding
            {
                Kind = ViewsWithoutTemplate,
                TargetId = view.Id.GetValue(),
                Name = view.Name,
                Detail = view.ViewType + " has no view template (apply_view_template can set one)"
            });
        }

        return findings;
    }

    private static List<AuditFinding> FindEmptySchedules(Document doc, CancellationToken ct)
    {
        var findings = new List<AuditFinding>();
        foreach (var schedule in AgentScheduleReader.Collect(doc))
        {
            ct.ThrowIfCancellationRequested();
            if (schedule.IsTemplate
                || schedule.IsInternalKeynoteSchedule
                || schedule.IsTitleblockRevisionSchedule)
                continue;

            int rows;
            try
            {
                rows = AgentScheduleReader.ReadRowCount(schedule, skipHeader: true);
            }
            catch (Exception)
            {
                continue;
            }

            if (rows > 0)
                continue;

            findings.Add(new AuditFinding
            {
                Kind = EmptySchedules,
                TargetId = schedule.Id.GetValue(),
                Name = schedule.Name,
                Detail = "The schedule has 0 data rows (get_schedule_definition shows why)"
            });
        }

        return findings;
    }

    private static List<AuditFinding> FindUnplacedFamilies(Document doc, CancellationToken ct)
    {
        var used = new HashSet<long>();
        foreach (var element in new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)))
        {
            ct.ThrowIfCancellationRequested();
            if (element is FamilyInstance instance && instance.Symbol is not null)
                used.Add(instance.Symbol.Family.Id.GetValue());
        }

        var findings = new List<AuditFinding>();
        foreach (var family in AgentFamilyReader.LoadableFamilies(doc))
        {
            ct.ThrowIfCancellationRequested();
            if (used.Contains(family.Id.GetValue()))
                continue;

            findings.Add(new AuditFinding
            {
                Kind = UnplacedFamilies,
                TargetId = family.Id.GetValue(),
                Name = family.Name,
                Detail = family.GetFamilySymbolIds().Count + " type(s), no placed instances" +
                         (family.IsInPlace ? ", in-place" : string.Empty)
            });
        }

        return findings;
    }

    private List<AuditFinding> FindPendingChangeSets(Document doc, CancellationToken ct)
    {
        var documentId = AgentDocumentIdentity.GetDocumentId(doc);
        var headers = AsyncBridge.RunSync(() => _changeSets.ListAsync(documentId, 100, 0, ct));

        var applied = new List<ChangeSetHeader>();
        foreach (var header in headers)
        {
            if (header.AppliedAt.HasValue)
                applied.Add(header);
        }

        return applied
            .Select(header => new AuditFinding
            {
                Kind = PendingChangeSets,
                Name = header.Operation,
                Detail = "Applied " + header.AppliedAt!.Value.ToString(
                    "u", System.Globalization.CultureInfo.InvariantCulture) +
                         " — can still be reverted (undo_last / undo_until)"
            })
            .ToList();
    }

    private static IEnumerable<View> CollectListableViews(Document doc)
    {
        foreach (var element in new FilteredElementCollector(doc).OfClass(typeof(View)))
        {
            if (element is not View view || !IsListable(view))
                continue;

            yield return view;
        }
    }

    private static bool IsListable(View view) =>
        view.ViewType != ViewType.DrawingSheet
        && view.ViewType != ViewType.Internal
        && view.ViewType != ViewType.ProjectBrowser
        && view.ViewType != ViewType.SystemBrowser
        && view.ViewType != ViewType.Undefined;

    private sealed class AuditFinding
    {
        public string Kind { get; init; } = string.Empty;

        public long? TargetId { get; init; }

        public string? Name { get; init; }

        public string Detail { get; init; } = string.Empty;
    }
}
