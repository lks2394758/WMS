using Wms.Domain.Catalog;
using Wms.Domain.Common;

namespace Wms.Persistence.Abstractions;

/// <summary>Reference data to seed: catalog and users. Never stock: stock enters through receipts.</summary>
public sealed record SeedData(
    IReadOnlyList<Location> Locations,
    IReadOnlyList<Product> Products,
    IReadOnlyList<Barcode> Barcodes,
    IReadOnlyList<UserAccount> Users);

/// <summary>
/// Idempotent seeding. A record is inserted only if neither its ID nor its natural key (SKU, location code, barcode
/// value, username) is stored yet; existing records are never updated, so a restart does not reset changed data.
/// </summary>
public interface ISeedStore
{
    /// <summary>Inserts the missing records atomically and returns how many were inserted.</summary>
    Task<int> AddMissingAsync(SeedData data, CancellationToken cancellationToken);

    /// <summary>
    /// Sets <paramref name="newHash"/> on each listed user whose stored hash is exactly <paramref name="expectedHash"/>
    /// (used to give a password to seeded users that were created without one). Returns the number of users changed.
    /// </summary>
    Task<int> ReplacePasswordHashAsync(
        IReadOnlyCollection<UserId> userIds, string expectedHash, string newHash, CancellationToken cancellationToken);
}
