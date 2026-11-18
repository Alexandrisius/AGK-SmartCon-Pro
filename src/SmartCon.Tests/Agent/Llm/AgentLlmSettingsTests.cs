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
    public void H1Flags_DefaultToOn()
    {
        var settings = new AgentLlmSettings();

        Assert.True(settings.ToolCatalogV2);
        Assert.True(settings.ContextCompactionV2);
        Assert.True(settings.WorkingStateEnabled);
    }

    [Fact]
    public void Load_H1FlagsOnWhenEnvironmentIsEmpty()
    {
        WithEnvironment(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["SMARTCON_AGENT_TOOL_CATALOG_V2"] = null,
                ["SMARTCON_AGENT_CONTEXT_COMPACTION_V2"] = null,
                ["SMARTCON_AGENT_WORKING_STATE"] = null
            },
            () =>
            {
                var settings = AgentLlmSettings.Load();

                Assert.True(settings.ToolCatalogV2);
                Assert.True(settings.ContextCompactionV2);
                Assert.True(settings.WorkingStateEnabled);
            });
    }

    [Fact]
    public void Load_H1FlagsEnabledByOneOrTrue()
    {
        WithEnvironment(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["SMARTCON_AGENT_TOOL_CATALOG_V2"] = "1",
                ["SMARTCON_AGENT_CONTEXT_COMPACTION_V2"] = "TRUE",
                ["SMARTCON_AGENT_WORKING_STATE"] = "True"
            },
            () =>
            {
                var settings = AgentLlmSettings.Load();

                Assert.True(settings.ToolCatalogV2);
                Assert.True(settings.ContextCompactionV2);
                Assert.True(settings.WorkingStateEnabled);
            });
    }

    [Fact]
    public void Load_H1Flags_ZeroDisablesAndUnknownKeepsDefault()
    {
        WithEnvironment(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["SMARTCON_AGENT_TOOL_CATALOG_V2"] = "0",
                ["SMARTCON_AGENT_CONTEXT_COMPACTION_V2"] = "yes",
                ["SMARTCON_AGENT_WORKING_STATE"] = "off"
            },
            () =>
            {
                var settings = AgentLlmSettings.Load();

                Assert.False(settings.ToolCatalogV2);
                Assert.True(settings.ContextCompactionV2);
                Assert.True(settings.WorkingStateEnabled);
            });
    }

    [Fact]
    public void Load_H1Flags_ZeroSwitchesEverythingOff()
    {
        WithEnvironment(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["SMARTCON_AGENT_TOOL_CATALOG_V2"] = "0",
                ["SMARTCON_AGENT_CONTEXT_COMPACTION_V2"] = "0",
                ["SMARTCON_AGENT_WORKING_STATE"] = "false"
            },
            () =>
            {
                var settings = AgentLlmSettings.Load();

                Assert.False(settings.ToolCatalogV2);
                Assert.False(settings.ContextCompactionV2);
                Assert.False(settings.WorkingStateEnabled);
            });
    }

    [Fact]
    public void Load_UsesEnvironmentValuesProviderInsteadOfSecretsFile()
    {
        WithEnvironment(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["SMARTCON_LLM_MODEL"] = null,
                ["SMARTCON_AGENT_WORKING_STATE"] = null
            },
            () =>
            {
                var settings = AgentLlmSettings.Load();

                Assert.Equal("seam-model", settings.Model);
                Assert.True(settings.WorkingStateEnabled);
            },
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["SMARTCON_LLM_MODEL"] = "seam-model",
                ["SMARTCON_AGENT_WORKING_STATE"] = "1"
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
