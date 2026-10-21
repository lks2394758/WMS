using Wms.Contracts.Common;
using Wms.Domain.Common;

namespace Wms.Application.Common;

/// <summary>Use-case failures that are not domain rules. Codes are the stable API problem codes.</summary>
public static class ApplicationErrors
{
    public static DomainError ProductNotFound(Guid id) =>
        new(ProblemCodes.ProductNotFound, $"Product {id} does not exist.");

    public static DomainError LocationNotFound(Guid id) =>
        new(ProblemCodes.LocationNotFound, $"Location {id} does not exist.");

    public static DomainError BarcodeNotFound(string value) =>
        new(ProblemCodes.ProductNotFound, $"No product has the active barcode '{value}'.");

    public static DomainError BarcodeAmbiguous(string value) =>
        new(ProblemCodes.BarcodeAmbiguous, $"Barcode '{value}' belongs to more than one product.");

    public static DomainError IdempotencyConflict() =>
        new(ProblemCodes.IdempotencyConflict,
            $"This {ApiHeaders.IdempotencyKey} was already used for a different request. Generate a new key for a new action.");

    public static DomainError ConcurrencyConflict() =>
        new(ProblemCodes.ConcurrencyConflict,
            $"Inventory is busy with another change and nothing was stored. Retry with the same {ApiHeaders.IdempotencyKey}.");

    public static DomainError Unauthenticated(string message) => new(ProblemCodes.Unauthenticated, message);
}
