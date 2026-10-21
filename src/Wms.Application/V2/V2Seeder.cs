using Wms.Application.Auth;
using Wms.Application.V2.Auth;
using Wms.Contracts.V2.Auth;
using Wms.Contracts.V2.Common;
using Wms.Persistence.Abstractions.V2;

namespace Wms.Application.V2;

/// <summary>Result of <see cref="V2Seeder.SeedAsync"/>. Never contains a password or hash.</summary>
public sealed record V2SeedOutcome(V2SeedReport Report, IReadOnlyList<string> AccountsWaitingForPassword, bool PasswordRejected);

/// <summary>
/// Insert-missing initialization of a new v2 database: one account per role, the preset unit "件" and the default
/// settings. No products, bins, stock or transactions. Existing rows (accounts and their passwords included) are never
/// changed, so a restart never resets anything. Accounts are created only when <c>V2:Seed:Password</c> is configured
/// (8-256 characters); otherwise they are reported as waiting and created on a later start.
/// </summary>
public sealed class V2Seeder(IV2SeedStore store, IPasswordHasher hasher, TimeProvider clock)
{
    public const string DefaultUnitName = "件";
    public const int DefaultRackLevels = 4;
    public const int DefaultCellsPerLevel = 6;
    public const long DefaultLowThresholdMicros = 20 * WireQuantity.MicrosPerUnit;
    public const long DefaultHighThresholdMicros = 80 * WireQuantity.MicrosPerUnit;
    public const int DefaultSessionHours = 8;
    public const int MinPasswordLength = 8;
    public const int MaxPasswordLength = 256;

    public static IReadOnlyList<(string Username, string DisplayName, string Role)> Accounts { get; } =
    [
        ("admin", "系统管理员", RoleNames.SystemAdministrator),
        ("supervisor", "仓库主管", RoleNames.WarehouseSupervisor),
        ("receiver", "收货员", RoleNames.ReceivingClerk),
        ("shipper", "发货员", RoleNames.ShippingClerk),
    ];

    public async Task<V2SeedOutcome> SeedAsync(string? password, CancellationToken cancellationToken)
    {
        var existing = await store.ExistingUsernamesAsync(
            Accounts.Select(a => V2Usernames.Normalize(a.Username)).ToList(), cancellationToken);
        var missing = Accounts.Where(a => !existing.Contains(V2Usernames.Normalize(a.Username))).ToList();

        var passwordUsable = password is { Length: >= MinPasswordLength and <= MaxPasswordLength } && !string.IsNullOrWhiteSpace(password);
        var users = passwordUsable
            ? missing.Select(a => new V2SeedUser(a.Username, a.DisplayName, [a.Role], hasher.Hash(password!))).ToList()
            : [];

        var report = await store.SeedAsync(
            new V2SeedRequest(
                users,
                DefaultUnitName,
                DefaultRackLevels,
                DefaultCellsPerLevel,
                DefaultLowThresholdMicros,
                DefaultHighThresholdMicros,
                DefaultSessionHours,
                clock.GetUtcNow()),
            cancellationToken);

        return new V2SeedOutcome(
            report,
            passwordUsable ? [] : missing.Select(a => a.Username).ToList(),
            PasswordRejected: !passwordUsable && !string.IsNullOrEmpty(password));
    }
}
