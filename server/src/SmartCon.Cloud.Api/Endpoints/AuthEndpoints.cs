// SPDX-License-Identifier: Business Source License 1.1 (see server/LICENSE); converts to Apache-2.0 after Change Date

namespace SmartCon.Cloud.Api.Endpoints;

using Auth;
using Data;
using Domain;
using Microsoft.AspNetCore.Identity;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/auth");

        group.MapPost("/register", async (
            RegisterRequest body,
            UserManager<CloudUser> users,
            TokenService tokens,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Email) || string.IsNullOrWhiteSpace(body.Password)
                || string.IsNullOrWhiteSpace(body.DisplayName))
                return Results.BadRequest(new { code = "validation", error = "email, password и displayName обязательны" });

            var user = new CloudUser
            {
                UserName = body.Email.Trim(),
                Email = body.Email.Trim(),
                DisplayName = body.DisplayName.Trim(),
                EmailConfirmed = true, // подтверждение email — C5 (ADR-076 Consequences)
            };
            var result = await users.CreateAsync(user, body.Password);
            if (!result.Succeeded)
            {
                // no email-enumeration: единый отказ без раскрытия причины «занят» (ADR-076 §1)
                return Results.BadRequest(new { code = "registration_failed", error = "регистрация не выполнена (проверьте email/пароль)" });
            }

            var pair = await tokens.IssueRefreshTokenAsync(user, ct);
            return Results.Json(AuthResponse.From(user, pair), statusCode: 201);
        });

        group.MapPost("/login", async (
            LoginRequest body,
            UserManager<CloudUser> users,
            TokenService tokens,
            CancellationToken ct) =>
        {
            var user = await users.FindByNameAsync(body.Email?.Trim() ?? string.Empty);
            if (user is null || !await users.CheckPasswordAsync(user, body.Password ?? string.Empty))
                return Results.Unauthorized();

            var pair = await tokens.IssueRefreshTokenAsync(user, ct);
            return Results.Ok(AuthResponse.From(user, pair));
        });

        group.MapPost("/refresh", async (
            RefreshRequest body,
            TokenService tokens,
            CancellationToken ct) =>
        {
            if (string.IsNullOrEmpty(body.RefreshToken)) return Results.Unauthorized();
            var pair = await tokens.RotateAsync(body.RefreshToken, ct);
            return pair is null ? Results.Unauthorized() : Results.Ok(TokenOnly.From(pair));
        });

        group.MapPost("/logout", async (
            RefreshRequest body,
            TokenService tokens,
            CancellationToken ct) =>
        {
            if (!string.IsNullOrEmpty(body.RefreshToken)) await tokens.RevokeAsync(body.RefreshToken, ct);
            return Results.NoContent();
        });

        return app;
    }

    public sealed record RegisterRequest(string Email, string Password, string DisplayName);
    public sealed record LoginRequest(string Email, string Password);
    public sealed record RefreshRequest(string RefreshToken);

    public sealed record AuthResponse(string AccessToken, string RefreshToken, int ExpiresInMinutes, UserDto User)
    {
        public static AuthResponse From(CloudUser u, TokenPair p) =>
            new(p.AccessToken, p.RefreshToken, TokenService.AccessTtlMinutes, new UserDto(u.Id, u.DisplayName, u.Email!));
    }

    public sealed record TokenOnly(string AccessToken, string RefreshToken, int ExpiresInMinutes)
    {
        public static TokenOnly From(TokenPair p) => new(p.AccessToken, p.RefreshToken, TokenService.AccessTtlMinutes);
    }

    public sealed record UserDto(Guid Id, string DisplayName, string Email);
}
