namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Per-category routing editor model (ADR-072, Phase 3): which routing
/// groups a system MEPCurve category exposes, how each group filters its
/// part candidates (fitting Revit category + <c>part_type</c> ordinals from
/// FamilyFacts, ADR-055) and which groups are multi-rule with size criteria
/// (manager-based: pipe/duct) vs single-value parameter rows (param-based:
/// flex/conduit/cable tray). Mirrors the Revit routing UI row-by-row —
/// the storage matrix is probe-verified (ADR-072 §2.7,
/// <c>RoutingStorageReality</c> 2026-08-29). Category ordinals are frozen
/// <c>BuiltInCategory</c> API constants (revitapidocs 2025/2026); part-type
/// ordinals are the frozen <c>PartType</c> API constants already pinned by
/// <see cref="PartTypeLabelMap"/>. Core cannot reference the Revit enums
/// (I-09), so the ints are hardcoded with their verification sources.
/// </summary>
public static class RoutingGroupCatalog
{
    // BuiltInCategory ordinals (revitapidocs 2025/2026; the four fitting
    // ids are also pinned by FamilyFactRuleSet).
    public const int PipeCurvesCategoryId = -2008044;
    public const int FlexPipeCurvesCategoryId = -2008050;
    public const int DuctCurvesCategoryId = -2008000;
    public const int FlexDuctCurvesCategoryId = -2008020;
    public const int ConduitCategoryId = -2008132;
    public const int CableTrayCategoryId = -2008130;
    public const int PipeFittingCategoryId = -2008049;
    public const int DuctFittingCategoryId = -2008010;
    public const int ConduitFittingCategoryId = -2008128;
    public const int CableTrayFittingCategoryId = -2008126;

    // PartType ordinals (Revit 2025 API — the same constants PartTypeLabelMap
    // pins to its labels; cable-tray families use either the generic fitting
    // part types or the channel/ladder-specific ones).
    private const int PartElbow = 5;
    private const int PartTee = 6;
    private const int PartTransition = 7;
    private const int PartCross = 8;
    private const int PartCap = 9;
    private const int PartTapPerpendicular = 10;
    private const int PartTapAdjustable = 11;
    private const int PartUnion = 13;
    private const int PartSpudPerpendicular = 21;
    private const int PartSpudAdjustable = 22;
    private const int PartFlange = 32;
    private const int PartTrayChannelElbow = 35;
    private const int PartTrayChannelVerticalElbow = 36;
    private const int PartTrayChannelCross = 37;
    private const int PartTrayChannelTee = 38;
    private const int PartTrayChannelTransition = 39;
    private const int PartTrayChannelUnion = 40;
    private const int PartTrayLadderElbow = 43;
    private const int PartTrayLadderVerticalElbow = 44;
    private const int PartTrayLadderCross = 45;
    private const int PartTrayLadderTee = 46;
    private const int PartTrayLadderTransition = 47;
    private const int PartTrayLadderUnion = 48;
    private const int PartEndCap = 53;
    private const int PartMechanicalCoupling = 60;

    // Connector-profile bits (frozen ConnectorProfileType semantics — the
    // same bitmask the connector_shape fact stores, owner stress test
    // 2026-09-01): a multi-shape transition carries BOTH bits.
    public const int ShapeRound = 1;
    public const int ShapeRectangular = 2;
    public const int ShapeOval = 4;

    private static readonly int[] ElbowParts = [PartElbow];
    private static readonly int[] JunctionParts = [PartTee, PartTapPerpendicular, PartTapAdjustable];
    private static readonly int[] TeeParts = [PartTee];
    private static readonly int[] CrossParts = [PartCross];
    private static readonly int[] TransitionParts = [PartTransition];
    private static readonly int[] UnionParts = [PartUnion];
    // The Revit routing dialog's single «Фланец» row (API group
    // MechanicalJoints: "joints that connect fitting to fitting, segment to
    // fitting, or segment to segment") accepts BOTH pipe flanges and
    // mechanical couplings — there is no other row for either (Autodesk
    // help "Add Flanges Automatically" / MEP forums on part-type visibility).
    private static readonly int[] MechanicalJointParts = [PartFlange, PartMechanicalCoupling];
    private static readonly int[] CapParts = [PartCap, PartEndCap];
    private static readonly int[] TakeoffParts =
        [PartTapPerpendicular, PartTapAdjustable, PartSpudPerpendicular, PartSpudAdjustable];
    private static readonly int[] TrayBendParts = [PartElbow, PartTrayChannelElbow, PartTrayLadderElbow];
    private static readonly int[] TrayVerticalBendParts =
        [PartElbow, PartTrayChannelVerticalElbow, PartTrayLadderVerticalElbow];
    private static readonly int[] TrayTeeParts = [PartTee, PartTrayChannelTee, PartTrayLadderTee];
    private static readonly int[] TrayCrossParts = [PartCross, PartTrayChannelCross, PartTrayLadderCross];
    private static readonly int[] TrayTransitionParts =
        [PartTransition, PartTrayChannelTransition, PartTrayLadderTransition];
    private static readonly int[] TrayUnionParts = [PartUnion, PartTrayChannelUnion, PartTrayLadderUnion];

    /// <summary>
    /// <c>true</c> for the six MEPCurve categories the routing editor
    /// supports (ADR-072 §2.4: the tab exists only for MEP categories).
    /// </summary>
    public static bool IsMepCurveCategory(int? revitCategoryId)
        => revitCategoryId is PipeCurvesCategoryId or FlexPipeCurvesCategoryId
            or DuctCurvesCategoryId or FlexDuctCurvesCategoryId
            or ConduitCategoryId or CableTrayCategoryId;

    /// <summary>
    /// <c>true</c> for pipe/duct (RoutingPreferenceManager-backed groups:
    /// multi-rule). Param-based categories (flex/conduit/cable tray) store
    /// one part per group parameter.
    /// </summary>
    public static bool IsManagerBased(int revitCategoryId)
        => revitCategoryId is PipeCurvesCategoryId or DuctCurvesCategoryId;

    /// <summary>
    /// <c>true</c> only for pipes — the ONLY category whose routing rules
    /// carry size-range criteria (DN spans) and a segment size table
    /// (owner decision 2026-08-30). Duct size availability is configured
    /// elsewhere; flex/conduit/cable-tray routing is a plain part choice
    /// without conditions — their groups expose no size UI and no segment
    /// row, and project-settings size analysis must not apply to them.
    /// </summary>
    public static bool HasSizeCriteria(int? revitCategoryId)
        => revitCategoryId is PipeCurvesCategoryId;

    /// <summary>
    /// <c>true</c> when the category exposes the preferred-junction setting
    /// (manager <c>PreferredJunctionType</c> on pipe/duct; the
    /// <c>RBS_CURVETYPE_PREFERRED_BRANCH_PARAM</c> integer on flex types).
    /// Conduit and cable tray have no such setting.
    /// </summary>
    public static bool HasPreferredJunction(int revitCategoryId)
        => revitCategoryId is PipeCurvesCategoryId or FlexPipeCurvesCategoryId
            or DuctCurvesCategoryId or FlexDuctCurvesCategoryId;

    /// <summary>The junctions manager group (tee/tap-dependent, Revit's
    /// abstract «Соединение» block — the editor labels it dynamically by
    /// the preferred junction type, owner review 2026-08-30).</summary>
    public static bool IsJunctionsManagerGroup(int? managerGroupType)
        => managerGroupType == (int)RoutingManagerGroup.Junctions;

    /// <summary>
    /// Revit routing dialog behavior: the junctions group holds BOTH tee and
    /// tap rules, but the rules of the non-preferred junction type are shown
    /// greyed out and unused. <c>preferredJunctionType</c>: 0 = tee, 1 = tap.
    /// </summary>
    public static bool IsInactiveJunctionPart(int? partTypeOrdinal, int preferredJunctionType)
        => partTypeOrdinal is { } part
            && (preferredJunctionType == 1 ? TeeParts : TakeoffParts).Contains(part);

    /// <summary>
    /// The Revit category id of the fitting families compatible with the
    /// given MEPCurve host category (picker category filter).
    /// </summary>
    public static int FittingCategoryOf(int hostCategoryId)
        => hostCategoryId switch
        {
            PipeCurvesCategoryId or FlexPipeCurvesCategoryId => PipeFittingCategoryId,
            DuctCurvesCategoryId or FlexDuctCurvesCategoryId => DuctFittingCategoryId,
            ConduitCategoryId => ConduitFittingCategoryId,
            CableTrayCategoryId => CableTrayFittingCategoryId,
            _ => 0,
        };

    /// <summary>
    /// The editor groups of a host category in Revit-UI order. Manager
    /// categories (pipe/duct) get the full group set incl. the read-only
    /// Segments row; param categories get their parameter rows. Conduit and
    /// cable tray "without Fittings" classes hide TEE/CROSS (ADR-072 §2.7) —
    /// <paramref name="withFittings"/> comes from the type's family key.
    /// </summary>
    public static IReadOnlyList<RoutingGroupDescriptor> GetGroups(int hostCategoryId, bool withFittings = true)
        => hostCategoryId switch
        {
            PipeCurvesCategoryId =>
            [
                // FHV21 (owner decision 2026-09-01): the Segments row is a
                // READ-ONLY view of the active version's per-version segment
                // configuration (mini-project content — set, ranges, order).
                // It shows each rule's REAL criterion and explains the
                // fitting dropdown sizes; editing happens in the mini only.
                SegmentRow(RoutingManagerGroup.Segments, "FM_Routing_Group_SegmentsPipe"),
                ManagerGroup(RoutingManagerGroup.Elbows, "FM_Routing_Group_Elbows", PipeFittingCategoryId, ElbowParts),
                ManagerGroup(RoutingManagerGroup.Junctions, "FM_Routing_Group_Junctions", PipeFittingCategoryId, JunctionParts),
                ManagerGroup(RoutingManagerGroup.Crosses, "FM_Routing_Group_Crosses", PipeFittingCategoryId, CrossParts),
                ManagerGroup(RoutingManagerGroup.Transitions, "FM_Routing_Group_Transitions", PipeFittingCategoryId, TransitionParts),
                ManagerGroup(RoutingManagerGroup.Unions, "FM_Routing_Group_Unions", PipeFittingCategoryId, UnionParts),
                ManagerGroup(RoutingManagerGroup.MechanicalJoints, "FM_Routing_Group_Flanges", PipeFittingCategoryId, MechanicalJointParts),
                ManagerGroup(RoutingManagerGroup.Caps, "FM_Routing_Group_Caps", PipeFittingCategoryId, CapParts),
            ],
            DuctCurvesCategoryId =>
            [
                // No Segments row and no size criteria: duct size
                // availability is configured outside routing, and Revit's
                // duct routing UI rows are a plain part choice (owner
                // decision 2026-08-30).
                ManagerGroup(RoutingManagerGroup.Elbows, "FM_Routing_Group_Elbows", DuctFittingCategoryId, ElbowParts, hasCriteria: false),
                ManagerGroup(RoutingManagerGroup.Junctions, "FM_Routing_Group_Junctions", DuctFittingCategoryId, JunctionParts, hasCriteria: false),
                ManagerGroup(RoutingManagerGroup.Crosses, "FM_Routing_Group_Crosses", DuctFittingCategoryId, CrossParts, hasCriteria: false),
                ManagerGroup(RoutingManagerGroup.Transitions, "FM_Routing_Group_Transitions", DuctFittingCategoryId, TransitionParts, hasCriteria: false, excludeMultiShapeParts: true),
                ManagerGroup(RoutingManagerGroup.Unions, "FM_Routing_Group_Unions", DuctFittingCategoryId, UnionParts, hasCriteria: false),
                ManagerGroup(RoutingManagerGroup.TransitionsRectangularToRound, "FM_Routing_Group_TransitionRectToRound", DuctFittingCategoryId, TransitionParts, hasCriteria: false, requiredShapeMask: ShapeRectangular | ShapeRound),
                ManagerGroup(RoutingManagerGroup.TransitionsRectangularToOval, "FM_Routing_Group_TransitionRectToOval", DuctFittingCategoryId, TransitionParts, hasCriteria: false, requiredShapeMask: ShapeRectangular | ShapeOval),
                ManagerGroup(RoutingManagerGroup.TransitionsOvalToRound, "FM_Routing_Group_TransitionOvalToRound", DuctFittingCategoryId, TransitionParts, hasCriteria: false, requiredShapeMask: ShapeOval | ShapeRound),
                ManagerGroup(RoutingManagerGroup.Caps, "FM_Routing_Group_Caps", DuctFittingCategoryId, CapParts, hasCriteria: false),
            ],
            FlexPipeCurvesCategoryId =>
            [
                ParamGroup("RBS_CURVETYPE_DEFAULT_TEE_PARAM", "FM_Routing_Group_Junctions", PipeFittingCategoryId, TeeParts),
                ParamGroup("RBS_CURVETYPE_DEFAULT_TAKEOFF_PARAM", "FM_Routing_Group_Takeoff", PipeFittingCategoryId, TakeoffParts),
                ParamGroup("RBS_CURVETYPE_DEFAULT_TRANSITION_PARAM", "FM_Routing_Group_TransitionSingle", PipeFittingCategoryId, TransitionParts),
                ParamGroup("RBS_CURVETYPE_DEFAULT_UNION_PARAM", "FM_Routing_Group_Unions", PipeFittingCategoryId, UnionParts),
            ],
            FlexDuctCurvesCategoryId =>
            [
                ParamGroup("RBS_CURVETYPE_DEFAULT_TEE_PARAM", "FM_Routing_Group_Junctions", DuctFittingCategoryId, TeeParts),
                ParamGroup("RBS_CURVETYPE_DEFAULT_TAKEOFF_PARAM", "FM_Routing_Group_Takeoff", DuctFittingCategoryId, TakeoffParts),
                ParamGroup("RBS_CURVETYPE_DEFAULT_TRANSITION_PARAM", "FM_Routing_Group_TransitionSingle", DuctFittingCategoryId, TransitionParts, excludeMultiShapeParts: true),
                ParamGroup("RBS_CURVETYPE_MULTISHAPE_TRANSITION_PARAM", "FM_Routing_Param_RectToRound", DuctFittingCategoryId, TransitionParts, requiredShapeMask: ShapeRectangular | ShapeRound),
                ParamGroup("RBS_CURVETYPE_MULTISHAPE_TRANSITION_RECTOVAL_PARAM", "FM_Routing_Param_RectToOval", DuctFittingCategoryId, TransitionParts, requiredShapeMask: ShapeRectangular | ShapeOval),
                ParamGroup("RBS_CURVETYPE_MULTISHAPE_TRANSITION_OVALROUND_PARAM", "FM_Routing_Param_OvalToRound", DuctFittingCategoryId, TransitionParts, requiredShapeMask: ShapeOval | ShapeRound),
                ParamGroup("RBS_CURVETYPE_DEFAULT_UNION_PARAM", "FM_Routing_Group_Unions", DuctFittingCategoryId, UnionParts),
            ],
            ConduitCategoryId => BuildConduitGroups(withFittings),
            CableTrayCategoryId => BuildCableTrayGroups(withFittings),
            _ => [],
        };

    private static IReadOnlyList<RoutingGroupDescriptor> BuildConduitGroups(bool withFittings)
    {
        var groups = new List<RoutingGroupDescriptor>
        {
            ParamGroup("RBS_CURVETYPE_DEFAULT_BEND_PARAM", "FM_Routing_Group_Bend", ConduitFittingCategoryId, ElbowParts),
        };
        if (withFittings)
        {
            groups.Add(ParamGroup("RBS_CURVETYPE_DEFAULT_TEE_PARAM", "FM_Routing_Group_Junctions", ConduitFittingCategoryId, TeeParts));
            groups.Add(ParamGroup("RBS_CURVETYPE_DEFAULT_CROSS_PARAM", "FM_Routing_Group_CrossesElectrical", ConduitFittingCategoryId, CrossParts));
        }
        groups.Add(ParamGroup("RBS_CURVETYPE_DEFAULT_TRANSITION_PARAM", "FM_Routing_Group_TransitionSingle", ConduitFittingCategoryId, TransitionParts));
        groups.Add(ParamGroup("RBS_CURVETYPE_DEFAULT_UNION_PARAM", "FM_Routing_Group_Unions", ConduitFittingCategoryId, UnionParts));
        return groups;
    }

    private static IReadOnlyList<RoutingGroupDescriptor> BuildCableTrayGroups(bool withFittings)
    {
        var groups = new List<RoutingGroupDescriptor>
        {
            ParamGroup("RBS_CURVETYPE_DEFAULT_HORIZONTAL_BEND_PARAM", "FM_Routing_Group_HorizontalBend", CableTrayFittingCategoryId, TrayBendParts),
            ParamGroup("RBS_CURVETYPE_DEFAULT_ELBOWUP_PARAM", "FM_Routing_Group_VerticalBendOuter", CableTrayFittingCategoryId, TrayVerticalBendParts),
            ParamGroup("RBS_CURVETYPE_DEFAULT_ELBOWDOWN_PARAM", "FM_Routing_Group_VerticalBendInner", CableTrayFittingCategoryId, TrayVerticalBendParts),
        };
        if (withFittings)
        {
            groups.Add(ParamGroup("RBS_CURVETYPE_DEFAULT_TEE_PARAM", "FM_Routing_Group_Junctions", CableTrayFittingCategoryId, TrayTeeParts));
            groups.Add(ParamGroup("RBS_CURVETYPE_DEFAULT_CROSS_PARAM", "FM_Routing_Group_CrossesElectrical", CableTrayFittingCategoryId, TrayCrossParts));
        }
        groups.Add(ParamGroup("RBS_CURVETYPE_DEFAULT_TRANSITION_PARAM", "FM_Routing_Group_TransitionSingle", CableTrayFittingCategoryId, TrayTransitionParts));
        groups.Add(ParamGroup("RBS_CURVETYPE_DEFAULT_UNION_PARAM", "FM_Routing_Group_Unions", CableTrayFittingCategoryId, TrayUnionParts));
        return groups;
    }

    private static RoutingGroupDescriptor ManagerGroup(
        RoutingManagerGroup group,
        string labelKey,
        int fittingCategoryId = 0,
        IReadOnlyList<int>? partTypes = null,
        bool isReadOnly = false,
        bool hasCriteria = true,
        int requiredShapeMask = 0,
        bool excludeMultiShapeParts = false)
        => new(
            RoutingGroupKeys.ForManagerGroup((int)group),
            (int)group,
            labelKey,
            isReadOnly,
            AllowMultipleRules: !isReadOnly,
            HasCriteria: hasCriteria && !isReadOnly,
            fittingCategoryId,
            partTypes ?? [],
            RequiredConnectorShapeMask: requiredShapeMask,
            ExcludeMultiShapeParts: excludeMultiShapeParts);

    private static RoutingGroupDescriptor ParamGroup(
        string builtInParameterName,
        string labelKey,
        int fittingCategoryId,
        IReadOnlyList<int> partTypes,
        int requiredShapeMask = 0,
        bool excludeMultiShapeParts = false)
        => new(
            RoutingGroupKeys.ForParam(builtInParameterName),
            null,
            labelKey,
            IsReadOnly: false,
            AllowMultipleRules: false,
            HasCriteria: false,
            fittingCategoryId,
            partTypes,
            RequiredConnectorShapeMask: requiredShapeMask,
            ExcludeMultiShapeParts: excludeMultiShapeParts);

    /// <summary>The pipe Segments row: display-only view of the active
    /// version's per-version segment configuration (FHV21).</summary>
    private static RoutingGroupDescriptor SegmentRow(RoutingManagerGroup group, string labelKey)
        => new(
            RoutingGroupKeys.ForManagerGroup((int)group),
            (int)group,
            labelKey,
            IsReadOnly: true,
            AllowMultipleRules: false,
            HasCriteria: false,
            FittingCategoryId: 0,
            PartTypeOrdinals: [],
            IsSegmentRow: true);
}

/// <summary>
/// Frozen <c>RoutingPreferenceRuleGroupType</c> ordinals (probe-verified
/// 2025 — the same map <see cref="RoutingGroupKeys"/> pins to storage keys).
/// </summary>
public enum RoutingManagerGroup
{
    Segments = 0,
    Elbows = 1,
    Junctions = 2,
    Crosses = 3,
    Transitions = 4,
    Unions = 5,
    MechanicalJoints = 6,
    TransitionsRectangularToRound = 7,
    TransitionsRectangularToOval = 8,
    TransitionsOvalToRound = 9,
    Caps = 10,
}

/// <summary>One routing editor group (a Revit routing-UI row).</summary>
/// <param name="GroupKey">Storage group key (<see cref="RoutingGroupKeys"/>).</param>
/// <param name="ManagerGroupType">Manager ordinal or <c>null</c> for param groups.</param>
/// <param name="LabelKey">Localization key of the group label.</param>
/// <param name="IsReadOnly">Display-only row (no editable cells at all).</param>
/// <param name="AllowMultipleRules">Manager fitting groups; param groups hold exactly one value.</param>
/// <param name="HasCriteria">Manager fitting groups carry min/max size criteria.</param>
/// <param name="FittingCategoryId">Revit category of the part candidates (0 = no picker).</param>
/// <param name="PartTypeOrdinals"><c>part_type</c> filter of the candidates (empty = no filter).</param>
    /// <param name="IsSegmentRow">The pipe Segments row (FHV21, owner
    /// decision 2026-09-01): a READ-ONLY view of the active version's
    /// per-version segment configuration — the mini-project owns the
    /// segment set, ranges and order (versioned content). The marker lets
    /// readers source this group's data from the per-version store instead
    /// of the item-level routing channel.</param>
    /// <param name="RequiredConnectorShapeMask">Multi-shape transition
    /// groups (owner stress test 2026-09-01): a candidate must carry ALL
    /// these <c>connector_shape</c> bits (a rect-to-round row never offers
    /// a purely rectangular transition). 0 = no requirement.</param>
    /// <param name="ExcludeMultiShapeParts">The plain Transitions rows
    /// (owner stress test 2026-09-01): multi-shape transitions live in
    /// their OWN rows, so the plain row offers single-shape parts only
    /// (a round-to-rect transition never appears under «Переход»).</param>
public sealed record RoutingGroupDescriptor(
    string GroupKey,
    int? ManagerGroupType,
    string LabelKey,
    bool IsReadOnly,
    bool AllowMultipleRules,
    bool HasCriteria,
    int FittingCategoryId,
    IReadOnlyList<int> PartTypeOrdinals,
    bool IsSegmentRow = false,
    int RequiredConnectorShapeMask = 0,
    bool ExcludeMultiShapeParts = false);
