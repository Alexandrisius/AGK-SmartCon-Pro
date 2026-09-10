using Autodesk.Revit.DB;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Source-aware extension of <see cref="IFamilyLoadService"/> for the
/// family-document (nested family) reload path (#209). Lets the caller hand
/// over an ALREADY-OPEN source document so one update cycle (pre-verify →
/// poke → doc-to-doc merge → post-verify) shares a single
/// <c>OpenDocumentFile</c> per family instead of opening the same resolved
/// file 2-3 times. Implemented by the Revit-boundary load service; callers
/// detect it via an <c>is</c> check and fall back to
/// <see cref="IFamilyLoadService.ReloadFamilyPreservingLoadedTypesAsync"/>
/// when it is absent (mocks, alternate implementations).
/// Must only be called on the Revit main thread (inside an ExternalEvent
/// callback, I-01) — the method is synchronous by design.
/// </summary>
public interface IFamilyLoadServiceSourceAware : IFamilyLoadService
{
    /// <summary>
    /// Reloads a family definition that is nested inside the active family
    /// document (poke + doc-to-doc merge, path-load fallback). The host
    /// document is resolved via <c>IRevitContext</c> internally (same rule
    /// as <see cref="IFamilyLoadService"/> — no host Document parameter).
    /// </summary>
    /// <param name="normalizedPath">Absolute normalized path to the resolved
    /// source .rfa (the catalog version file).</param>
    /// <param name="familyName">Nested family name (= file base name).</param>
    /// <param name="overwriteParameterValues">See
    /// <see cref="IFamilyLoadService.ReloadFamilyPreservingLoadedTypesAsync"/>.</param>
    /// <param name="preOpenedSourceDocProvider">
    /// Optional provider of an ALREADY-OPEN source document for
    /// <paramref name="normalizedPath"/>. BORROWED: ownership stays with the
    /// caller, the service never closes it. When the provider is null or
    /// returns null, the service opens (and closes) the file itself. When a
    /// document is provided, the "source file open in the editor" guard is
    /// skipped — the caller is responsible for performing that check BEFORE
    /// opening the file.
    /// </param>
    FamilyLoadResult ReloadNestedInFamilyDocument(
        string normalizedPath,
        string familyName,
        bool overwriteParameterValues,
        Func<Document?>? preOpenedSourceDocProvider = null);
}
