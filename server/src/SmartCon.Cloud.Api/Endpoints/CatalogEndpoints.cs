// SPDX-License-Identifier: Business Source License 1.1 (see server/LICENSE); converts to Apache-2.0 after Change Date

namespace SmartCon.Cloud.Api.Endpoints;

using System.Security.Claims;
using System.Text.Json;
using Data;
using Domain;
using Microsoft.EntityFrameworkCore;

public static class CatalogEndpoints
{
    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/catalogs", async (
            CreateCatalogRequest body,
            ClaimsPrincipal principal,
            CloudDbContext db,
            CancellationToken ct) =>
        {
            var user = await ResolveUserAsync(db, principal, ct);
            if (user is null) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(body.Name) || body.Name.Trim().Length > 200)
                return Results.BadRequest(new { code = "validation", error = "name обязателен (≤200 символов)" });

            // Slug: транслитерация кириллицы + уникализация суффиксом -2/-3/…
            // (ASCII-only фильтр порождал ПУСТОЙ slug для русских имён — каталог
            // становился недостижим, 404 на всех путях; GitHub-модель name-2).
            var slug = Slugify(body.Name);
            if (await db.Catalogs.AnyAsync(c => c.Slug == slug, ct))
            {
                for (var i = 2; ; i++)
                {
                    var candidate = $"{slug}-{i}";
                    if (!await db.Catalogs.AnyAsync(c => c.Slug == candidate, ct))
                    {
                        slug = candidate;
                        break;
                    }
                    if (i > 99) return Results.Conflict(new { code = "slug_taken", error = $"slug '{slug}' занят (slug immutable, ADR-075/E18)" });
                }
            }

            var catalog = new Catalog { Id = Guid.NewGuid(), OwnerUserId = user.Id, Slug = slug, Name = body.Name.Trim() };
            db.Catalogs.Add(catalog);
            await db.SaveChangesAsync(ct);
            return Results.Json(new CatalogDto(catalog.Id, catalog.Slug, catalog.Name, catalog.CurrentPublishSeq), statusCode: 201);
        }).RequireAuthorization();

        // Срез: publish = { manifest } (полная дельта-модель basePublishSeq/changes/kind — C1, §6.2/§6.3).
        app.MapPost("/v1/catalogs/{slug}/publish", async (
            string slug,
            PublishRequest body,
            ClaimsPrincipal principal,
            CloudDbContext db,
            CancellationToken ct) =>
        {
            var user = await ResolveUserAsync(db, principal, ct);
            if (user is null) return Results.Unauthorized();

            var catalog = await db.Catalogs.FirstOrDefaultAsync(c => c.Slug == slug, ct);
            if (catalog is null) return NotFound(slug);
            if (catalog.OwnerUserId != user.Id)
                return Results.Problem(statusCode: 403, title: "publish запрещён", detail: "только Owner публикует (Editors — C4)");

            var manifest = body.Manifest;
            if (manifest.ValueKind != JsonValueKind.Object || manifest.GetPropertyOr("formatVersion", 0) != 1)
                return Results.BadRequest(new { code = "invalid_manifest", error = "ожидается formatVersion=1" });

            var claimedSeq = manifest.GetPropertyOr("publishSeq", 0L);
            if (claimedSeq <= catalog.CurrentPublishSeq)
                return Results.Conflict(new
                {
                    code = "stale_base_seq",
                    error = $"каталог уже опубликован как #{catalog.CurrentPublishSeq}; перечитайте манифест и повторите (pull-before-push, ADR-077 §2)",
                    currentPublishSeq = catalog.CurrentPublishSeq,
                });
            if (claimedSeq != catalog.CurrentPublishSeq + 1)
                return Results.BadRequest(new { code = "invalid_manifest", error = $"publishSeq должен быть {catalog.CurrentPublishSeq + 1}" });

            var hashes = CollectSha256(manifest).Distinct().ToList();
            var known = await db.CasObjects.Where(o => hashes.Contains(o.Sha256)).Select(o => o.Sha256).ToListAsync(ct);
            var missing = hashes.Except(known, StringComparer.OrdinalIgnoreCase).ToList();
            if (missing.Count > 0)
                return Results.UnprocessableEntity(new { code = "missing_objects", error = "сначала загрузите файлы (PUT /v1/files/{sha256})", missing });

            var manifestJson = manifest.GetRawText();
            await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
            // Перечитываем seq в транзакции: параллельный publish другого автора ловится здесь (ADR-077 §3).
            // ReloadAsync обязателен: EF identity resolution не обновляет уже-отслеживаемую сущность через запрос.
            await db.Entry(catalog).ReloadAsync(ct);
            if (claimedSeq != catalog.CurrentPublishSeq + 1)
            {
                await tx.RollbackAsync(ct);
                return Results.Conflict(new
                {
                    code = "stale_base_seq",
                    error = $"каталог уже опубликован как #{catalog.CurrentPublishSeq}; перечитайте манифест и повторите (pull-before-push, ADR-077 §2)",
                    currentPublishSeq = catalog.CurrentPublishSeq,
                });
            }

            var seq = catalog.CurrentPublishSeq + 1;
            db.PublishPoints.Add(new PublishPoint
            {
                CatalogId = catalog.Id,
                Seq = seq,
                ManifestJson = manifestJson,
                ManifestSizeBytes = manifestJson.Length,
                PublishedBy = user.Id,
                ChangeCount = manifest.GetPropertyOr("items", JsonValueKind.Undefined) == JsonValueKind.Array
                    ? manifest.EnumerateArrayOr("items").Count()
                    : 0,
            });
            foreach (var hash in known)
                db.PublishPointFiles.Add(new PublishPointFile { CatalogId = catalog.Id, Seq = seq, Sha256 = hash });
            catalog.CurrentPublishSeq = seq;
            catalog.HashFormatVersion = manifest.GetPropertyOr("hashFormatVersion", catalog.HashFormatVersion);

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Json(new { publishSeq = seq }, statusCode: 201);
        }).RequireAuthorization();

        app.MapGet("/v1/catalogs/{slug}/manifest/latest", async (
            string slug,
            CloudDbContext db,
            CancellationToken ct) =>
        {
            var catalog = await db.Catalogs.FirstOrDefaultAsync(c => c.Slug == slug, ct);
            if (catalog is null || catalog.CurrentPublishSeq == 0) return NotFound(slug);

            var point = await db.PublishPoints
                .Where(p => p.CatalogId == catalog.Id && p.Seq == catalog.CurrentPublishSeq)
                .FirstAsync(ct);
            using var parsed = JsonDocument.Parse(point.ManifestJson);
            return Results.Ok(new LatestManifestResponse(point.Seq, point.ManifestSizeBytes, parsed.RootElement.Clone()));
        }).RequireAuthorization();

        // Срез-заглушка: подписка без ключей (pricing='free'); ключи SCCAT/suspend/consent — C1 (ADR-076 §3).
        app.MapPost("/v1/catalogs/{slug}/subscribe", async (
            string slug,
            ClaimsPrincipal principal,
            CloudDbContext db,
            CancellationToken ct) =>
        {
            var user = await ResolveUserAsync(db, principal, ct);
            if (user is null) return Results.Unauthorized();

            var catalog = await db.Catalogs.FirstOrDefaultAsync(c => c.Slug == slug, ct);
            if (catalog is null) return NotFound(slug);

            var existing = await db.Subscriptions.FirstOrDefaultAsync(s => s.CatalogId == catalog.Id && s.UserId == user.Id, ct);
            if (existing is not null) return Results.Ok(new { subscriptionId = existing.Id, restored = true });

            var subscription = new Subscription { CatalogId = catalog.Id, UserId = user.Id };
            db.Subscriptions.Add(subscription);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { subscriptionId = subscription.Id, restored = false });
        }).RequireAuthorization();

        return app;
    }

    private static async Task<CloudUser?> ResolveUserAsync(CloudDbContext db, ClaimsPrincipal principal, CancellationToken ct)
    {
        var sub = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub");
        if (!Guid.TryParse(sub, out var userId)) return null;
        return await db.Users.OfType<CloudUser>().FirstOrDefaultAsync(u => u.Id == userId, ct);
    }

    private static IResult NotFound(string slug) =>
        Results.Problem(statusCode: 404, title: "каталог не существует",
            detail: $"'{slug}' не найден (404-анти-оракул: для чужих приватных каталогов ответ тот же, ADR-076 §4)");

    /// <summary>Кириллица → латиница (транслит без внешних пакетов). Пустые значения
    /// ('ъ','ь') схлопываются соседними '-'/буквами.</summary>
    private static readonly Dictionary<char, string> Translit = new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d", ['е'] = "e", ['ё'] = "e",
        ['ж'] = "zh", ['з'] = "z", ['и'] = "i", ['й'] = "y", ['к'] = "k", ['л'] = "l", ['м'] = "m",
        ['н'] = "n", ['о'] = "o", ['п'] = "p", ['р'] = "r", ['с'] = "s", ['т'] = "t", ['у'] = "u",
        ['ф'] = "f", ['х'] = "h", ['ц'] = "ts", ['ч'] = "ch", ['ш'] = "sh", ['щ'] = "sch",
        ['ъ'] = "", ['ы'] = "y", ['ь'] = "", ['э'] = "e", ['ю'] = "yu", ['я'] = "ya",
    };

    private static string Slugify(string name)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var raw in name.Trim().ToLowerInvariant())
        {
            var c = raw;
            if (char.IsAsciiLetterOrDigit(c))
            {
                sb.Append(c);
                continue;
            }
            if (Translit.TryGetValue(c, out var latin))
            {
                sb.Append(latin);
                continue;
            }
            sb.Append('-');
        }
        var slug = sb.ToString();
        // Схлопнуть '-'- серии и обрезать по краям (в т.ч. от пустых транслит-значений).
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        slug = slug.Trim('-');
        if (slug.Length > 48) slug = slug[..48].Trim('-');
        return slug.Length == 0 ? "catalog" : slug;
    }

    /// <summary>Рекурсивно собирает все значения свойств "sha256" (срез: манифест принимается как есть;
    /// полная серверная валидация §6.3 — C1).</summary>
    private static IEnumerable<string> CollectSha256(JsonElement node)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in node.EnumerateObject())
                {
                    if (prop.Name == "sha256"
                        && prop.Value.ValueKind == JsonValueKind.String
                        && prop.Value.GetString() is { Length: 64 } hex
                        && hex.All(char.IsAsciiHexDigit))
                        yield return hex.ToLowerInvariant();
                    else
                        foreach (var nested in CollectSha256(prop.Value)) yield return nested;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in node.EnumerateArray())
                    foreach (var nested in CollectSha256(item)) yield return nested;
                break;
        }
    }

    private static int GetPropertyOr(this JsonElement element, string name, int fallback) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : fallback;

    private static long GetPropertyOr(this JsonElement element, string name, long fallback) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : fallback;

    private static JsonValueKind GetPropertyOr(this JsonElement element, string name, JsonValueKind fallback) =>
        element.TryGetProperty(name, out var v) ? v.ValueKind : fallback;

    private static IEnumerable<JsonElement> EnumerateArrayOr(this JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];

    public sealed record CreateCatalogRequest(string Name);
    public sealed record PublishRequest(JsonElement Manifest);
    public sealed record CatalogDto(Guid Id, string Slug, string Name, long CurrentPublishSeq);
    public sealed record LatestManifestResponse(long PublishSeq, long ManifestSizeBytes, JsonElement Manifest);
}
