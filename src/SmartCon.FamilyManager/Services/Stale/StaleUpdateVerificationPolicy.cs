namespace SmartCon.FamilyManager.Services.Stale;

/// <summary>
/// #209 round-3: arbitration policy for the tri-state content
/// verification (see <c>StaleFamilyUpdater
/// .VerifyEmbeddedMatchesResolvedFileAsync</c>). Isolated as a pure
/// function because the verification's <c>true</c> has TWO incompatible
/// meanings at different call sites, and treating "not applicable" as
/// "verified" caused project-context regressions (validator findings
/// Critical #1/#2): only an explicit match may skip or arbitrate a
/// reload, only an explicit mismatch fails a successful one.
/// </summary>
internal static class StaleUpdateVerificationPolicy
{
    /// <summary>
    /// Pre-verify: skip the reload entirely (content already matches the
    /// catalog target — retry-after-partial-batch case). Only an EXPLICIT
    /// match; <c>null</c> (project context / unreadable / no stored hash)
    /// must always run the reload.
    /// </summary>
    public static bool ShouldSkipReload(bool? preVerified) => preVerified == true;

    /// <summary>
    /// The reload reported failure: accept it as "already up-to-date"
    /// only when the content verification EXPLICITLY matches (family-doc
    /// "unchanged → LoadFamily false" case). <c>null</c> keeps the
    /// failure — a rejected reload must never be masked as success.
    /// </summary>
    public static bool ShouldAcceptFailedReload(bool? postVerified) => postVerified == true;

    /// <summary>
    /// The reload reported success: fail the update only on an EXPLICIT
    /// mismatch (the silent no-op reload the verification exists for).
    /// <c>null</c> (project context, unreadable hash) trusts the reload.
    /// </summary>
    public static bool ShouldFailSuccessfulReload(bool? postVerified) => postVerified == false;
}
