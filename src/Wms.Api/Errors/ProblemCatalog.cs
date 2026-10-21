using Wms.Contracts.Common;

namespace Wms.Api.Errors;

/// <summary>HTTP status, title and type URI of each stable problem code (docs/api-conventions.md).</summary>
internal static class ProblemCatalog
{
    private const string TypeBase = "https://wms-api.company.intra/problems/";

    public static int StatusOf(string code) => code switch
    {
        ProblemCodes.ValidationFailed => StatusCodes.Status400BadRequest,
        ProblemCodes.Unauthenticated => StatusCodes.Status401Unauthorized,
        ProblemCodes.Forbidden => StatusCodes.Status403Forbidden,
        ProblemCodes.NotFound or ProblemCodes.ProductNotFound or ProblemCodes.LocationNotFound => StatusCodes.Status404NotFound,
        ProblemCodes.ProductInactive or ProblemCodes.LocationInactive => StatusCodes.Status422UnprocessableEntity,
        ProblemCodes.BarcodeAmbiguous or ProblemCodes.InsufficientStock or ProblemCodes.ConcurrencyConflict
            or ProblemCodes.IdempotencyConflict => StatusCodes.Status409Conflict,
        ProblemCodes.RateLimited => StatusCodes.Status429TooManyRequests,
        _ => StatusCodes.Status500InternalServerError,
    };

    public static string TitleOf(string code) => code switch
    {
        ProblemCodes.ValidationFailed => "Validation failed",
        ProblemCodes.Unauthenticated => "Authentication required",
        ProblemCodes.Forbidden => "Permission denied",
        ProblemCodes.NotFound => "Resource not found",
        ProblemCodes.ProductNotFound => "Product not found",
        ProblemCodes.LocationNotFound => "Location not found",
        ProblemCodes.ProductInactive => "Product is disabled",
        ProblemCodes.LocationInactive => "Location is disabled",
        ProblemCodes.BarcodeAmbiguous => "Barcode is ambiguous",
        ProblemCodes.InsufficientStock => "Insufficient available stock",
        ProblemCodes.ConcurrencyConflict => "Concurrent change",
        ProblemCodes.IdempotencyConflict => "Idempotency key reused",
        ProblemCodes.RateLimited => "Too many requests",
        _ => "Internal error",
    };

    public static string TypeOf(string code) => TypeBase + code.ToLowerInvariant().Replace('_', '-');
}
