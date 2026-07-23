namespace SmartCon.FamilyManager.ViewModels;

/// <summary>
/// One resolved fact row of the properties window header (ADR-055) —
/// e.g. «Тип детали: Отвод». Built by
/// <see cref="FamilyPropertiesViewModel"/> from
/// <see cref="SmartCon.Core.Models.FamilyManager.FamilyFactRuleSet"/>
/// rules matched to the item's Revit category; both strings are already
/// localized for the current UI language.
/// </summary>
public sealed record FamilyFactDisplayRow(string Label, string Value);
