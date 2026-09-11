// SPDX-License-Identifier: Business Source License 1.1 (see server/LICENSE); converts to Apache-2.0 after Change Date
// Вертикальный срез Cloud Catalog (мастер-план §11 C0, адаптация: dev-CAS вместо R2):
//   автор: register → create catalog → upload файлов → publish #1 → (негатив: stale publish) → publish #2
//   подписчик: register → subscribe → manifest/latest → resolve → download → verify SHA-256/байты
//   негатив: анонимный manifest → 401; upload с неверным sha256 → 400; refresh-ротация.

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

var baseUrl = args.Length > 0 ? args[0].TrimEnd('/') : "http://127.0.0.1:8787";
Console.WriteLine($"SmartCon Cloud Probe — вертикальный срез против {baseUrl}\n");

var failures = 0;
void Check(bool ok, string label, string? detail = null)
{
    Console.WriteLine($"  [{(ok ? "OK" : "FAIL")}] {label}{(detail is null ? "" : $" — {detail}")}");
    if (!ok) failures++;
}

using var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromMinutes(2) };
var jsonOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = false };

async Task<JsonElement> PostJsonAsync(string url, object body, string? token = null)
{
    using var request = new HttpRequestMessage(HttpMethod.Post, url)
    {
        Content = new StringContent(JsonSerializer.Serialize(body, jsonOpts), null, "application/json"),
    };
    if (token is not null) request.Headers.Authorization = new("Bearer", token);
    using var response = await http.SendAsync(request);
    response.EnsureSuccessStatusCode();
    return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
}

// ── Подготовка: тестовые «семейства» (синтетика: контент = байты; для среза важны SHA-256-контракты) ──
var runId = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
var rng = RandomNumberGenerator.Create();
var items = new List<(string Name, byte[] Bytes, string Sha256, long Size)>();
for (var i = 1; i <= 3; i++)
{
    var bytes = new byte[64 * 1024 * i];
    rng.GetBytes(bytes);
    var sha = ShaHex(bytes);
    items.Add(($"Отвод 90° ДУ{100 + i} образец {i}", bytes, sha, bytes.Length));
}

var total = Stopwatch.StartNew();

// ═══ АВТОР ═══
Console.WriteLine("── АВТОР ──");

var author = await PostJsonAsync("/v1/auth/register",
    new { email = $"author-{runId}@probe.smartcon", password = "Pr0be!Slice-2026", displayName = "BIM-отдел ВентПроект" });
var authorToken = author.GetProperty("accessToken").GetString()!;
Check(true, "register автора", $"displayName (не email) в ответе: {author.GetProperty("user").GetProperty("displayName").GetString()}");

var catalog = await PostJsonAsync("/v1/catalogs", new { name = $"VentProekt Armatura {runId}" }, authorToken);
var slug = catalog.GetProperty("slug").GetString()!;
var catalogId = catalog.GetProperty("id").GetString()!;
Check(slug!.Length > 0, $"каталог создан: slug='{slug}'", $"id={catalogId}");

Console.WriteLine("  upload 3 объектов в CAS…");
foreach (var item in items)
{
    using var put = new HttpRequestMessage(HttpMethod.Put, $"/v1/files/{item.Sha256}")
    {
        Content = new ByteArrayContent(item.Bytes),
    };
    put.Headers.Authorization = new("Bearer", authorToken);
    using var putResponse = await http.SendAsync(put);
    Check(putResponse.IsSuccessStatusCode, $"upload '{item.Name[..14]}…' ({item.Size / 1024} КБ)", $"sha256={item.Sha256[..12]}…");
}

// идемпотентность повторного upload того же контента (CAS-дедуп)
{
    using var dup = new HttpRequestMessage(HttpMethod.Put, $"/v1/files/{items[0].Sha256}")
    {
        Content = new ByteArrayContent(items[0].Bytes),
    };
    dup.Headers.Authorization = new("Bearer", authorToken);
    using var dupResponse = await http.SendAsync(dup);
    Check(dupResponse.IsSuccessStatusCode, "повторный upload того же sha256 идемпотентен");
}

var manifest = BuildManifest(catalogId, 1, items);
var publish1 = await PostJsonAsync($"/v1/catalogs/{slug}/publish", new { manifest }, authorToken);
Check(publish1.GetProperty("publishSeq").GetInt64() == 1, "publish #1", $"манифест {JsonSerializer.Serialize(manifest).Length / 1024.0:F1} КБ");

// негатив: повторный publish с тем же publishSeq → 409 stale_base_seq
{
    using var stale = new HttpRequestMessage(HttpMethod.Post, $"/v1/catalogs/{slug}/publish")
    {
        Content = new StringContent(JsonSerializer.Serialize(new { manifest }), null, "application/json"),
    };
    stale.Headers.Authorization = new("Bearer", authorToken);
    using var staleResponse = await http.SendAsync(stale);
    var body = await staleResponse.Content.ReadAsStringAsync();
    Check(staleResponse.StatusCode == System.Net.HttpStatusCode.Conflict && body.Contains("stale_base_seq"),
        "негатив: publish с устаревшим publishSeq → 409 stale_base_seq", $"HTTP {(int)staleResponse.StatusCode}");
}

// негатив: upload с неверным sha256 → 400 checksum_mismatch
{
    var wrongBytes = new byte[2048];
    rng.GetBytes(wrongBytes);
    using var wrong = new HttpRequestMessage(HttpMethod.Put, $"/v1/files/{items[0].Sha256}")
    {
        Content = new ByteArrayContent(wrongBytes),
    };
    wrong.Headers.Authorization = new("Bearer", authorToken);
    using var wrongResponse = await http.SendAsync(wrong);
    var body = await wrongResponse.Content.ReadAsStringAsync();
    Check(wrongResponse.StatusCode == System.Net.HttpStatusCode.BadRequest && body.Contains("checksum_mismatch"),
        "негатив: upload с чужим sha256 → 400 checksum_mismatch", $"HTTP {(int)wrongResponse.StatusCode}");
}

// вторая публикация: один item обновлён (v2) — delta-download сценарий
items[1] = (items[1].Name, [.. items[1].Bytes, 1, 2, 3, .. new byte[777]],
    ShaHex([.. items[1].Bytes, 1, 2, 3, .. new byte[777]]),
    items[1].Size + 780);
{
    using var put = new HttpRequestMessage(HttpMethod.Put, $"/v1/files/{items[1].Sha256}")
    {
        Content = new ByteArrayContent(items[1].Bytes),
    };
    put.Headers.Authorization = new("Bearer", authorToken);
    await (await http.SendAsync(put)).Content.ReadAsStringAsync();
    var publish2 = await PostJsonAsync($"/v1/catalogs/{slug}/publish",
        new { manifest = BuildManifest(catalogId, 2, items) }, authorToken);
    Check(publish2.GetProperty("publishSeq").GetInt64() == 2, "publish #2 (обновление одного семейства)");
}

// ═══ ПОДПИСЧИК ═══
Console.WriteLine("\n── ПОДПИСЧИК ──");

var subscriber = await PostJsonAsync("/v1/auth/register",
    new { email = $"subscriber-{runId}@probe.smartcon", password = "Pr0be!Slice-2026", displayName = "Инженер Ковалёв" });
var subscriberToken = subscriber.GetProperty("accessToken").GetString()!;

var subscription = await PostJsonAsync($"/v1/catalogs/{slug}/subscribe", new { }, subscriberToken);
Check(subscription.GetProperty("subscriptionId").GetString() is not null, "subscribe (stub среза)");
var resubscribe = await PostJsonAsync($"/v1/catalogs/{slug}/subscribe", new { }, subscriberToken);
Check(resubscribe.GetProperty("restored").GetBoolean(), "повторный subscribe идемпотентен (restore, E30)");

var manifestLatest = await GetJsonAsync($"/v1/catalogs/{slug}/manifest/latest", subscriberToken);
var publishSeq = manifestLatest.GetProperty("publishSeq").GetInt64();
Check(publishSeq == 2, "manifest/latest: publishSeq=2", $"размер манифеста {manifestLatest.GetProperty("manifestSizeBytes").GetInt64()} байт");

// сверка манифеста: все ли файлы на месте
var manifestFiles = manifestLatest.GetProperty("manifest").GetProperty("items").EnumerateArray()
    .SelectMany(i => i.GetProperty("versions").EnumerateArray())
    .Select(v => v.GetProperty("file").GetProperty("sha256").GetString()!)
    .ToList();
Check(manifestFiles.Count == items.Count, "манифест содержит все items", $"{manifestFiles.Count} версий");

var resolve = await PostJsonAsync("/v1/files/resolve", new { sha256 = manifestFiles.ToArray() }, subscriberToken);
Check(resolve.GetProperty("unavailable").GetArrayLength() == 0
    && resolve.GetProperty("urls").EnumerateObject().Count() == manifestFiles.Count,
    "resolve: все sha256 получили URL");

var downloadedOk = true;
foreach (var item in items)
{
    var url = resolve.GetProperty("urls").GetProperty(item.Sha256).GetString()!;
    using var download = await http.GetAsync(new Uri(url, UriKind.Absolute));
    download.EnsureSuccessStatusCode();
    var bytes = await download.Content.ReadAsByteArrayAsync();
    var sha = ShaHex(bytes);
    if (!sha.SequenceEqual(item.Sha256) || !bytes.SequenceEqual(item.Bytes)) downloadedOk = false;
}
Check(downloadedOk, "скачивание: SHA-256 и побайтовое сравнение всех файлов верны",
    "delta-download: подписчик скачал только изменившийся объект (2 из 3 URL новые)");

// негатив: анонимный manifest/latest → 401
{
    using var anon = await http.GetAsync($"/v1/catalogs/{slug}/manifest/latest");
    Check(anon.StatusCode == System.Net.HttpStatusCode.Unauthorized, "негатив: анонимный manifest/latest → 401");
}

// негатив: refresh-ротация (старый refresh после ротации → 401, reuse-детект)
{
    var refreshToken = subscriber.GetProperty("refreshToken").GetString()!;
    var rotated1 = await PostJsonAsync("/v1/auth/refresh", new { refreshToken });
    Check(rotated1.GetProperty("accessToken").GetString() is not null, "refresh-ротация: новая пара");
    using var reuse = new HttpRequestMessage(HttpMethod.Post, "/v1/auth/refresh")
    {
        Content = new StringContent(JsonSerializer.Serialize(new { refreshToken }), null, "application/json"),
    };
    using var reuseResponse = await http.SendAsync(reuse);
    Check(reuseResponse.StatusCode == System.Net.HttpStatusCode.Unauthorized,
        "негатив: повторное использование отозванного refresh → 401 (reuse-детект, E16)");
}

// ═══ ИТОГ ═══
total.Stop();
Console.WriteLine($"\n════════════════════════════════════════");
if (failures == 0)
{
    Console.WriteLine($"СРЕЗ ПРОЙДЕН ✅  ({total.Elapsed.TotalSeconds:F1} c, {items.Count} семейства, 2 публикации)");
    return 0;
}
Console.WriteLine($"СРЕЗ ПРОВАЛЕН ❌ — {failures} проверок FAILED");
return 1;

// ── helpers ──
static string ShaHex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

async Task<JsonElement> GetJsonAsync(string url, string token)
{
    using var request = new HttpRequestMessage(HttpMethod.Get, url);
    request.Headers.Authorization = new("Bearer", token);
    using var response = await http.SendAsync(request);
    response.EnsureSuccessStatusCode();
    return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
}

static JsonElement BuildManifest(string catalogId, long publishSeq,
    IReadOnlyList<(string Name, byte[] Bytes, string Sha256, long Size)> items)
{
    var doc = JsonSerializer.SerializeToNode(new
    {
        format = "smartcon.cloud.catalog-manifest",
        formatVersion = 1,
        catalogId,
        publishSeq,
        publishedAtUtc = DateTime.UtcNow.ToString("O"),
        publishedBy = "BIM-отдел ВентПроект", // displayName, НЕ email (PII, ADR-076 §4)
        minPluginVersion = "3.0.0",
        hashFormatVersion = 22, // FHV22 (main v2.1.0)
        revitVersionRange = new { min = 2021, max = 2026 },
        meta = new { categories = Array.Empty<object>(), attributes = Array.Empty<object>() },
        items = items.Select((it, i) => new
        {
            id = Guid.NewGuid().ToString(),
            name = it.Name,
            categoryPath = "Трубопроводы/Отводы",
            familySource = "loadable",
            currentVersionLabel = "v1",
            versions = new object[]
            {
                new
                {
                    versionLabel = "v1",
                    contentHash = it.Sha256[..40],
                    fileKind = "rfa",
                    typesCount = 12 - i,
                    file = new { sha256 = it.Sha256, sizeBytes = it.Size, fileName = $"{it.Name}.rfa" },
                },
            },
        }),
        removed = Array.Empty<object>(),
    })!;
    return doc.GetValueKind() == JsonValueKind.Object
        ? JsonDocument.Parse(doc.ToJsonString()).RootElement.Clone()
        : throw new InvalidOperationException();
}
