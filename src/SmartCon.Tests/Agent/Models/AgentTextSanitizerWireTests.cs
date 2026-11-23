using System.Text.Json;
using SmartCon.Core.Models.Agent;
using Xunit;

namespace SmartCon.Tests.Agent.Models;

/// <summary>
/// The sanitized JSON is the text the model reads: non-ASCII text stays as
/// it is, control characters are still neutralized.
/// </summary>
public sealed class AgentTextSanitizerWireTests
{
    [Fact]
    public void SanitizeJson_KeepsCyrillicAsIs()
    {
        const string json =
            "{\"family\":\"Кран шаровой\",\"breadcrumb\":\"Инженерные системы › Трубопроводная арматура › Краны шаровые\"}";

        var sanitized = AgentTextSanitizer.SanitizeJson(json);

        Assert.Contains("Кран шаровой", sanitized, StringComparison.Ordinal);
        Assert.Contains("›", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u04", sanitized, StringComparison.Ordinal);
        Assert.True(sanitized.Length < json.Length + 40, sanitized);
    }

    [Fact]
    public void SanitizeJson_StillEscapesControlCharacters()
    {
        // A valid escape in the input: the value keeps a visible \u000B text.
        var escaped = AgentTextSanitizer.SanitizeJson("{\"name\":\"a\\u000Bb\"}");
        Assert.Contains("a\\\\u000Bb", escaped, StringComparison.Ordinal);
        using (var document = JsonDocument.Parse(escaped))
        {
            Assert.Equal("a\\u000Bb", document.RootElement.GetProperty("name").GetString());
        }

        // A raw control character inside the JSON text: the fallback keeps the
        // text valid and the character never reaches the model raw.
        var raw = AgentTextSanitizer.SanitizeJson("{\"name\":\"a" + (char)0x000B + "b\"}");
        Assert.DoesNotContain(((char)0x000B).ToString(), raw, StringComparison.Ordinal);
        Assert.Contains("\\u000B", raw, StringComparison.Ordinal);
        using (var document = JsonDocument.Parse(raw))
        {
            Assert.Equal("a\u000Bb", document.RootElement.GetProperty("name").GetString());
        }
    }

    [Fact]
    public void SanitizeJson_DoesNotCutTheToolMessage()
    {
        // The reason and the next step live at the end of a message: capped at
        // 400 characters the model lost exactly that part.
        var message = new string('m', 900) + " [Action: narrow the selection]";
        var json = "{\"message\":\"" + message + "\",\"name\":\"" + new string('n', 900) + "\"}";

        var sanitized = AgentTextSanitizer.SanitizeJson(json);

        using var document = JsonDocument.Parse(sanitized);
        Assert.Equal(message, document.RootElement.GetProperty("message").GetString());
        Assert.EndsWith("…", document.RootElement.GetProperty("name").GetString(), StringComparison.Ordinal);
    }
}
