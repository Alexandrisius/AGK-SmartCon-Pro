using System.IO;
using System.Linq;
using SmartCon.Core.Logging;
using SmartCon.Core.Models;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Core.Services.Implementation;

/// <summary>
/// Pure-C# implementation of <see cref="IProjectBaseBindingEvaluator"/>. It
/// delegates to the shared <see cref="IFileNameParser"/> engine — the exact
/// same instance ProjectManagement uses for the ShareProject export pipeline
/// — so that semantic of block parsing, validation, field constraints stays
/// consistent across modules without any FamilyManager → ProjectManagement
/// reference (ADR-045, see #119).
/// </summary>
public sealed class ProjectBaseBindingEvaluator : IProjectBaseBindingEvaluator
{
    private readonly IFileNameParser _parser;

    public ProjectBaseBindingEvaluator(IFileNameParser parser)
    {
        _parser = parser;
    }

    public ProjectBaseMatch Evaluate(ProjectBaseBinding? binding, string? filePath)
    {
        using var _scope = SmartConLogger.BeginScope("ProjectBaseBindingEvaluator",
            ("Method", nameof(Evaluate)),
            ("FilePath", Path.GetFileName(filePath ?? string.Empty) ?? "(none)"));

        if (binding is null)
        {
            SmartConLogger.Debug("ProjectBaseBindingEvaluator: binding is null -> NotApplicable");
            return new ProjectBaseMatch(ProjectBaseMatchKind.NotApplicable);
        }
        if (string.IsNullOrEmpty(filePath))
        {
            SmartConLogger.Debug("ProjectBaseBindingEvaluator: filePath is empty -> NotApplicable");
            return new ProjectBaseMatch(ProjectBaseMatchKind.NotApplicable);
        }
        if (binding.Template is null || binding.Template.Blocks is null || binding.Template.Blocks.Count == 0)
        {
            SmartConLogger.Debug($"ProjectBaseBindingEvaluator: template empty (blocks={binding.Template?.Blocks?.Count ?? 0}) -> NotApplicable");
            return new ProjectBaseMatch(ProjectBaseMatchKind.NotApplicable);
        }

        var parsed = _parser.ParseBlocks(filePath!, binding.Template);
        var fieldLibrary = binding.FieldLibrary as List<FieldDefinition> ?? binding.FieldLibrary?.ToList() ?? new List<FieldDefinition>();
        var validation = _parser.ValidateDetailed(filePath!, binding.Template, fieldLibrary);

        if (validation.IsValid)
        {
            SmartConLogger.Debug($"ProjectBaseBindingEvaluator: validation passed, parsed {parsed.Count} values -> Match");
            return new ProjectBaseMatch(ProjectBaseMatchKind.Match, Reason: null, ParsedValues: parsed);
        }

        SmartConLogger.Debug($"ProjectBaseBindingEvaluator: validation failed: {validation.Summary} -> Mismatch");
        return new ProjectBaseMatch(ProjectBaseMatchKind.Mismatch, Reason: validation.Summary, ParsedValues: parsed);
    }
}
