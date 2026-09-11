// SPDX-License-Identifier: Business Source License 1.1 (see server/LICENSE); converts to Apache-2.0 after Change Date

namespace SmartCon.Cloud.Api.Domain;

using Microsoft.AspNetCore.Identity;

/// <summary>Аккаунт SmartCon Cloud (ADR-076 §1: ASP.NET Core Identity, самопанная auth запрещена).</summary>
public sealed class CloudUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Каталог (server/ схема §6.1). Вертикальный срез: без visibility/pricing/модерации.</summary>
public sealed class Catalog
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public string Slug { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public long CurrentPublishSeq { get; set; }
    public int HashFormatVersion { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Publish point: атомарный неизменяемый снапшот (ADR-075 §2).</summary>
public sealed class PublishPoint
{
    public Guid CatalogId { get; set; }
    public long Seq { get; set; }
    public string ManifestJson { get; set; } = string.Empty;
    public long ManifestSizeBytes { get; set; }
    public Guid PublishedBy { get; set; }
    public DateTime PublishedAtUtc { get; set; } = DateTime.UtcNow;
    public int ChangeCount { get; set; }
}

/// <summary>CAS-объект: файл, адресованный SHA-256 содержимого (ADR-075 §3).</summary>
public sealed class CasObject
{
    public string Sha256 { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime FirstSeenAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Ссылка publish point → CAS-объект (GC/дедуп C1).</summary>
public sealed class PublishPointFile
{
    public Guid CatalogId { get; set; }
    public long Seq { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}

/// <summary>Refresh-токен с ротацией и reuse-детектом (ADR-076 §2).</summary>
public sealed class RefreshToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
    public Guid? ReplacedBy { get; set; }
}

/// <summary>Подписка (stub среза: без ключей/статусов/suspend — C1).</summary>
public sealed class Subscription
{
    public Guid Id { get; set; }
    public Guid CatalogId { get; set; }
    public Guid UserId { get; set; }
    public DateTime ActivatedAtUtc { get; set; } = DateTime.UtcNow;
}
