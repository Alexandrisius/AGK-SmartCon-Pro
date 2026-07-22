using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Parses a Revit shared parameter file (ФОП, .txt) without Revit API.
/// The file is tab-delimited with *META / *GROUP / *PARAM sections.
/// </summary>
public interface ISharedParameterFileParser
{
    /// <summary>
    /// Reads and parses the file at <paramref name="filePath"/>.
    /// Throws <see cref="InvalidDataException"/> when the file does not look
    /// like a shared parameter file (no *PARAM section at all).
    /// </summary>
    IReadOnlyList<SharedParameterEntry> ParseFile(string filePath);

    /// <summary>
    /// Parses shared parameter file content already loaded into a string.
    /// </summary>
    IReadOnlyList<SharedParameterEntry> ParseContent(string content);
}
