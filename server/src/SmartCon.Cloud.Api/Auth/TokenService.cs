// SPDX-License-Identifier: Business Source License 1.1 (see server/LICENSE); converts to Apache-2.0 after Change Date

namespace SmartCon.Cloud.Api.Auth;

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Data;
using Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

public sealed record TokenPair(string AccessToken, string RefreshToken, DateTime RefreshExpiresAtUtc);

/// <summary>JWT access (30 мин) + rotating refresh (7 дней) с reuse-детектом (ADR-076 §2).</summary>
public sealed class TokenService(IConfiguration config, CloudDbContext db)
{
    public const int AccessTtlMinutes = 30;
    public const int RefreshTtlDays = 7;

    public string IssueAccessToken(CloudUser user)
    {
        var jwt = new JwtSecurityToken(
            issuer: config["Jwt:Issuer"],
            audience: config["Jwt:Audience"],
            claims:
            [
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            ],
            notBefore: DateTime.UtcNow,
            expires: DateTime.UtcNow.AddMinutes(AccessTtlMinutes),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Convert.FromHexString(config["Jwt:SigningKeyHex"]!)),
                SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    public async Task<TokenPair> IssueRefreshTokenAsync(CloudUser user, CancellationToken ct)
    {
        var raw = Base64Url(RandomNumberGenerator.GetBytes(32));
        var token = new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = Sha256Hex(raw),
            ExpiresAtUtc = DateTime.UtcNow.AddDays(RefreshTtlDays),
        };
        db.RefreshTokens.Add(token);
        await db.SaveChangesAsync(ct);
        return new TokenPair(IssueAccessToken(user), raw, token.ExpiresAtUtc);
    }

    /// <summary>Ротация: живой токен → новая пара; expiry → 401 без побочных эффектов;
    /// reuse ОТЗВАННОГО токена → отзыв ВСЕХ сессий юзера (кража, ADR-076 §2 / E16).</summary>
    public async Task<TokenPair?> RotateAsync(string rawRefreshToken, CancellationToken ct)
    {
        var hash = Sha256Hex(rawRefreshToken);
        var stored = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (stored is null) return null;

        if (stored.ExpiresAtUtc <= DateTime.UtcNow)
            return null; // истёкший токен — не кража: другие сессии не трогаем

        if (stored.RevokedAtUtc is not null)
        {
            var now = DateTime.UtcNow;
            foreach (var t in db.RefreshTokens.Where(t => t.UserId == stored.UserId && t.RevokedAtUtc == null))
                t.RevokedAtUtc = now;
            await db.SaveChangesAsync(ct);
            return null;
        }

        var user = await db.Users.OfType<CloudUser>().FirstOrDefaultAsync(u => u.Id == stored.UserId, ct);
        if (user is null) return null;

        stored.RevokedAtUtc = DateTime.UtcNow;
        var pair = await IssueRefreshTokenAsync(user, ct);
        stored.ReplacedBy = db.RefreshTokens.Local.First(t => t.TokenHash == Sha256Hex(pair.RefreshToken)).Id;
        await db.SaveChangesAsync(ct);
        return pair;
    }

    public async Task RevokeAsync(string rawRefreshToken, CancellationToken ct)
    {
        var hash = Sha256Hex(rawRefreshToken);
        var stored = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (stored is null || stored.RevokedAtUtc is not null) return;
        stored.RevokedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private static string Sha256Hex(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
