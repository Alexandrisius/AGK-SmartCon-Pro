using System.Text.Json;
using System.Text.Json.Serialization;
using Autodesk.Revit.DB;
using RevitFamilyManager = Autodesk.Revit.DB.FamilyManager;

namespace SmartCon.Revit.Agent.Tools.Families;

/// <summary>One reversible family operation recorded in a change set.</summary>
internal sealed class FamilyUndoOperation
{
    /// <summary>add | rename | value | formula</summary>
    public string? Kind { get; set; }

    public string? Parameter { get; set; }

    public string? Scope { get; set; }

    /// <summary>Old name for a rename.</summary>
    public string? NewName { get; set; }

    public string? TypeName { get; set; }

    /// <summary>double | integer | string | elementId</summary>
    public string? Storage { get; set; }

    public string? Value { get; set; }

    public string? Formula { get; set; }
}

/// <summary>
/// JSON payload stored in the family change item (OldValue). Ops are reverted in
/// reverse order inside one family-document transaction.
/// </summary>
internal sealed class FamilyUndoPlan
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string? FamilyName { get; set; }

    public string? BackupPath { get; set; }

    public List<FamilyUndoOperation> Ops { get; set; } = new();

    public string Serialize() => JsonSerializer.Serialize(this, SerializerOptions);

    public static FamilyUndoPlan? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<FamilyUndoPlan>(json!, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Helpers shared by the family tools and undo_last.</summary>
internal static class AgentFamilyUndo
{
    internal static FamilyParameter? FindParameter(
        RevitFamilyManager manager,
        string? name,
        string? scope)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        bool? instance = null;
        if (string.Equals(scope, "instance", StringComparison.OrdinalIgnoreCase))
            instance = true;
        else if (string.Equals(scope, "type", StringComparison.OrdinalIgnoreCase))
            instance = false;

        foreach (var parameter in manager.GetParameters())
        {
            if (!string.Equals(parameter.Definition.Name, name, StringComparison.OrdinalIgnoreCase))
                continue;
            if (instance.HasValue && parameter.IsInstance != instance.Value)
                continue;
            return parameter;
        }

        return null;
    }

    internal static FamilyType? FindType(RevitFamilyManager manager, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return manager.CurrentType;

        foreach (FamilyType type in manager.Types)
        {
            if (string.Equals(type.Name, name, StringComparison.OrdinalIgnoreCase))
                return type;
        }

        return null;
    }

    internal static bool SetRaw(RevitFamilyManager manager, FamilyParameter parameter, string? storage, string? raw)
    {
        switch (storage)
        {
            case "double":
                if (!double.TryParse(
                        raw,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var number))
                {
                    return false;
                }

                manager.Set(parameter, number);
                return true;
            case "integer":
                if (!int.TryParse(
                        raw,
                        System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var integer))
                {
                    return false;
                }

                manager.Set(parameter, integer);
                return true;
            case "string":
                manager.Set(parameter, raw ?? string.Empty);
                return true;
            case "elementId":
                if (!long.TryParse(
                        raw,
                        System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var id))
                {
                    return false;
                }

                manager.Set(
                    parameter,
                    SmartCon.Core.Compatibility.ElementIdCompat.Create(id));
                return true;
            default:
                return false;
        }
    }
}