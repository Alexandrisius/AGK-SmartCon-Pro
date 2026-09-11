// SPDX-License-Identifier: Business Source License 1.1 (see server/LICENSE); converts to Apache-2.0 after Change Date

namespace SmartCon.Cloud.Api.Data;

using Domain;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

public sealed class CloudDbContext(DbContextOptions<CloudDbContext> options)
    : IdentityUserContext<CloudUser, Guid>(options) // .NET 10: роли удалены из Identity; Guid-ключ без ролей = IdentityUserContext
{
    public DbSet<Catalog> Catalogs => Set<Catalog>();
    public DbSet<PublishPoint> PublishPoints => Set<PublishPoint>();
    public DbSet<CasObject> CasObjects => Set<CasObject>();
    public DbSet<PublishPointFile> PublishPointFiles => Set<PublishPointFile>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<Catalog>(e =>
        {
            e.ToTable("catalogs");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Slug).IsUnique();
            e.Property(x => x.Slug).HasMaxLength(64);
            e.Property(x => x.Name).HasMaxLength(200);
        });

        b.Entity<PublishPoint>(e =>
        {
            e.ToTable("publish_points");
            e.HasKey(x => new { x.CatalogId, x.Seq });
        });

        b.Entity<CasObject>(e =>
        {
            e.ToTable("cas_objects");
            e.HasKey(x => x.Sha256);
        });

        b.Entity<PublishPointFile>(e =>
        {
            e.ToTable("publish_point_files");
            e.HasKey(x => new { x.CatalogId, x.Seq, x.Sha256 });
        });

        b.Entity<RefreshToken>(e =>
        {
            e.ToTable("auth_tokens");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TokenHash).IsUnique();
        });

        b.Entity<Subscription>(e =>
        {
            e.ToTable("subscriptions");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.CatalogId, x.UserId }).IsUnique();
        });
    }
}
