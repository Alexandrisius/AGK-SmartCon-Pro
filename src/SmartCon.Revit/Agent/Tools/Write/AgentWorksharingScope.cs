using Autodesk.Revit.DB;
using Autodesk.Revit.Exceptions;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// One element or workset the current user cannot write in a workshared model.
/// A blocked item is skipped, not failed: the rest of the batch is written.
/// </summary>
internal sealed class WorksharingBlock
{
    internal required string Code { get; init; }

    internal required string Reason { get; init; }

    internal long? ElementId { get; init; }

    internal string? Owner { get; init; }
}

/// <summary>
/// Ownership gate for writes into a workshared document. A local copy is not a
/// different kind of document: edits are allowed, but an element owned by
/// another user (or one that is out of date with central) rolls back the whole
/// Revit transaction. This gate takes ownership of the batch targets in one
/// <c>WorksharingUtils.CheckoutElements</c> call before the transaction and
/// reports every element that stayed unavailable, so a batch can skip those and
/// still write the rest.
///
/// A detached workshared copy has no central to borrow from: edits are local and
/// allowed (<c>IsWorkshared == true</c>, <c>IsDetached == true</c>).
/// </summary>
internal sealed class AgentWorksharingScope
{
    internal const string NotWorksharedMode = "not_workshared";
    internal const string DetachedMode = "detached";
    internal const string LocalCopyMode = "local_copy";

    private readonly Document _document;
    private readonly Dictionary<long, WorksharingBlock> _blocked = new();
    private readonly HashSet<long> _requested = new();
    private readonly HashSet<long> _granted = new();

    private AgentWorksharingScope()
    {
        _document = null!;
        Mode = NotWorksharedMode;
    }

    private AgentWorksharingScope(Document document)
    {
        _document = document;
        IsWorkshared = document.IsWorkshared;
        IsDetached = IsWorkshared && document.IsDetached;
        Mode = !IsWorkshared
            ? NotWorksharedMode
            : IsDetached
                ? DetachedMode
                : LocalCopyMode;
    }

    internal string Mode { get; }

    internal bool IsWorkshared { get; }

    internal bool IsDetached { get; }

    /// <summary>A local copy that can borrow from a central model.</summary>
    internal bool NeedsCentral => IsWorkshared && !IsDetached;

    internal int RequestedCount => _requested.Count;

    internal int CheckedOutCount => _granted.Count;

    internal int BlockedCount => _blocked.Count;

    /// <summary>Set when central access itself failed: nothing may be written.</summary>
    internal string? FailureCode { get; private set; }

    internal string? FailureMessage { get; private set; }

    internal static AgentWorksharingScope Create(Document document) => new(document);

    /// <summary>Gate for calls that run without a document scope (tests, family tools).</summary>
    internal static AgentWorksharingScope None { get; } = new();

    /// <summary>
    /// Takes ownership of as many ids as possible in one call (Autodesk: checkout
    /// all elements at once, not one by one). Must run on the Revit thread before
    /// the write transaction. Ids that do not exist are ignored by the caller;
    /// <see cref="WorksharingUtils.CheckoutElements"/> throws for them.
    /// </summary>
    internal void Checkout(IEnumerable<ElementId> ids, CancellationToken ct)
    {
        if (!NeedsCentral)
            return;

        var candidates = new List<ElementId>();
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            if (id is null || id == ElementId.InvalidElementId)
                continue;
            if (_requested.Add(id.GetValue()))
                candidates.Add(id);
        }

        if (candidates.Count == 0)
            return;

        HashSet<ElementId> granted = new();
        try
        {
            using var options = new TransactWithCentralOptions();
            options.SetLockCallback(new NoWaitLockCallback());
            var owned = WorksharingUtils.CheckoutElements(
                _document, new HashSet<ElementId>(candidates), options);
            if (owned is not null)
            {
                foreach (var id in owned)
                    granted.Add(id);
            }
        }
        catch (System.OperationCanceledException)
        {
            throw;
        }
        catch (CentralFileCommunicationException ex)
        {
            FailCentral(
                "central_unavailable",
                "The central model is unreachable: " + ex.Message,
                "check the central path/network, then retry");
            return;
        }
        catch (CentralModelAccessDeniedException ex)
        {
            FailCentral(
                "central_access_denied",
                "Access to the central model was denied: " + ex.Message,
                "ask the BIM manager for access or open a local copy with the right permissions");
            return;
        }
        catch (CentralModelContentionException ex)
        {
            FailCentral(
                "central_busy",
                "The central model is locked by another user: " + ex.Message,
                "retry after the other user finishes the operation");
            return;
        }
        catch (CentralModelException ex)
        {
            FailCentral(
                "central_error",
                "Revit could not access the central model: " + ex.Message,
                "check the central model state and retry");
            return;
        }
        catch (RevitServerCommunicationException ex)
        {
            FailCentral(
                "central_unavailable",
                "The Revit Server central model is unreachable: " + ex.Message,
                "check the server connection, then retry");
            return;
        }
        catch (RevitServerInternalException ex)
        {
            FailCentral(
                "central_error",
                "Revit Server reported an internal error: " + ex.Message,
                "ask the server administrator, then retry");
            return;
        }
        catch (Autodesk.Revit.Exceptions.ArgumentException ex)
        {
            FailCentral(
                "worksharing_failed",
                "Revit refused the ownership request: " + ex.Message,
                "reload the local copy, then retry");
            return;
        }
        catch (Exception ex)
        {
            FailCentral(
                "worksharing_failed",
                "The ownership request failed (" + ex.GetType().Name + "): " + ex.Message,
                "retry; if it repeats, reload the local copy");
            return;
        }

        foreach (var id in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (BlockIfStale(id) || BlockIfOwnedByOther(id))
                continue;

            if (granted.Contains(id))
            {
                _granted.Add(id.GetValue());
                continue;
            }

            var owner = SafeOwner(id, out _);
            Block(id, "element_checkout_denied",
                "Revit did not grant ownership of element " + id.GetValue() +
                (string.IsNullOrWhiteSpace(owner) ? string.Empty : " (owner: " + owner + ")") +
                ". [Action: reload latest and retry this element]",
                owner);
        }
    }

    /// <summary>
    /// Read-only ownership check for a dry run: reports what an apply would skip
    /// without borrowing anything from the central model. The status queries read
    /// Revit's local cache, so the probe never touches the central file.
    /// </summary>
    internal void Probe(IEnumerable<ElementId> ids, CancellationToken ct)
    {
        if (!NeedsCentral)
            return;

        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            if (id is null || id == ElementId.InvalidElementId)
                continue;
            if (!_requested.Add(id.GetValue()))
                continue;

            // An unowned element is not blocked: the apply borrows it implicitly.
            if (!BlockIfStale(id) && !BlockIfOwnedByOther(id))
                _granted.Add(id.GetValue());
        }
    }

    private bool BlockIfStale(ElementId id)
    {
        var updates = SafeUpdates(id);
        if (updates == ModelUpdatesStatus.DeletedInCentral)
        {
            Block(id, "element_deleted_in_central",
                "Element " + id.GetValue() + " was deleted in the central model. " +
                "[Action: reload latest; the element will disappear from the local copy]");
            return true;
        }

        if (updates == ModelUpdatesStatus.UpdatedInCentral)
        {
            Block(id, "element_out_of_date",
                "Element " + id.GetValue() + " has newer changes in the central model. " +
                "[Action: run Reload Latest, then retry this element]");
            return true;
        }

        return false;
    }

    private bool BlockIfOwnedByOther(ElementId id)
    {
        var owner = SafeOwner(id, out var status);
        if (status != CheckoutStatus.OwnedByOtherUser)
            return false;

        Block(id, "element_owned_by_other_user",
            "Element " + id.GetValue() + " is checked out by " +
            (string.IsNullOrWhiteSpace(owner) ? "another user" : owner) +
            ". [Action: ask that user to synchronize and relinquish it, then retry this element]",
            owner);
        return true;
    }

    /// <summary>
    /// Whether this element must be skipped right now. Unknown ids (resolved
    /// inside the transaction, not part of the batch targets) are checked against
    /// Revit's cached ownership/update status: edits may still borrow unowned
    /// elements automatically, but an element owned by another user or out of
    /// date rolls the transaction back.
    /// </summary>
    internal bool TryGetBlock(ElementId? elementId, out WorksharingBlock block)
    {
        block = null!;
        if (!NeedsCentral || elementId is null || elementId == ElementId.InvalidElementId)
            return false;

        var key = elementId.GetValue();
        if (_blocked.TryGetValue(key, out var known))
        {
            block = known;
            return true;
        }

        if (_granted.Contains(key))
            return false;

        var updates = SafeUpdates(elementId);
        if (updates == ModelUpdatesStatus.DeletedInCentral)
        {
            block = Remember(elementId, "element_deleted_in_central",
                "Element " + key + " was deleted in the central model. " +
                "[Action: reload latest; the element will disappear from the local copy]");
            return true;
        }

        if (updates == ModelUpdatesStatus.UpdatedInCentral)
        {
            block = Remember(elementId, "element_out_of_date",
                "Element " + key + " has newer changes in the central model. " +
                "[Action: run Reload Latest, then retry this element]");
            return true;
        }

        var owner = SafeOwner(elementId, out var status);
        if (status == CheckoutStatus.OwnedByOtherUser)
        {
            block = Remember(elementId, "element_owned_by_other_user",
                "Element " + key + " is checked out by " +
                (string.IsNullOrWhiteSpace(owner) ? "another user" : owner) +
                ". [Action: ask that user to synchronize and relinquish it, then retry this element]",
                owner);
            return true;
        }

        return false;
    }

    /// <summary>
    /// A new element lands in the active workset. Creating in a workset owned by
    /// another user fails the transaction, so take that one workset before it.
    /// A dry run only probes it: an unowned workset is left for the apply to borrow.
    /// </summary>
    internal bool TryEnsureActiveWorkset(bool probeOnly, out WorksharingBlock block)
    {
        block = null!;
        if (!NeedsCentral)
            return true;

        WorksetTable table;
        WorksetId activeId;
        Workset workset;
        try
        {
            table = _document.GetWorksetTable();
            activeId = table.GetActiveWorksetId();
            workset = table.GetWorkset(activeId);
        }
        catch (Exception ex)
        {
            block = new WorksharingBlock
            {
                Code = "workset_not_found",
                Reason = "Revit could not read the active workset: " + ex.Message +
                         " [Action: open the Worksets dialog and pick an editable workset]"
            };
            return false;
        }

        if (workset is not null && workset.IsEditable)
            return true;

        if (probeOnly)
        {
            if (string.IsNullOrWhiteSpace(workset?.Owner))
                return true;

            block = WorksetBlock(workset!);
            return false;
        }

        HashSet<WorksetId> granted = new();
        try
        {
            using var options = new TransactWithCentralOptions();
            options.SetLockCallback(new NoWaitLockCallback());
            var owned = WorksharingUtils.CheckoutWorksets(
                _document, new HashSet<WorksetId> { activeId }, options);
            if (owned is not null)
            {
                foreach (var id in owned)
                    granted.Add(id);
            }
        }
        catch (System.OperationCanceledException)
        {
            throw;
        }
        catch (CentralFileCommunicationException ex)
        {
            FailCentral("central_unavailable",
                "The central model is unreachable: " + ex.Message,
                "check the central path/network, then retry");
        }
        catch (CentralModelAccessDeniedException ex)
        {
            FailCentral("central_access_denied",
                "Access to the central model was denied: " + ex.Message,
                "ask the BIM manager for access, then retry");
        }
        catch (CentralModelContentionException ex)
        {
            FailCentral("central_busy",
                "The central model is locked by another user: " + ex.Message,
                "retry after the other user finishes the operation");
        }
        catch (CentralModelException ex)
        {
            FailCentral("central_error",
                "Revit could not access the central model: " + ex.Message,
                "check the central model state and retry");
        }
        catch (RevitServerCommunicationException ex)
        {
            FailCentral("central_unavailable",
                "The Revit Server central model is unreachable: " + ex.Message,
                "check the server connection, then retry");
        }
        catch (RevitServerInternalException ex)
        {
            FailCentral("central_error",
                "Revit Server reported an internal error: " + ex.Message,
                "ask the server administrator, then retry");
        }
        catch (Exception ex)
        {
            FailCentral("worksharing_failed",
                "Revit refused the workset ownership request: " + ex.Message,
                "reload the local copy, then retry");
        }

        if (FailureCode is not null)
        {
            block = new WorksharingBlock
            {
                Code = FailureCode,
                Reason = FailureMessage ?? "The central model is unavailable."
            };
            return false;
        }

        if (granted.Contains(activeId))
            return true;

        block = workset is null
            ? new WorksharingBlock
            {
                Code = "workset_owned_by_other_user",
                Reason = "The active workset is not editable, and a new element would be " +
                         "created in it. [Action: switch the active workset to one you own " +
                         "(Worksets dialog), then retry]"
            }
            : WorksetBlock(workset);
        return false;
    }

    private static WorksharingBlock WorksetBlock(Workset workset)
    {
        var name = string.IsNullOrWhiteSpace(workset.Name)
            ? "the active workset"
            : "'" + workset.Name + "'";
        var owner = workset.Owner;
        return new WorksharingBlock
        {
            Code = "workset_owned_by_other_user",
            Owner = owner,
            Reason = "Workset " + name + " is owned by " +
                     (string.IsNullOrWhiteSpace(owner) ? "another user" : owner) +
                     ", and a new element would be created in it. " +
                     "[Action: switch the active workset to one you own (Worksets dialog) " +
                     "or ask the owner to relinquish it, then retry]"
        };
    }

    internal Dictionary<string, object?> ToPayload()
    {
        var blocked = new List<object>();
        foreach (var entry in _blocked.Values)
        {
            if (blocked.Count >= 20)
                break;

            blocked.Add(new
            {
                elementId = entry.ElementId,
                code = entry.Code,
                owner = entry.Owner
            });
        }

        return new Dictionary<string, object?>
        {
            ["mode"] = Mode,
            ["isLocalCopy"] = NeedsCentral,
            ["requested"] = RequestedCount,
            ["checkedOut"] = CheckedOutCount,
            ["blocked"] = BlockedCount,
            ["blockedSample"] = blocked,
            ["blockedTruncated"] = BlockedCount > blocked.Count
        };
    }

    private void FailCentral(string code, string message, string action)
    {
        FailureCode ??= code;
        FailureMessage ??= message + " [Action: " + action + "]";
    }

    private void Block(long id, string code, string reason, string? owner = null)
    {
        _blocked[id] = new WorksharingBlock
        {
            Code = code,
            Reason = reason,
            ElementId = id,
            Owner = string.IsNullOrWhiteSpace(owner) ? null : owner
        };
    }

    private void Block(ElementId id, string code, string reason, string? owner = null) =>
        Block(id.GetValue(), code, reason, owner);

    private WorksharingBlock Remember(
        ElementId id,
        string code,
        string reason,
        string? owner = null)
    {
        var key = id.GetValue();
        if (_blocked.TryGetValue(key, out var existing))
            return existing;

        var block = new WorksharingBlock
        {
            Code = code,
            Reason = reason,
            ElementId = key,
            Owner = string.IsNullOrWhiteSpace(owner) ? null : owner
        };
        _blocked[key] = block;
        return block;
    }

    private ModelUpdatesStatus? SafeUpdates(ElementId id)
    {
        try
        {
            return WorksharingUtils.GetModelUpdatesStatus(_document, id);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private string? SafeOwner(ElementId id, out CheckoutStatus? status)
    {
        status = null;
        try
        {
            var owner = string.Empty;
            status = WorksharingUtils.GetCheckoutStatus(_document, id, out owner);
            return owner;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Revit waits and retries the central lock endlessly by default; a tool call
    /// must fail with a typed error instead of freezing the UI when the central is
    /// busy.
    /// </summary>
    private sealed class NoWaitLockCallback : ICentralLockedCallback
    {
        public bool ShouldWaitForLockAvailability() => false;
    }
}
