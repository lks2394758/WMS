namespace Wms.Contracts.V2.Admin;

/// <summary><c>GET /api/v2/users</c> row and <c>GET /api/v2/users/{id}</c>. Never carries a password or credential.</summary>
public sealed record UserResponse(
    string Id,
    string Username,
    string DisplayName,
    IReadOnlyList<string> Roles,
    bool Enabled,
    bool HasActiveSession,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int Version);

/// <summary><c>POST /api/v2/users</c>. The password is hashed and never returned or logged.</summary>
public sealed record UserCreateRequest(
    string Username,
    string DisplayName,
    IReadOnlyList<string> Roles,
    bool Enabled,
    string Password);

/// <summary>
/// <c>PUT /api/v2/users/{id}</c>. Disabling the account or changing its roles revokes its session immediately.
/// </summary>
public sealed record UserUpdateRequest(
    string Username,
    string DisplayName,
    IReadOnlyList<string> Roles,
    bool Enabled,
    int Version);

/// <summary><c>POST /api/v2/users/{id}/reset-password</c>; revokes the account's session.</summary>
public sealed record ResetPasswordRequest(string Password, int? Version = null);

/// <summary><c>GET /api/v2/settings</c>, also returned by <c>PUT</c>.</summary>
public sealed record SettingsResponse(
    int DefaultRackLevels,
    int DefaultCellsPerLevel,
    string LowThreshold,
    string HighThreshold,
    int SessionHours,
    int Version,
    DateTimeOffset? UpdatedAt = null,
    string? UpdatedById = null,
    string? UpdatedByName = null);

/// <summary><c>PUT /api/v2/settings</c> (administrator only).</summary>
public sealed record SettingsUpdateRequest(
    int DefaultRackLevels,
    int DefaultCellsPerLevel,
    string LowThreshold,
    string HighThreshold,
    int SessionHours,
    int Version);
