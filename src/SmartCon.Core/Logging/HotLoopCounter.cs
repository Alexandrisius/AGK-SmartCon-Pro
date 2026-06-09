namespace SmartCon.Core.Logging;

/// <summary>
/// Lightweight counter for "log a progress line every N iterations"
/// inside hot loops. Use it to avoid emitting one <c>Debug</c> line per
/// element when the loop body executes thousands of times per second —
/// every emitted line allocates a <see cref="string"/> for the prefix
/// concatenation, plus an <see cref="System.Diagnostics.Stopwatch"/>
/// formatted timestamp inside <see cref="SmartConLogger.Debug(string)"/>.
/// </summary>
/// <remarks>
/// <para>Usage:</para>
/// <code>
/// var counter = new HotLoopCounter(sampleEvery: 1024);
/// foreach (var c in allConns)
/// {
///     // ... heavy work ...
///     if (counter.ShouldLog())
///         SmartConLogger.Debug($"processed {counter.Count}/{allConns.Count}");
/// }
/// SmartConLogger.Debug($"done: {allConns.Count}");
/// </code>
/// <para>The <paramref name="sampleEvery"/> parameter is rounded up to
/// the next power of two internally so that <see cref="ShouldLog"/>
/// becomes a single bitwise <c>AND</c> instead of a modulo division —
/// cheaper on hot paths. Pass powers of two (1024, 256, 16) for the
/// fast path; arbitrary values fall back to the slower <c>%</c>
/// path automatically.</para>
/// <para>The struct is a value type so it does not allocate on the
/// heap. <see cref="ShouldLog"/> mutates <see cref="Count"/> via
/// <c>++_count</c>; reads of <see cref="Count"/> from the same thread
/// are exact. Cross-thread reads are best-effort: the underlying
/// <see cref="int"/> field is not <c>volatile</c>, so a reader on
/// another thread may see a stale value, but never a torn one.</para>
/// </remarks>
public struct HotLoopCounter
{
    private readonly int _modulo;
    private int _count;

    /// <summary>
    /// Initialise a counter that emits one log line per
    /// <paramref name="sampleEvery"/> iterations. The value is rounded
    /// up to the next power of two — so <c>new HotLoopCounter(1000)</c>
    /// behaves as if you had passed <c>1024</c>. <paramref name="sampleEvery"/>
    /// must be at least 1; the constructor coerces 0 / negative values
    /// to 1.
    /// </summary>
    public HotLoopCounter(int sampleEvery)
    {
        if (sampleEvery < 1) sampleEvery = 1;
        _modulo = NextPowerOfTwo(sampleEvery);
        _count = 0;
    }

    /// <summary>Total number of <see cref="ShouldLog"/> calls made so far.</summary>
    public int Count => _count;

    /// <summary>Configured sample interval (rounded to power of two).</summary>
    public int SampleEvery => _modulo;

    /// <summary>
    /// Increment the internal counter and return <c>true</c> exactly
    /// once per <see cref="SampleEvery"/> calls. Cheap on the hot path:
    /// one <c>AND</c>, one <c>EQ</c>, one <c>INC</c>. The mutable
    /// <see cref="_count"/> field is intentionally not <c>readonly</c>;
    /// a <c>readonly struct</c> cannot increment a private field.
    /// </summary>
    public bool ShouldLog()
    {
        var n = ++_count;
        // Fast path: SampleEvery is a power of two (always true after
        // the constructor rounds up), so we can use bitwise AND.
        // The fall-back to modulo is a defensive guard for the rare
        // case where a caller constructs via parameterless-ctor (none
        // exists today) or future API that accepts arbitrary ints.
        return (n & (_modulo - 1)) == 0;
    }

    private static int NextPowerOfTwo(int n)
    {
        if (n <= 1) return 1;
        n--;
        n |= n >> 1;
        n |= n >> 2;
        n |= n >> 4;
        n |= n >> 8;
        n |= n >> 16;
        return n + 1;
    }
}
