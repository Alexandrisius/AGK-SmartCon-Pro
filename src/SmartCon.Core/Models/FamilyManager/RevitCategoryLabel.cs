namespace SmartCon.Core.Models.FamilyManager;

/// <summary>One pickable Revit model category: the
/// <c>BuiltInCategory</c> ordinal (locale-invariant storage value) plus
/// its localized display label.</summary>
/// <param name="Ordinal"><c>BuiltInCategory</c> ordinal
/// (e.g. -2008049 for pipe fittings).</param>
/// <param name="Label">Localized display name
/// (<c>LabelUtils.GetLabelFor</c> in the Revit session language).</param>
public sealed record RevitCategoryLabel(int Ordinal, string Label);
