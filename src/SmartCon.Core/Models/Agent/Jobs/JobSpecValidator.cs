namespace SmartCon.Core.Models.Agent.Jobs;

/// <summary>
/// Pure validation of <see cref="JobSpec"/> before any Revit work happens, so
/// malformed jobs never reach the pump.
/// </summary>
public static class JobSpecValidator
{
    public static JobError? Validate(JobSpec? spec)
    {
        if (spec is null)
        {
            return JobErrors.Spec("spec is null");
        }

        if (spec.SchemaVersion != JobSpec.CurrentSchemaVersion)
        {
            return JobErrors.Spec(
                "unsupported schemaVersion " + spec.SchemaVersion +
                " (supported: " + JobSpec.CurrentSchemaVersion + ")");
        }

        if (string.IsNullOrWhiteSpace(spec.DocumentId))
        {
            return JobErrors.Spec("documentId is required");
        }

        if (string.IsNullOrWhiteSpace(spec.Action))
        {
            return JobErrors.Spec("action is required");
        }

        if (spec.Options.BatchSize <= 0 || spec.Options.BatchSize > 500)
        {
            return JobErrors.Spec("options.batchSize must be 1..500");
        }

        if (spec.Options.SliceBudgetMs < 200 || spec.Options.SliceBudgetMs > 5000)
        {
            return JobErrors.Spec("options.sliceBudgetMs must be 200..5000");
        }

        if (spec.Options.MaxItems <= 0 || spec.Options.MaxItems > 10000)
        {
            return JobErrors.Spec("options.maxItems must be 1..10000");
        }

        if (spec.Options.SaveEveryMinutes <= 0)
        {
            return JobErrors.Spec("options.saveEveryMinutes must be positive");
        }

        if (spec.OptionsOverride is { } overrides)
        {
            if (overrides.BatchSize is { } batchSize && (batchSize <= 0 || batchSize > 500))
            {
                return JobErrors.Spec("options override batchSize must be 1..500");
            }

            if (overrides.MaxItems is { } maxItems && (maxItems <= 0 || maxItems > 10000))
            {
                return JobErrors.Spec("options override maxItems must be 1..10000");
            }
        }

        switch (spec.Mode)
        {
            case JobMode.Preview:
                if (spec.PreviewDigest is not null)
                {
                    return JobErrors.Spec("mode=preview must not carry previewDigest");
                }

                break;

            case JobMode.Apply:
                if (string.IsNullOrWhiteSpace(spec.PreviewDigest))
                {
                    return JobErrors.Spec(
                        "mode=apply requires previewDigest from a completed preview");
                }

                break;

            case JobMode.Resume:
                if (spec.ResumeJobId is null || spec.ResumeJobId == Guid.Empty)
                {
                    return JobErrors.Spec("mode=resume requires resumeJobId");
                }

                break;

            default:
                return JobErrors.Spec("unsupported mode " + spec.Mode);
        }

        return null;
    }

    /// <summary>Validates a preview at apply time: digest, document, count and expiry.</summary>
    public static JobError? ValidateApplyEnvelope(JobPreview preview, JobSpec spec, DateTime utcNow)
    {
        if (preview.Digest != spec.PreviewDigest)
        {
            return JobErrors.PreviewMismatch();
        }

        if (!string.Equals(preview.DocumentId, spec.DocumentId, StringComparison.Ordinal))
        {
            return JobErrors.DocumentMismatch(preview.DocumentId, spec.DocumentId);
        }

        if (!string.Equals(preview.Action, spec.Action, StringComparison.Ordinal))
        {
            return JobErrors.PreviewMismatch();
        }

        if (preview.IsExpired(utcNow))
        {
            return JobErrors.PreviewExpired(preview.ExpiresAtUtc);
        }

        return null;
    }
}