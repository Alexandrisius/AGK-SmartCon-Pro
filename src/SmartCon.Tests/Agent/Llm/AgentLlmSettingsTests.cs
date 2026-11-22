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

    [Fact]
    public void Defaults_ReasoningAndBudget_FitEffortMax()
    {
        var settings = new AgentLlmSettings();

        Assert.Equal(AgentLlmSettings.DefaultReasoningEffort, settings.ReasoningEffort);
        Assert.Equal("max", settings.ReasoningEffort);
        Assert.Null(settings.ReasoningFormat);
        Assert.Equal(64_000, settings.MaxOutputTokens);
        Assert.Equal(1_800, settings.MaxRequestSeconds);
        Assert.Equal(120, settings.TimeoutSeconds);
    }

    [Theory]
    [InlineData("openrouter", AgentReasoningFormat.ReasoningObject)]
    [InlineData("zai-glm", AgentReasoningFormat.ReasoningEffort)]
    [InlineData("opencode-go", AgentReasoningFormat.ReasoningEffort)]
    [InlineData("mystery", AgentReasoningFormat.ReasoningEffort)]
    public void EffectiveReasoningFormat_AutoFollowsTheProvider(
        string provider,
        AgentReasoningFormat expected)
    {
        var settings = new AgentLlmSettings
        {
            Provider = provider,
            ReasoningFormat = null
        };

        Assert.Equal(expected, settings.EffectiveReasoningFormat);

        // An explicit choice wins over the provider default.
        settings.ReasoningFormat = AgentReasoningFormat.None;
        Assert.Equal(AgentReasoningFormat.None, settings.EffectiveReasoningFormat);
    }

    [Fact]
    public void Load_ReadsReasoningSettingsAndClampsBudget()
    {
        WithEnvironment(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["SMARTCON_LLM_PROVIDER"] = "zai-glm",
                ["SMARTCON_LLM_REASONING_FORMAT"] = "reasoning",
                ["SMARTCON_LLM_REASONING_EFFORT"] = "XHigh ",
                ["SMARTCON_LLM_MAX_OUTPUT_TOKENS"] = "1",
                ["SMARTCON_LLM_MAX_REQUEST_SECONDS"] = "10"
            },
            () =>
            {
                var settings = AgentLlmSettings.Load();

                Assert.Equal(AgentReasoningFormat.ReasoningObject, settings.ReasoningFormat);
                Assert.Equal("xhigh", settings.ReasoningEffort);
                Assert.Equal(AgentLlmSettings.MinMaxOutputTokens, settings.MaxOutputTokens);
                Assert.Equal(60, settings.MaxRequestSeconds);
            });
    }

    [Fact]
    public void Load_UnknownEffortAndFormat_FallBackWithWarnings()
    {
        WithEnvironment(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["SMARTCON_LLM_REASONING_FORMAT"] = "bogus",
                ["SMARTCON_LLM_REASONING_EFFORT"] = "ultra"
            },
            () =>
            {
                var settings = AgentLlmSettings.Load();

                Assert.Null(settings.ReasoningFormat);
                Assert.Equal("max", settings.ReasoningEffort);
            });
    }

    [Fact]
    public void Load_AutoFormatChoice_StaysAuto()
    {
        WithEnvironment(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["SMARTCON_LLM_REASONING_FORMAT"] = "auto"
            },
            () =>
            {
                Assert.Null(AgentLlmSettings.Load().ReasoningFormat);
            });
    }

    [Fact]
    public void NormalizeReasoningEffort_UnsetOrEmpty_DefaultsSilently()
    {
        // SMARTCON_LLM_REASONING_EFFORT unset is the normal state: no warning
        // (only a set-but-invalid value warns, see Load_UnknownEffortAndFormat).
        Assert.Equal(
            AgentLlmSettings.DefaultReasoningEffort,
            AgentLlmSettings.NormalizeReasoningEffort(null));
        Assert.Equal(
            AgentLlmSettings.DefaultReasoningEffort,
            AgentLlmSettings.NormalizeReasoningEffort(string.Empty));
        Assert.Equal(
            AgentLlmSettings.DefaultReasoningEffort,
            AgentLlmSettings.NormalizeReasoningEffort("   "));
    }

    [Fact]
    public void Load_MaxOutputTokensAboveCap_IsClamped()
    {
        WithEnvironment(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["SMARTCON_LLM_MAX_OUTPUT_TOKENS"] = "999000"
            },
            () =>
            {
                Assert.Equal(
                    AgentLlmSettings.MaxMaxOutputTokens,
                    AgentLlmSettings.Load().MaxOutputTokens);
            });
    }

    [Fact]
    public void SaveValues_RoundTripThroughLoad()
    {
        WithEnvironment(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["SMARTCON_LLM_PROVIDER"] = null,
                ["SMARTCON_LLM_MODEL"] = null,
                ["SMARTCON_LLM_REASONING_FORMAT"] = null,
                ["SMARTCON_LLM_REASONING_EFFORT"] = null,
                ["SMARTCON_LLM_MAX_OUTPUT_TOKENS"] = null,
                ["SMARTCON_ZAI_API_KEY"] = null,
                ["SMARTCON_ZAI_BASE_URL"] = null
            },
            () =>
            {
                var original = new AgentLlmSettings
                {
                    Provider = "zai-glm",
                    Model = "glm-5.3",
                    ApiKey = "sk-x",
                    BaseUrl = AgentLlmSettings.DefaultZaiBaseUrl,
                    ReasoningFormat = AgentReasoningFormat.ReasoningEffort,
                    ReasoningEffort = "high",
                    MaxOutputTokens = 123_456
                };

                var saved = original.BuildSaveValues();
                Assert.Equal("reasoning_effort", saved["SMARTCON_LLM_REASONING_FORMAT"]);
                Assert.Equal("high", saved["SMARTCON_LLM_REASONING_EFFORT"]);
                Assert.Equal("123456", saved["SMARTCON_LLM_MAX_OUTPUT_TOKENS"]);

                AgentLlmSettings.EnvValuesProvider = () => saved;
                var loaded = AgentLlmSettings.Load();

                Assert.Equal("zai-glm", loaded.Provider);
                Assert.Equal(AgentReasoningFormat.ReasoningEffort, loaded.ReasoningFormat);
                Assert.Equal("high", loaded.ReasoningEffort);
                Assert.Equal(123_456, loaded.MaxOutputTokens);
            });
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
