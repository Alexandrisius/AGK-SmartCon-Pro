using SmartCon.Core.Services;
using Xunit;

namespace SmartCon.Tests.Core.Services;

public sealed class ServiceHostTests
{
    [Fact]
    public void GetService_WhenNotInitialized_ThrowsInvalidOperationException()
    {
        ServiceHost.Reset();

        Assert.Throws<InvalidOperationException>(() => ServiceHost.GetService<string>());
    }

    [Fact]
    public void GetService_WhenInitialized_ResolvesService()
    {
        ServiceHost.Initialize(type =>
        {
            if (type == typeof(string))
            {
                return "test-value";
            }

            throw new InvalidOperationException($"Unknown type: {type}");
        });

        try
        {
            var result = ServiceHost.GetService<string>();
            Assert.Equal("test-value", result);
        }
        finally
        {
            ServiceHost.Reset();
        }
    }
}
