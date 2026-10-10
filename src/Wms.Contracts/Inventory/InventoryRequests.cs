namespace Wms.Contracts.Inventory;

/// <summary>One product and a quantity greater than zero.</summary>
public sealed record QuantityLine(Guid ProductId, int Quantity);

/// <summary>One product and the physically counted quantity (zero allowed).</summary>
public sealed record CountLine(Guid ProductId, int CountedQuantity);

/// <summary>One product and a signed, non-zero correction.</summary>
public sealed record AdjustmentLine(Guid ProductId, int QuantityDelta);

public sealed record ReceiptRequest(Guid LocationId, IReadOnlyList<QuantityLine> Lines, string? ReferenceNo = null);

public sealed record MovementRequest(
    Guid FromLocationId,
    Guid ToLocationId,
    IReadOnlyList<QuantityLine> Lines,
    string? ReferenceNo = null);

public sealed record ShipmentRequest(Guid LocationId, IReadOnlyList<QuantityLine> Lines, string? ReferenceNo = null);

public sealed record CountRequest(Guid LocationId, string Reason, IReadOnlyList<CountLine> Lines);

public sealed record AdjustmentRequest(
    Guid LocationId,
    string Reason,
    IReadOnlyList<AdjustmentLine> Lines,
    string? ReferenceNo = null);
