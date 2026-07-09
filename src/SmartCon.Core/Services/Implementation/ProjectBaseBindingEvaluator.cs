using System.Linq;
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

    public ProjectBaseMatch Evaluate(ProjectBaseBinding? binding, string filePath)
    {
        if (binding is null)
            return new ProjectBaseMatch(ProjectBaseMatchKind.NotApplicable);
        if (string.IsNullOrEmpty(filePath))
            return new ProjectBaseMatch(ProjectBaseMatchKind.NotApplicable);
        if (binding.Template is null || binding.Template.Blocks is null || binding.Template.Blocks.Count == 0)
            return new ProjectBaseMatch(ProjectBaseMatchKind.NotApplicable);

        var parsed = _parser.ParseBlocks(filePath, binding.Template);
        var fieldLibrary = binding.FieldLibrary as List<FieldDefinition> ?? binding.FieldLibrary?.ToList() ?? new List<FieldDefinition>();
        var validation = _parser.ValidateDetailed(filePath, binding.Template, fieldLibrary);

        if (validation.IsValid)
            return new ProjectBaseMatch(ProjectBaseMatchKind.Match, Reason: null, ParsedValues: parsed);

        return new ProjectBaseMatch(ProjectBaseMatchKind.Mismatch, Reason: validation.Summary, ParsedValues: parsed);
    }
}