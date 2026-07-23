namespace SmartCon.Core.Threading;

/// <summary>
/// Bridge for executing <see cref="Task"/> continuations on a thread-pool
/// thread and synchronously waiting for the result. Used to avoid
/// UI-thread deadlocks when calling true async I/O APIs from sync
/// contexts (Revit <c>IExternalCommand.Execute</c>, event handlers,
/// dispatchers).
/// </summary>
/// <remarks>
/// <para><b>USE ONLY FOR TRUE ASYNC I/O</b> (SQLite, file I/O, HTTP,
/// JSON deserialization) — i.e. async methods that contain a real
/// <c>await</c> inside and do NOT call Revit API.</para>
///
/// <para><b>NEVER USE FOR REVIT API.</b> Wrapping Revit API in
/// <c>Task.Run()</c> causes <c>"Failed to register a managed object"</c>
/// or <c>"The calling thread cannot access this object because a
/// different thread owns it"</c> — Revit API is single-threaded
/// (main UI thread only). See
/// <c>revit-api-best-practice/references/async-threading-patterns.md</c>,
/// "Mistake 1: Wrapping Revit API in Task.Run".</para>
///
/// <para><b>SAFE/DANGEROUS table for production callers (as of
/// feature/logging-improvements branch):</b></para>
/// <list type="table">
///   <item><term>✅ SAFE</term><description><c>IFamilyFileResolver.ResolveForLoadAsync</c> — pure SQLite I/O via <c>Microsoft.Data.Sqlite</c> with real <c>await</c>.</description></item>
///   <item><term>✅ SAFE</term><description><c>IFamilyCatalogProvider.FindByNormalizedNameAsync</c> — pure SQLite query, no Revit API.</description></item>
///   <item><term>✅ SAFE</term><description><c>IUpdateService.GetPendingUpdateAsync</c> — pure file I/O (<c>File.ReadAllTextAsync</c> + <c>JsonSerializer.Deserialize</c>).</description></item>
///   <item><term>❌ DANGEROUS</term><description><c>IFamilyLoadService.LoadFamilyAsync</c> — internally calls <c>doc.LoadFamily</c> and <c>_transactionService.RunInTransaction</c>. <b>Use</b> <c>.GetAwaiter().GetResult()</c> directly (no Task.Run).</description></item>
///   <item><term>❌ DANGEROUS</term><description><c>IFamilyLoadService.LoadFamilySymbolAsync</c> — internally calls <c>doc.LoadFamilySymbol</c>. <b>Use</b> <c>.GetAwaiter().GetResult()</c> directly (no Task.Run).</description></item>
/// </list>
///
/// <para><b>For sync-blocking callers of IFamilyLoadService:</b>
/// <c>IDropHandler.Execute</c> and the Stale Update ExternalEvent callback
/// call <c>LoadFamilyAsync</c>/<c>LoadFamilySymbolAsync</c>/
/// <c>ReloadFamilyPreservingLoadedTypesAsync</c> via
/// <c>.GetAwaiter().GetResult()</c> directly (never via AsyncBridge — these
/// methods call Revit API internally). This is only safe when every await
/// inside completes synchronously: today the single SQLite await
/// (<c>ISharedNestedFamilyRepository.GetNamesForCurrentVersionAsync</c>)
/// completes synchronously because Microsoft.Data.Sqlite has no true async
/// I/O (MS docs "Async limitations"). To not rely on that contract,
/// sync-blocking callers MUST pre-resolve nested names (via
/// <c>AsyncBridge.RunSync</c> on the repository — pure SQLite, SAFE) and
/// pass them through the <c>nestedSharedNames</c> parameter, which removes
/// the async gap entirely. See FamilyPlacementDropHandler and
/// StaleFamilyUpdater for the reference pattern.</para>
///
/// <para><b>How it works:</b></para>
/// <code>
/// // UI thread (Revit main thread) — sync context is DispatcherSynchronizationContext
/// var data = Task.Run(() => _fileResolver.ResolveAsync(...))  // ThreadPool, no sync context
///              .GetAwaiter().GetResult();                       // blocks UI thread briefly
/// // Inside ResolveAsync, await captures null sync context → continuation on ThreadPool
/// // → no deadlock
/// </code>
///
/// <para><b>Use sparingly.</b> The proper long-term fix is to make
/// <c>IExternalCommand.Execute</c> callers <c>async</c> themselves.
/// This helper is a stop-gap for true async I/O calls that would
/// otherwise deadlock when called from sync contexts on the UI thread.</para>
/// </remarks>
public static class AsyncBridge
{
    /// <summary>
    /// Execute <paramref name="taskFactory"/> on a thread-pool thread and
    /// synchronously block the current thread until the task completes.
    /// Returns the task's result. Does not marshal exceptions: any
    /// exception thrown inside the task is re-thrown on the calling
    /// thread (wrapped in <see cref="AggregateException"/> by the
    /// <c>GetAwaiter().GetResult()</c> semantics — this is why we
    /// unwrap to the original exception type in the body).
    /// </summary>
    public static T RunSync<T>(Func<Task<T>> taskFactory)
    {
#if NET
        ArgumentNullException.ThrowIfNull(taskFactory);
#else
        if (taskFactory is null) throw new ArgumentNullException(nameof(taskFactory));
#endif
        return Task.Run(taskFactory).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Execute <paramref name="taskFactory"/> on a thread-pool thread and
    /// synchronously block the current thread until the task completes.
    /// </summary>
    public static void RunSync(Func<Task> taskFactory)
    {
#if NET
        ArgumentNullException.ThrowIfNull(taskFactory);
#else
        if (taskFactory is null) throw new ArgumentNullException(nameof(taskFactory));
#endif
        Task.Run(taskFactory).GetAwaiter().GetResult();
    }
}
