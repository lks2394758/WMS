using Wms.Domain.Common;

namespace Wms.Domain.V2.Inventory;

/// <summary>
/// Exact v2 quantities: micro-units (value × 1 000 000) in a 64-bit integer, never floating point. A single value is at
/// most <see cref="Max"/> (999 999 999 999.999 999); intermediate products use 128-bit integers and are never rounded.
/// </summary>
public static class V2Micro
{
    public const long PerUnit = 1_000_000;
    public const long Max = 999_999_999_999_999_999;

    /// <summary>The smallest step a product with <paramref name="decimalPlaces"/> (0-6) may use, in micro-units.</summary>
    public static long Step(int decimalPlaces) => decimalPlaces switch
    {
        0 => 1_000_000,
        1 => 100_000,
        2 => 10_000,
        3 => 1_000,
        4 => 100,
        5 => 10,
        6 => 1,
        _ => throw new ArgumentOutOfRangeException(nameof(decimalPlaces), decimalPlaces, "Decimal places are 0-6."),
    };

    /// <summary>True when the value has no more fraction digits than the product allows.</summary>
    public static bool FitsPrecision(long micros, int decimalPlaces) => micros % Step(decimalPlaces) == 0;

    /// <summary>
    /// Exact product of two micro-unit values (packaging quantity × conversion factor): 128-bit intermediate, no rounding.
    /// </summary>
    public static MultiplyOutcome Multiply(long a, long b, out long product)
    {
        product = 0;
        var raw = (Int128)a * b;
        if (raw % PerUnit != 0)
        {
            return MultiplyOutcome.NeedsRounding; // more than six fraction digits
        }

        var value = raw / PerUnit;
        if (value > Max || value < -Max)
        {
            return MultiplyOutcome.OutOfRange;
        }

        product = (long)value;
        return MultiplyOutcome.Exact;
    }
}

public enum MultiplyOutcome
{
    Exact,
    NeedsRounding,
    OutOfRange,
}

/// <summary>Stable codes of the v2 inventory rules (identical strings to the API problem codes).</summary>
public static class V2InventoryCodes
{
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string Archived = "ARCHIVED";
    public const string BinFrozen = "BIN_FROZEN";
    public const string BinProductMismatch = "BIN_PRODUCT_MISMATCH";
    public const string InvestigationUnresolved = "INVESTIGATION_UNRESOLVED";
    public const string CapacityRequired = "CAPACITY_REQUIRED";
    public const string CapacityNotAllowed = "CAPACITY_NOT_ALLOWED";
    public const string InsufficientStock = "INSUFFICIENT_STOCK";
    public const string PrecisionExceeded = "PRECISION_EXCEEDED";
    public const string QuantityOutOfRange = "QUANTITY_OUT_OF_RANGE";
}

/// <summary>
/// The working state of one bin inside one inventory transaction (decisions §3-4). Several lines touching the same bin
/// act on this accumulated state in order, never on a stale snapshot. One product per bin; stock never negative;
/// a zero-quantity bin keeps its product and capacity; refilling an empty bin needs a new capacity and starts with the
/// default thresholds unless overrides are given; a bin with stock keeps its configuration; another product may only
/// enter an empty bin without an unresolved count investigation; frozen and archived bins accept nothing.
/// </summary>
public sealed class V2BinState(
    long binId,
    string code,
    long? productId,
    long quantityMicros,
    long? capacityMicros,
    long? lowThresholdMicros,
    long? highThresholdMicros,
    bool archived,
    bool frozen,
    bool investigationOpen,
    int version)
{
    public long BinId { get; } = binId;
    public string Code { get; } = code;
    public long? ProductId { get; private set; } = productId;
    public long QuantityMicros { get; private set; } = quantityMicros;
    public long? CapacityMicros { get; private set; } = capacityMicros;
    public long? LowThresholdMicros { get; private set; } = lowThresholdMicros;
    public long? HighThresholdMicros { get; private set; } = highThresholdMicros;
    public int Version { get; } = version;
    public bool Changed { get; private set; }

    /// <summary>Adds stock of a product. Configuration members are allowed only when the bin is empty (quantity 0).</summary>
    public DomainError? Add(
        long product,
        int decimalPlaces,
        long micros,
        long? capacity,
        long? low,
        long? high,
        long defaultLow,
        long defaultHigh,
        string field)
    {
        if (CheckOpen(field) is { } closed)
        {
            return closed;
        }

        if (micros <= 0)
        {
            return new DomainError(V2InventoryCodes.ValidationFailed, "The quantity added must be greater than zero.", field + ".quantity");
        }

        if (QuantityMicros == 0)
        {
            if (ProductId != product && investigationOpen)
            {
                return new DomainError(
                    V2InventoryCodes.InvestigationUnresolved,
                    $"Bin {Code} has an unresolved count investigation; only its own product may enter until it is resolved.",
                    field + ".toBinId");
            }

            if (capacity is not { } newCapacity)
            {
                return new DomainError(
                    V2InventoryCodes.CapacityRequired, $"Bin {Code} is empty: send the capacity for this product.", field + ".capacity");
            }

            if (newCapacity <= 0 || newCapacity > V2Micro.Max)
            {
                return new DomainError(V2InventoryCodes.QuantityOutOfRange, "The capacity must be above zero and within the range.", field + ".capacity");
            }

            if (!V2Micro.FitsPrecision(newCapacity, decimalPlaces))
            {
                return new DomainError(
                    V2InventoryCodes.PrecisionExceeded, $"The capacity has more than {decimalPlaces} decimal places.", field + ".capacity");
            }

            if ((low ?? defaultLow) >= (high ?? defaultHigh))
            {
                return new DomainError(
                    V2InventoryCodes.ValidationFailed, "The effective low threshold must be below the effective high threshold.", field + ".lowThreshold");
            }

            ProductId = product;
            CapacityMicros = newCapacity;
            LowThresholdMicros = low; // null = warehouse default; earlier overrides are not carried over
            HighThresholdMicros = high;
        }
        else
        {
            if (ProductId != product)
            {
                return new DomainError(
                    V2InventoryCodes.BinProductMismatch, $"Bin {Code} holds another product; a bin holds one product at a time.", field + ".toBinId");
            }

            if (capacity is not null || low is not null || high is not null)
            {
                return new DomainError(
                    V2InventoryCodes.CapacityNotAllowed,
                    $"Bin {Code} holds stock: its capacity and thresholds stay; send them only for an empty bin.",
                    field + ".capacity");
            }
        }

        if (QuantityMicros > V2Micro.Max - micros)
        {
            return new DomainError(V2InventoryCodes.QuantityOutOfRange, $"Bin {Code} would exceed the largest quantity.", field + ".quantity");
        }

        QuantityMicros += micros;
        Changed = true;
        return null;
    }

    /// <summary>Removes stock of a product; the result never goes below zero. At zero the product and capacity stay.</summary>
    public DomainError? Remove(long product, long micros, string field, string binField)
    {
        if (CheckOpen(field) is { } closed)
        {
            return closed;
        }

        if (micros <= 0)
        {
            return new DomainError(V2InventoryCodes.ValidationFailed, "The quantity removed must be greater than zero.", field + ".quantity");
        }

        if (QuantityMicros > 0 && ProductId != product)
        {
            return new DomainError(
                V2InventoryCodes.BinProductMismatch, $"Bin {Code} holds another product.", field + "." + binField);
        }

        if (ProductId != product || QuantityMicros < micros)
        {
            return new DomainError(
                V2InventoryCodes.InsufficientStock,
                $"Bin {Code} has not enough of this product; stock can never become negative.",
                field + ".quantity");
        }

        QuantityMicros -= micros;
        Changed = true;
        return null;
    }

    private DomainError? CheckOpen(string field)
    {
        if (archived)
        {
            return new DomainError(V2InventoryCodes.Archived, $"Bin {Code} is archived.", field);
        }

        return frozen
            ? new DomainError(V2InventoryCodes.BinFrozen, $"Bin {Code} is frozen by a count; no inventory operation may touch it.", field)
            : null;
    }
}
