// SPDX-License-Identifier: Business Source License 1.1 (see server/LICENSE); converts to Apache-2.0 after Change Date

namespace SmartCon.Cloud.Api.Endpoints;

using Cas;
using Data;
using Domain;
using Microsoft.EntityFrameworkCore;

public static class FileEndpoints
{
    public static IEndpointRouteBuilder MapFileEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPut("/v1/files/{sha256}", async (
            string sha256,
            HttpRequest request,
            IObjectStorage storage,
            CloudDbContext db,
            CancellationToken ct) =>
        {
            if (!IsSha256(sha256)) return Results.BadRequest(new { code = "bad_hash", error = "sha256 должен быть 64 hex-символа" });

            // Streaming: содержимое не буферизуется, SHA-256 считается на лету (квоты/лимиты — C1).
            var actual = await storage.PutAsync(request.Body, ct);
            if (!string.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { code = "checksum_mismatch", error = $"тело не соответствует заявленному sha256 (фактический {actual})" });

            if (!await db.CasObjects.AnyAsync(o => o.Sha256 == sha256, ct))
            {
                db.CasObjects.Add(new CasObject { Sha256 = sha256, SizeBytes = await storage.SizeOfAsync(sha256, ct) });
                await db.SaveChangesAsync(ct);
            }
            return Results.Ok(new { sha256, stored = true });
        }).RequireAuthorization();

        app.MapPost("/v1/files/resolve", async (
            ResolveRequest body,
            IObjectStorage storage,
            HttpRequest request,
            CancellationToken ct) =>
        {
            var urls = new Dictionary<string, string>();
            var unavailable = new List<string>();
            foreach (var hash in (body.Sha256 ?? []).Distinct())
            {
                if (!IsSha256(hash))
                {
                    unavailable.Add(hash); // невалидный хэш = недоступен (никаких путей в CAS)
                    continue;
                }
                // Срез: существование в CAS; per-catalog подписка на каждом хэше — C1 (ADR-076 §4).
                if (await storage.ExistsAsync(hash, ct))
                    urls[hash] = $"{request.Scheme}://{request.Host}/v1/files/{hash}";
                else
                    unavailable.Add(hash);
            }
            return Results.Ok(new ResolveResponse(urls, unavailable));
        }).RequireAuthorization();

        // Presigned-семантика среза (ADR-075 §4): URL играет роль короткоживущего токена, как R2 presigned GET;
        // выдача URL (resolve) авторизована, сам URL — opaque строка для HttpClient.GetAsync без заголовков.
        app.MapGet("/v1/files/{sha256}", async (
            string sha256,
            IObjectStorage storage,
            CancellationToken ct) =>
        {
            if (!IsSha256(sha256))
                return Results.BadRequest(new { code = "bad_hash", error = "sha256 должен быть 64 hex-символа" });
            if (!await storage.ExistsAsync(sha256, ct))
                return Results.NotFound(new { code = "not_found", error = "объект не существует" });
            var stream = await storage.OpenReadAsync(sha256, ct);
            return Results.Stream(stream, "application/octet-stream");
        });

        return app;
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(char.IsAsciiHexDigit);

    public sealed record ResolveRequest(string[] Sha256);
    public sealed record ResolveResponse(Dictionary<string, string> Urls, List<string> Unavailable);
}
