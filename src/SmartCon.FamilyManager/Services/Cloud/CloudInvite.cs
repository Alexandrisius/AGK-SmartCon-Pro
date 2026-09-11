using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartCon.FamilyManager.Services.Cloud;

/// <summary>
/// Строка-приглашение (master plan §7.3.1):
/// <c>smartcon-cloud:subscribe:{base64url(JSON{endpoint?,slug,key?})}</c>.
/// Одно поле — вставил и подключился; endpoint опущен = официальный сервер
/// (для среза v1 всегда присутствует — dev-сервер владельца). base64url без
/// padding ('-','_' вместо '+','/'); опечатка slug и «нет доступа» на
/// сервере неотличимы (404-антиоракул).
/// </summary>
public static class CloudInvite
{
    public const string Prefix = "smartcon-cloud:subscribe:";

    public sealed record InviteData(string Endpoint, string Slug, string? Key = null);

    /// <summary>Парсит строку приглашения; null — формат не распознан.</summary>
    public static InviteData? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text!.Trim();
        if (!trimmed.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return null;

        string json;
        try
        {
            var base64 = NormalizeBase64Url(trimmed[Prefix.Length..]);
            var bytes = Convert.FromBase64String(base64);
            json = Encoding.UTF8.GetString(bytes);
        }
        catch (FormatException)
        {
            return null;
        }

        try
        {
            var dto = JsonSerializer.Deserialize<InviteDto>(json);
            if (dto is null || string.IsNullOrWhiteSpace(dto.Slug)) return null;
            return new InviteData(dto.Endpoint ?? string.Empty, dto.Slug!, dto.Key);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Собирает строку приглашения из endpoint+slug (ключи — вне среза v1).</summary>
    public static string Build(string endpoint, string slug)
    {
        var json = JsonSerializer.Serialize(new InviteDto
        {
            Endpoint = endpoint,
            Slug = slug,
        });
        return Prefix + ToBase64Url(Encoding.UTF8.GetBytes(json));
    }

    /// <summary>base64url → base64 с восстановлением padding (net48-совместимо).</summary>
    private static string NormalizeBase64Url(string value)
    {
        var sb = new StringBuilder(value.Replace('-', '+').Replace('_', '/'));
        while (sb.Length % 4 != 0) sb.Append('=');
        return sb.ToString();
    }

    private static string ToBase64Url(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private sealed class InviteDto
    {
        [JsonPropertyName("endpoint")] public string? Endpoint { get; set; }
        [JsonPropertyName("slug")] public string? Slug { get; set; }
        [JsonPropertyName("key")] public string? Key { get; set; }
    }
}
