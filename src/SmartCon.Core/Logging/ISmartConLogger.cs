namespace SmartCon.Core.Logging;

/// <summary>
/// Abstraction over the SmartCon logger so call sites can be tested
/// with a fake implementation and so the static <see cref="SmartConLogger"/>
/// can be swapped for a DI-injected instance in the future (Phase 3).
/// </summary>
/// <remarks>
/// <para>
/// All methods on this interface map 1:1 to a corresponding static
/// method on <see cref="SmartConLogger"/>. The static facade continues
/// to be the default entry point; this interface exists so that:
/// </para>
/// <list type="bullet">
///   <item>Unit tests can inject a recording fake that captures every call.</item>
///   <item>A future DI container can register a singleton instance instead of using the static facade.</item>
///   <item>A <c>Microsoft.Extensions.Logging.ILogger</c> adapter can route M.E.L. consumers into our sinks.</item>
/// </list>
/// <para>
/// The interface intentionally does NOT expose <c>BeginScope</c> /
/// <c>Measure</c> on the instance — scope state lives in
/// <see cref="LogScopeProvider"/> (AsyncLocal-based) and is orthogonal
/// to the writer implementation. Callers that need scope use
/// <see cref="SmartConLogger.BeginScope(string, ValueTuple{string, object?}[])"/>
/// directly regardless of which writer is registered.
/// </para>
/// </remarks>
public interface ISmartConLogger
{
    void Info(string message);
    void Debug(string message);
    void Warn(string message);
    void Error(string message);
}
