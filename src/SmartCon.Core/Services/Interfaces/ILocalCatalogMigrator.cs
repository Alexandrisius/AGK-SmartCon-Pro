namespace SmartCon.Core.Services.Interfaces;

public interface ILocalCatalogMigrator
{
    Task MigrateAsync(CancellationToken ct = default);
}
