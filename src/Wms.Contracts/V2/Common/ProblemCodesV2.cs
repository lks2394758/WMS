namespace Wms.Contracts.V2.Common;

/// <summary>
/// Stable API v2 error codes and their HTTP status. Later phases only add codes; a code is never renamed,
/// reused or moved to another status within v2. See contracts/v2/README.md for when each is returned.
/// </summary>
public static class ProblemCodesV2
{
    // Transport, authentication and authorization
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string Unauthenticated = "UNAUTHENTICATED";
    public const string InvalidCredentials = "INVALID_CREDENTIALS";
    public const string SessionExpired = "SESSION_EXPIRED";
    public const string SessionRevoked = "SESSION_REVOKED";
    public const string Forbidden = "FORBIDDEN";
    public const string RoleNotAssigned = "ROLE_NOT_ASSIGNED";
    public const string NotFound = "NOT_FOUND";
    public const string VersionConflict = "VERSION_CONFLICT";
    public const string IdempotencyConflict = "IDEMPOTENCY_CONFLICT";
    public const string ConcurrencyConflict = "CONCURRENCY_CONFLICT";
    public const string PayloadTooLarge = "PAYLOAD_TOO_LARGE";
    public const string RateLimited = "RATE_LIMITED";
    public const string InternalError = "INTERNAL_ERROR";

    // Master data
    public const string AlreadyExists = "ALREADY_EXISTS";
    public const string InUse = "IN_USE";
    public const string Archived = "ARCHIVED";
    public const string StockNotZero = "STOCK_NOT_ZERO";
    public const string OpenCountOrInvestigation = "OPEN_COUNT_OR_INVESTIGATION";
    public const string InvalidImage = "INVALID_IMAGE";
    public const string LastAdministrator = "LAST_ADMINISTRATOR";
    public const string ThresholdConflict = "THRESHOLD_CONFLICT";

    // Inventory
    public const string BinFrozen = "BIN_FROZEN";
    public const string BinProductMismatch = "BIN_PRODUCT_MISMATCH";
    public const string InvestigationUnresolved = "INVESTIGATION_UNRESOLVED";
    public const string CapacityRequired = "CAPACITY_REQUIRED";
    public const string CapacityNotAllowed = "CAPACITY_NOT_ALLOWED";
    public const string InsufficientStock = "INSUFFICIENT_STOCK";
    public const string PrecisionExceeded = "PRECISION_EXCEEDED";
    public const string QuantityOutOfRange = "QUANTITY_OUT_OF_RANGE";
    public const string SerialNumbersRequired = "SERIAL_NUMBERS_REQUIRED";
    public const string SerialCountMismatch = "SERIAL_COUNT_MISMATCH";
    public const string SerialNotInBin = "SERIAL_NOT_IN_BIN";
    public const string SerialAlreadyInStock = "SERIAL_ALREADY_IN_STOCK";
    public const string SerialNumberSuperseded = "SERIAL_NUMBER_SUPERSEDED";
    public const string SerialTrackingDisabled = "SERIAL_TRACKING_DISABLED";

    // Counts and investigations
    public const string CountTaskExists = "COUNT_TASK_EXISTS";
    public const string CountBinHasNoProduct = "COUNT_BIN_HAS_NO_PRODUCT";
    public const string CountStateConflict = "COUNT_STATE_CONFLICT";
    public const string CountRoundAlreadySubmitted = "COUNT_ROUND_ALREADY_SUBMITTED";
    public const string TransactionNotEligible = "TRANSACTION_NOT_ELIGIBLE";

    public static IReadOnlyDictionary<string, int> HttpStatus { get; } = new Dictionary<string, int>
    {
        [ValidationFailed] = 400,
        [Unauthenticated] = 401,
        [InvalidCredentials] = 401,
        [SessionExpired] = 401,
        [SessionRevoked] = 401,
        [Forbidden] = 403,
        [RoleNotAssigned] = 403,
        [NotFound] = 404,
        [VersionConflict] = 409,
        [IdempotencyConflict] = 409,
        [ConcurrencyConflict] = 409,
        [PayloadTooLarge] = 413,
        [RateLimited] = 429,
        [InternalError] = 500,

        [AlreadyExists] = 409,
        [InUse] = 409,
        [Archived] = 422,
        [StockNotZero] = 409,
        [OpenCountOrInvestigation] = 409,
        [InvalidImage] = 422,
        [LastAdministrator] = 409,
        [ThresholdConflict] = 409,

        [BinFrozen] = 409,
        [BinProductMismatch] = 409,
        [InvestigationUnresolved] = 409,
        [CapacityRequired] = 422,
        [CapacityNotAllowed] = 422,
        [InsufficientStock] = 409,
        [PrecisionExceeded] = 422,
        [QuantityOutOfRange] = 422,
        [SerialNumbersRequired] = 422,
        [SerialCountMismatch] = 422,
        [SerialNotInBin] = 409,
        [SerialAlreadyInStock] = 409,
        [SerialNumberSuperseded] = 409,
        [SerialTrackingDisabled] = 422,

        [CountTaskExists] = 409,
        [CountBinHasNoProduct] = 422,
        [CountStateConflict] = 409,
        [CountRoundAlreadySubmitted] = 409,
        [TransactionNotEligible] = 422,
    };
}
