# Jeremy Tammik Recommendations

Jeremy Tammik is the Autodesk Revit API expert and primary community
voice. His blog "The Building Coder" (thebuildingcoder.typepad.com) is the
canonical source for Revit plugin best practices. Key recommendations for
testing:

## Storage: ElementId, not Element

> *"It's also best not to store Revit elements directly, but instead only
> their ElementIds or UniqueIds. UniqueIds are strings, which are value
> objects. ElementId is a class but is serializable. Both can safely be used
> between threads."*
> — [The Building Coder #2024](https://jeremytammik.github.io/tbc/a/2024_context.html)

**Implication for tests:** mock SUTs should expose `ElementId` (or
`string` for `UniqueId`), never `Element` directly. If SUT stores `Element`,
you can't create a real one in tests without Revit.

## Revit API is single-threaded

> *"The official statement is and always has been that accessing the Revit
> API from other than the main thread is not allowed and not recommended."*
> — Arnošt Löbel (Revit team), [The Building Coder #1244](https://jeremytammik.github.io/tbc/a/1244_no_multithreading.htm)

**Implication for tests:**
- xUnit default: parallel between test classes. If you have integration tests
  that share Revit state, use
  `[assembly: CollectionBehavior(DisableTestParallelization = true)]`
- Avoid `Task.Run(() => RevitApiCall())` in tests — that's a production
  pattern, not a test pattern

## Speckle xUnitRevit is "the best available test framework for Revit"

> *"speckle's Revit unit test is the best available test framework for Revit
> that we've found. You can run test functions using the speckle UI, and you
> don't have to test a full external command – you can test individual methods."*
> *"Note that you still have to open Revit to use it, as I haven't seen a Revit
> Test Framework that can get around that — as far as I know, Revit has to be
> open to have access to its API, which is required to test anything that
> uses and Revit API calls."*
> — Tobias Flöscher (Geberit), [The Building Coder #1971](https://jeremytammik.github.io/tbc/a/1971_unit_test.html)

**Implication:** for true integration tests, you DO need Revit running. The
goal of "pure unit testing" for Revit is limited to pure-logic classes (which
is what we focus on in SmartCon). For everything else, use integration tests
(see [integration-testing.md](integration-testing.md)).

## Code refactor for testability

> *"I refactored the code so that it was as much as possible independent
> from the Revit API. This code was testable as I knew it, using tests run on
> continuous integration. But a lot of code remained in the project
> referencing the Revit API."*
> — Tobias Flöscher (Geberit), [The Building Coder #2067](https://jeremytammik.github.io/tbc/a/2067_revittest.html)

**Implication:** the SmartCon pattern of `IRevitContext` / `IRevitContextWriter`
/ `IFamilyManagerAwaitableEvent` / `IFamilyFinder` / `IFamilyVersionStore` etc.
is the **right** approach. Push more Revit-touching code into adapters and
keep business logic in pure C# classes that take interfaces.

## ExternalEvent patterns

> *"Wrap your handler in a try/catch and log the exception. If Execute throws
> before you change anything, you'll see IsPending stay true and nothing
> happens."*
> — Autodesk Community + multiple Jeremy Tammik posts

**Implication for tests:** the production code's `FamilyManagerAwaitableEvent`
follows this pattern (try/catch around every `ProcessQueue` action, log
exception, complete task with `TrySetException`). The
`FamilyManagerAwaitableEventTests.cs` tests verify the exception propagation.

## Revit's external event IsPending quirk

> *"In 2026 this is stricter — the event can remain pending until Revit fully
> returns to idle."*
> — Autodesk Community: ExternalEventHandler not raise in 2026

**Implication for integration tests:** in Revit 2026, `ExternalEvent.Raise()`
may be delayed if Revit is in modal state. Add a wait/retry pattern in
integration tests, or call from a known-idle state.

## GetRevitVersion via IRevitContext

> From SmartCon invariant I-09: *"Access to the current Document. Do not
> cache between operations. UIDocument is not exposed in Core."*

**Implication for tests:** `IRevitContext.GetRevitVersion()` returns a string
(e.g. "2025"). Tests should mock this explicitly (NOT mock the whole
`IRevitContext` — see [revit-mocking.md](revit-mocking.md) for why).

## Summary: Tammik's testing philosophy

1. **Pure logic in pure C# classes** (no Revit refs) — fully unit-testable
2. **Wrappers/adapters** around Revit types — mock the wrapper interface
3. **Integration tests for the wrapper itself** — use Speckle/ricaun pattern
4. **Production log validation** for edge cases integration tests can't reach

SmartCon follows this exactly: see `SmartCon.Core` (pure) vs
`SmartCon.Revit` (adapters) vs the `IFamilyManagerAwaitableEvent` test seam.
