using Xunit;

namespace SmartCon.Tests;

/// <summary>
/// #214: tests that mutate or assert the static
/// <see cref="SmartCon.Core.Services.LocalizationService"/> language state.
/// <c>LocalizationService.SetLanguage</c> is process-global; with
/// <c>parallelizeTestCollections=true</c> an EN-mutating test raced
/// RU-asserting tests (observed flake:
/// <c>FamilyPropertiesViewModelTests.LoadFactsAsync_UndefinedPartType_ShowsUndefinedLabel</c>).
/// All such classes join this collection to serialize against each other.
/// </summary>
[CollectionDefinition("Localization", DisableParallelization = true)]
public sealed class LocalizationCollection;
