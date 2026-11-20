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

        settings.Apply("openrouter", "new-model", string.Empty);

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

        settings.Apply("opencode-go", "other-model", string.Empty);

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

        settings.Apply("opencode-go", "other-model", string.Empty);

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

        settings.Apply("opencode-go", "other-model", " sk-explicit ");

        Assert.Equal("sk-explicit", settings.ApiKey);
    }

    [Fact]
    public void Apply_ProviderSwitchWithBlankModel_UsesTargetProviderDefault()
    {
        AgentLlmSettings.StoredKeyProvider = _ => null;
        var settings = new AgentLlmSettings
        {
            Provider = "openrouter",
            ApiKey = "sk-or-current"
        };

        settings.Apply("opencode-go", "   ", string.Empty);

        Assert.Equal(AgentLlmSettings.DefaultOpenCodeGoModel, settings.Model);
    }

    [Fact]
    public void Apply_ProviderSwitchWithTypedModel_KeepsTypedName()
    {
        AgentLlmSettings.StoredKeyProvider = _ => null;
        var settings = new AgentLlmSettings
        {
            Provider = "openrouter",
            ApiKey = "sk-or-current"
        };

        settings.Apply("opencode-go", "qwen4-max", string.Empty);

        Assert.Equal("qwen4-max", settings.Model);
    }

    [Fact]
    public void Apply_SameProviderWithCustomBaseUrl_KeepsBaseUrl()
    {
        var settings = new AgentLlmSettings
        {
            Provider = "openrouter",
            ApiKey = "sk-or-current",
            BaseUrl = "https://proxy.local/v1"
        };

        settings.Apply("openrouter", "new-model", string.Empty);

        Assert.Equal("https://proxy.local/v1", settings.BaseUrl);
    }

    [Fact]
    public void SummarySettings_HaveCompactionDefaults()
    {
        var settings = new AgentLlmSettings();

        Assert.Equal(string.Empty, settings.SummaryModel);
        Assert.Equal(24_000, settings.SummaryMaxInputTokens);
        Assert.Equal(1_500, settings.SummaryMaxOutputTokens);
    }

    [Fact]
    public void Load_ReadsSummarySettingsAndClampsThem()
    {
        WithEnvironment(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["SMARTCON_AGENT_SUMMARY_MODEL"] = "cheap/model",
                ["SMARTCON_AGENT_SUMMARY_MAX_INPUT_TOKENS"] = "1",
                ["SMARTCON_AGENT_SUMMARY_MAX_OUTPUT_TOKENS"] = "999999"
            },
            () =>
            {
                var settings = AgentLlmSettings.Load();

                Assert.Equal("cheap/model", settings.SummaryModel);
                Assert.Equal(4_000, settings.SummaryMaxInputTokens);
                Assert.Equal(8_192, settings.SummaryMaxOutputTokens);
            });
    }

    [Fact]
    public void Load_UsesEnvironmentValuesProviderInsteadOfSecretsFile()
    {
        WithEnvironment(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["SMARTCON_LLM_MODEL"] = null
            },
            () =>
            {
                var settings = AgentLlmSettings.Load();

                Assert.Equal("seam-model", settings.Model);
            },
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["SMARTCON_LLM_MODEL"] = "seam-model"
            });
    }

    [Fact]
    public void Apply_ProviderSwitchToZai_UsesCodingEndpointAndDefaultModel()
    {
        AgentLlmSettings.StoredKeyProvider = _ => "sk-zai-stored";
        var settings = new AgentLlmSettings
        {
            Provider = "openrouter",
            ApiKey = "sk-or-previous",
            Model = "deepseek/deepseek-v4.1-flash-1017"
        };

        settings.Apply("zai-glm", string.Empty, string.Empty);

        Assert.Equal("zai-glm", settings.Provider);
        Assert.Equal(AgentLlmSettings.DefaultZaiBaseUrl, settings.BaseUrl);
        Assert.Equal(AgentLlmSettings.DefaultZaiModel, settings.Model);
        Assert.Equal("sk-zai-stored", settings.ApiKey);
    }

    [Fact]
    public void GetStoredKeyFor_ZaiProvider_ReadsZaiEnvKey()
    {
        WithEnvironment(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["SMARTCON_ZAI_API_KEY"] = null,
                ["SMARTCON_OPENROUTER_API_KEY"] = null
            },
            () =>
            {
                var key = AgentLlmSettings.GetStoredKeyFor("zai-glm");

                Assert.Equal("sk-zai-from-file", key);
            },
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["SMARTCON_ZAI_API_KEY"] = "sk-zai-from-file",
                ["SMARTCON_OPENROUTER_API_KEY"] = "sk-or-foreign"
            });
    }

    [Fact]
    public void CachePolicy_ZaiProvider_IsImplicitCaching()
    {
        var plan = AgentProviderCachePolicy.Resolve(
            "zai-glm",
            "glm-5.3",
            0.2,
            Guid.NewGuid());

        Assert.Equal(AgentCacheKind.Implicit, plan.Kind);
        Assert.Null(plan.PromptCacheKey);
        Assert.False(plan.UseAnthropicBreakpoints);
    }

    private static void WithEnvironment(
        IReadOnlyDictionary<string, string?> values,
        Action action,
        IReadOnlyDictionary<string, string>? envFileValues = null)
    {
        var previous = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var name in values.Keys)
            previous[name] = Environment.GetEnvironmentVariable(name);

        try
        {
            AgentLlmSettings.EnvValuesProvider = envFileValues is null
                ? () => new Dictionary<string, string>(StringComparer.Ordinal)
                : () => envFileValues;
            foreach (var pair in values)
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            action();
        }
        finally
        {
            foreach (var pair in previous)
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            AgentLlmSettings.EnvValuesProvider = null;
        }
    }
}

[CollectionDefinition("LlmSettings", DisableParallelization = true)]
public sealed class LlmSettingsCollection;
