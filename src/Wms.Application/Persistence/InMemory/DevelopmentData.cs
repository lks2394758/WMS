using Wms.Application.Auth;
using Wms.Domain.Catalog;
using Wms.Domain.Common;
using Wms.Domain.Identity;
using Wms.Persistence.Abstractions;

namespace Wms.Application.Persistence.InMemory;

/// <summary>What one seeding run changed. Both counts are 0 on a restart against an already seeded database.</summary>
public sealed record DevelopmentSeedReport(int RecordsInserted, int PasswordsSet, bool PasswordConfigured);

/// <summary>
/// Fixed catalog and one user per role for development, the SQLite demonstration database and API tests. IDs are
/// stable across restarts so clients can keep them. No stock is seeded: it enters through receipts. No password is
/// stored in source: without one, the users exist but cannot sign in.
/// <para>
/// Seeding is idempotent and goes through <see cref="ISeedStore"/>, so it works on every provider. The first run
/// inserts the catalog and users. Later runs insert only what is missing and never touch balances, the ledger or
/// existing rows: a changed password, name or flag survives a restart. The one exception is explicit: a seeded user
/// that was created without a password (unusable hash) receives the configured password once it is set.
/// </para>
/// </summary>
public static class DevelopmentData
{
    public static readonly WarehouseId Warehouse = new(Guid.Parse("3f6c1f2e-9a4b-4c1d-8e2f-1a2b3c4d5e01"));

    public static readonly LocationId Receiving = new(Guid.Parse("7b1e2d3c-4a5b-4c6d-9e8f-0a1b2c3d4e01"));
    public static readonly LocationId ShelfA0101 = new(Guid.Parse("7b1e2d3c-4a5b-4c6d-9e8f-0a1b2c3d4e02"));
    public static readonly LocationId ShelfA0102 = new(Guid.Parse("7b1e2d3c-4a5b-4c6d-9e8f-0a1b2c3d4e03"));

    public static readonly ProductId Widget = new(Guid.Parse("5d2c3b4a-1e2f-4a3b-8c4d-5e6f7a8b9c04"));
    public static readonly ProductId Gadget = new(Guid.Parse("5d2c3b4a-1e2f-4a3b-8c4d-5e6f7a8b9c05"));

    public static readonly UserId Administrator = new(Guid.Parse("9e8d7c6b-5a4f-4e3d-8c2b-1a0f9e8d7c01"));
    public static readonly UserId Supervisor = new(Guid.Parse("9e8d7c6b-5a4f-4e3d-8c2b-1a0f9e8d7c02"));
    public static readonly UserId ReceivingClerk = new(Guid.Parse("9e8d7c6b-5a4f-4e3d-8c2b-1a0f9e8d7c03"));
    public static readonly UserId ShippingClerk = new(Guid.Parse("9e8d7c6b-5a4f-4e3d-8c2b-1a0f9e8d7c04"));

    /// <summary>Usernames of the seeded users, one per role.</summary>
    public static IReadOnlyDictionary<UserRole, string> Usernames { get; } = new Dictionary<UserRole, string>
    {
        [UserRole.SystemAdministrator] = "admin",
        [UserRole.WarehouseSupervisor] = "supervisor",
        [UserRole.ReceivingClerk] = "receiver",
        [UserRole.ShippingClerk] = "shipper",
    };

    /// <summary>The seeded users' IDs.</summary>
    public static IReadOnlyList<UserId> UserIds { get; } = [Administrator, Supervisor, ReceivingClerk, ShippingClerk];

    /// <param name="store">The provider's seed port.</param>
    /// <param name="hasher">Hashes <paramref name="password"/>.</param>
    /// <param name="password">Shared password of the seeded users, from configuration; null or empty disables sign-in.</param>
    /// <param name="cancellationToken">Cancels the run; inserts are atomic, so nothing partial is stored.</param>
    public static async Task<DevelopmentSeedReport> SeedAsync(
        ISeedStore store, IPasswordHasher hasher, string? password, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(hasher);

        var configured = !string.IsNullOrEmpty(password);
        var passwordHash = configured ? hasher.Hash(password!) : hasher.UnusableHash;
        var inserted = await store.AddMissingAsync(Build(passwordHash), cancellationToken).ConfigureAwait(false);
        var passwordsSet = configured
            ? await store.ReplacePasswordHashAsync(UserIds, hasher.UnusableHash, passwordHash, cancellationToken).ConfigureAwait(false)
            : 0;
        return new DevelopmentSeedReport(inserted, passwordsSet, configured);
    }

    private static SeedData Build(string passwordHash) => new(
        [
            Location.Create(Receiving, Warehouse, "RCV-01").Value,
            Location.Create(ShelfA0101, Warehouse, "A-01-01").Value,
            Location.Create(ShelfA0102, Warehouse, "A-01-02").Value,
        ],
        [
            Product.Create(Widget, "WIDGET-001", "Widget", "EA", capacity: 100).Value,
            Product.Create(Gadget, "GADGET-001", "Gadget", "EA").Value,
        ],
        [
            Barcode.Create("4006381333931", Widget).Value,
            Barcode.Create("9780201379624", Gadget).Value,
        ],
        [
            new UserAccount(Administrator, Usernames[UserRole.SystemAdministrator], "System Administrator", UserRole.SystemAdministrator, passwordHash),
            new UserAccount(Supervisor, Usernames[UserRole.WarehouseSupervisor], "Warehouse Supervisor", UserRole.WarehouseSupervisor, passwordHash),
            new UserAccount(ReceivingClerk, Usernames[UserRole.ReceivingClerk], "Receiving Clerk", UserRole.ReceivingClerk, passwordHash),
            new UserAccount(ShippingClerk, Usernames[UserRole.ShippingClerk], "Shipping Clerk", UserRole.ShippingClerk, passwordHash),
        ]);
}
