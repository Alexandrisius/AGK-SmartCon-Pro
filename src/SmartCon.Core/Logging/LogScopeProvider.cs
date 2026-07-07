namespace SmartCon.Core.Logging;

/// <summary>
/// Ambient storage for the currently active logging scopes. Backed by
/// <see cref="AsyncLocal{T}"/> so that the scope chain flows through async
/// awaits and thread-pool hops without losing context.
///
/// Why AsyncLocal and not ThreadStatic:
/// - SmartCon code awaits across threads (e.g. await Task.Run inside
///   <c>IExternalEventHandler.Execute</c>, await Task.Yield in FireAndForget).
/// - <c>[ThreadStatic]</c> silently breaks correlation in those paths:
///   the scope is visible on thread A, the continuation runs on thread B,
///   the inner log line has no <c>[OpId=...]</c> prefix.
/// - <c>AsyncLocal&lt;T&gt;</c> copies the value along the
///   <see cref="System.Threading.ExecutionContext"/>, so awaits preserve it.
/// </summary>
internal static class LogScopeProvider
{
    /// <summary>
    /// Singly-linked immutable stack node. We avoid
    /// <c>System.Collections.Immutable.ImmutableStack&lt;T&gt;</c> because
    /// it is not part of the net48 BCL and adding a NuGet dependency
    /// (I-09) for a 4-line linked list is overkill.
    /// </summary>
    private sealed class ScopeNode
    {
        public ScopeNode(LogScope scope, ScopeNode? next)
        {
            Scope = scope;
            Next = next;
        }

        public LogScope Scope { get; }
        public ScopeNode? Next { get; }
    }

    private static readonly AsyncLocal<ScopeNode?> _top = new();

    /// <summary>
    /// Currently active scope (innermost), or <c>null</c> when no scope is open.
    /// </summary>
    public static LogScope? Current => _top.Value?.Scope;

    /// <summary>
    /// Push a new scope onto the ambient stack. The returned disposable
    /// restores the previous stack on <see cref="IDisposable.Dispose"/>.
    /// </summary>
    public static IDisposable Push(LogScope scope)
    {
        var newTop = new ScopeNode(scope, _top.Value);
        _top.Value = newTop;
        return new PopOnDispose(scope, newTop);
    }

    /// <summary>
    /// Pop the innermost scope from the ambient stack. Used by
    /// <see cref="MeasureScope.Dispose"/> which manages its own
    /// end-of-life footer line and so does not need the disposable wrapper
    /// returned by <see cref="Push"/>.
    /// </summary>
    internal static void Pop()
    {
        _top.Value = _top.Value?.Next;
    }

    /// <summary>
    /// Snapshot of the current scope chain from outermost to innermost.
    /// Used by writers to render the full <c>[OpId=…]</c> / property prefix.
    /// </summary>
    public static IEnumerable<LogScope> EnumerateFromRoot()
    {
        // Collect into a stack, then pop to reverse order.
        var frames = new System.Collections.Generic.Stack<LogScope>();
        for (var n = _top.Value; n is not null; n = n.Next)
        {
            frames.Push(n.Scope);
        }
        while (frames.Count > 0) yield return frames.Pop();
    }

    private sealed class PopOnDispose : IDisposable
    {
        private readonly LogScope _scope;
        private readonly ScopeNode _node;
        private bool _disposed;

        public PopOnDispose(LogScope scope, ScopeNode node)
        {
            _scope = scope;
            _node = node;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Only pop if this scope is still the top. If a deeper
            // scope has already been disposed in the wrong order
            // (shouldn't happen with using-statement discipline, but
            // defensive code here), we leave the chain alone.
            if (!ReferenceEquals(_top.Value, _node)) return;
            _top.Value = _node.Next;

            // Emit a closing marker so the operator can see when the
            // scope ended in the log. We use the ambient chain (which
            // no longer contains this scope) plus this scope's own
            // prefix, so nested END markers are visible in the right
            // order without the "Op=Op" double-print. MeasureScope
            // writes its own richer footer (with elapsed) in Dispose
            // and pops the stack itself, so it never reaches this
            // branch.
            SmartConLogger.WriteMain("INF", $" === END ===");
        }
    }
}

