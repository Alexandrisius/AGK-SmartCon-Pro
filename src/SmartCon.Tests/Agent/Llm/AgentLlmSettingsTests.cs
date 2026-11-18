using Xunit;
using SmartCon.Agent.Services.Llm;

namespace SmartCon.Tests.Agent.Llm;

[Collection("LlmSettings")]
public sealed class AgentLlmSettingsTests : IDisposable
{
    public void Dispose() => AgentLlmSettings.StoredKeyProvider = null;

    [Fact]
    public void Apply_SameProviderWithBlankKey_KeepsCurrentKey()
    {
        var settings = new AgentLlmSettings
        {
            Provider = "openrouter",
            ApiKey = "sk-or-current"
        };

        settings.Apply("openrouter", "new-model", string.Empty, string.Empty);

        Assert.Equal("sk-or-current", settings.ApiKey);
        Assert.Equal("new-model", settings.Model);
    }

    [Fact]
    public void Apply_ProviderSwitchWithBlankKey_DoesNotCarryPreviousKey()
    {
        AgentLlmSettings.StoredKeyProvider = _ => null;
        var settings = new AgentLlmSettings
        {
            Provider = "openrouter",
            ApiKey = "sk-or-previous"
        };

        settings.Apply("opencode-go", "other-model", string.Empty, string.Empty);

        Assert.Equal(string.Empty, settings.ApiKey);
        Assert.Equal("opencode-go", settings.Provider);
        Assert.Equal(AgentLlmSettings.DefaultOpenCodeGoBaseUrl, settings.BaseUrl);
    }

    [Fact]
    public void Apply_ProviderSwitch_LoadsStoredKeyForTargetProvider()
    {
        string? requested = null;
        AgentLlmSettings.StoredKeyProvider = provider =>
        {
            requested = provider;
            return "sk-go-stored";
        };

        var settings = new AgentLlmSettings
        {
            Provider = "openrouter",
            ApiKey = "sk-or-previous"
        };

        settings.Apply("opencode-go", "other-model", string.Empty, string.Empty);

        Assert.Equal("opencode-go", requested);
        Assert.Equal("sk-go-stored", settings.ApiKey);
    }

    [Fact]
    public void Apply_ExplicitKey_WinsOverStoredKey()
    {
        AgentLlmSettings.StoredKeyProvider = _ => "sk-stored";

        var settings = new AgentLlmSettings
        {
            Provider = "openrouter",
            ApiKey = "sk-or-previous"
        };

        settings.Apply("opencode-go", "other-model", " sk-explicit ", string.Empty);

        Assert.Equal("sk-explicit", settings.ApiKey);
    }
}

[CollectionDefinition("LlmSettings", DisableParallelization = true)]
public sealed class LlmSettingsCollection;
