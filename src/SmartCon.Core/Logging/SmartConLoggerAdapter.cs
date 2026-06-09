using System.Runtime.CompilerServices;

namespace SmartCon.Core.Logging;

/// <summary>
/// Default <see cref="ISmartConLogger"/> implementation that delegates
/// to the static <see cref="SmartConLogger"/> facade. Useful when a
/// DI container is configured to resolve <c>ISmartConLogger</c> as a
/// singleton but the actual write path stays the static one (no
/// behavioural change).
/// </summary>
/// <remarks>
/// The static facade is the only writer in Phase 1/2. In Phase 3 a
/// background <see cref="System.Threading.Channels.Channel{T}"/> drain
/// may replace it; this adapter keeps call sites stable by always
/// routing through the public API.
/// </remarks>
public sealed class SmartConLoggerAdapter : ISmartConLogger
{
    public static readonly SmartConLoggerAdapter Instance = new();

    private SmartConLoggerAdapter() { }

    public void Info(string message) => SmartConLogger.Info(message);
    public void Debug(string message) => SmartConLogger.Debug(message);
    public void Warn(string message) => SmartConLogger.Warn(message);
    public void Error(string message) => SmartConLogger.Error(message);
}
