namespace Wms.Domain.Common;

/// <summary>
/// A business-rule failure. <see cref="Code"/> matches the stable API problem code. <see cref="Field"/> optionally
/// names the request field (wire name) that caused a validation failure.
/// </summary>
public sealed record DomainError(string Code, string Message, string? Field = null);

/// <summary>Error codes the domain can raise; identical strings to the API error contract (guide §7.5).</summary>
public static class DomainErrorCodes
{
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string ProductInactive = "PRODUCT_INACTIVE";
    public const string LocationInactive = "LOCATION_INACTIVE";
    public const string InsufficientStock = "INSUFFICIENT_STOCK";
}

public static class DomainErrors
{
    public static DomainError Validation(string message, string? field = null) =>
        new(DomainErrorCodes.ValidationFailed, message, field);

    public static DomainError ProductInactive(string sku) =>
        new(DomainErrorCodes.ProductInactive, $"Product {sku} is disabled and cannot be used for inventory commands.");

    public static DomainError LocationInactive(string code) =>
        new(DomainErrorCodes.LocationInactive, $"Location {code} is disabled and cannot be used for inventory commands.");

    public static DomainError InsufficientStock(string locationCode, int available, int requested) =>
        new(DomainErrorCodes.InsufficientStock,
            $"Location {locationCode} has {available} available units; {requested} cannot be removed.");
}
