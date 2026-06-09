using SmartCon.Core.Logging;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Models;

public sealed class ISmartConLoggerContractTests
{
    [Fact]
    public void SmartConLoggerAdapter_ForwardsInfo()
    {
        // Smoke: the adapter routes to the static SmartConLogger.
        // The static writes to smartcon.log under %AppData%\AGK\SmartCon;
        // we just ensure no exception is thrown.
        var adapter = SmartConLoggerAdapter.Instance;
        adapter.Info("adapter info probe");
    }

    [Fact]
    public void SmartConLoggerAdapter_ForwardsAllLevels()
    {
        var adapter = SmartConLoggerAdapter.Instance;
        adapter.Debug("adapter debug");
        adapter.Info("adapter info");
        adapter.Warn("adapter warn");
        adapter.Error("adapter error");
    }

    [Fact]
    public void SmartConLoggerAdapter_IsSingleton()
    {
        Assert.Same(SmartConLoggerAdapter.Instance, SmartConLoggerAdapter.Instance);
    }

    [Fact]
    public void SmartConLoggerAdapter_ImplementsInterface()
    {
        ISmartConLogger logger = SmartConLoggerAdapter.Instance;
        Assert.NotNull(logger);
        logger.Info("via interface");
    }
}
