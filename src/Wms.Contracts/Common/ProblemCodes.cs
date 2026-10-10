namespace Wms.Contracts.Common;

/// <summary>Stable machine-readable error codes (guide §7.5). Never rename or reuse a code within v1.</summary>
public static class ProblemCodes
{
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string Unauthenticated = "UNAUTHENTICATED";
    public const string Forbidden = "FORBIDDEN";
    public const string NotFound = "NOT_FOUND";
    public const string ProductNotFound = "PRODUCT_NOT_FOUND";
    public const string LocationNotFound = "LOCATION_NOT_FOUND";
    public const string ProductInactive = "PRODUCT_INACTIVE";
    public const string LocationInactive = "LOCATION_INACTIVE";
    public const string BarcodeAmbiguous = "BARCODE_AMBIGUOUS";
    public const string InsufficientStock = "INSUFFICIENT_STOCK";
    public const string ConcurrencyConflict = "CONCURRENCY_CONFLICT";
    public const string IdempotencyConflict = "IDEMPOTENCY_CONFLICT";
    public const string InternalError = "INTERNAL_ERROR";

    public static IReadOnlyList<string> All { get; } =
    [
        ValidationFailed, Unauthenticated, Forbidden, NotFound, ProductNotFound, LocationNotFound,
        ProductInactive, LocationInactive, BarcodeAmbiguous, InsufficientStock, ConcurrencyConflict,
        IdempotencyConflict, InternalError,
    ];
}
