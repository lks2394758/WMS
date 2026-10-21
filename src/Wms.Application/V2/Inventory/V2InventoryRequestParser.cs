using Wms.Application.V2.Common;
using Wms.Contracts.V2.Common;
using Wms.Contracts.V2.Inventory;

namespace Wms.Application.V2.Inventory;

/// <summary>A validated inline product of a receipt line (created inside the receipt transaction).</summary>
public sealed record V2InlineProductPlan(
    string Name, int DecimalPlaces, bool SerialTracking, long? UnitId, string? NewUnitName, string? Sku, string? Barcode);

/// <summary>One validated request line in micro-units; <see cref="Field"/> is its error path, e.g. <c>lines[2]</c>.</summary>
public sealed record V2LinePlan(
    int Index,
    string Field,
    long? ProductId,
    V2InlineProductPlan? NewProduct,
    long? FromBinId,
    long? ToBinId,
    long? QuantityMicros,
    long? CapacityMicros,
    long? LowThresholdMicros,
    long? HighThresholdMicros,
    string? PackagingUnit,
    long? PackagingQuantityMicros,
    long? ConversionFactorMicros,
    IReadOnlyList<string> SerialNumbers,
    int? GenerateSerialCount);

public sealed record V2OperationPlan(
    string Type, IReadOnlyList<V2LinePlan> Lines, string? Source, string? Destination, string? Reason, long? CountResultId);

/// <summary>
/// Request validation of contracts/v2/README.md §7 that needs no stored data: envelope, members allowed per operation
/// ("—" = must be absent), exact quantity/percent/ID grammars, serial member exclusivity and uniqueness, inline product
/// fields. Everything that depends on stored state is checked inside the write transaction.
/// </summary>
public static class V2InventoryRequestParser
{
    public const int MaxLines = 200;
    public const int MaxSerialsPerRequest = 10_000;
    public const int MaxGeneratedPerLine = 10_000;

    public static V2Failure? Parse(string type, InventoryOperationRequest? request, out V2OperationPlan plan)
    {
        plan = new V2OperationPlan(type, [], null, null, null, null);
        if (request?.Lines is not { Count: >= 1 and <= MaxLines } lines)
        {
            return V2Failure.Validation("lines", $"lines must hold 1-{MaxLines} lines.");
        }

        var receipt = type == TransactionTypesV2.Receipt;
        var shipment = type == TransactionTypesV2.Shipment;
        var adjustment = type == TransactionTypesV2.Adjustment;
        if (Text(request.Source, 200, "source", receipt, out var source) is { } badSource)
        {
            return badSource;
        }

        if (Text(request.Destination, 200, "destination", shipment, out var destination) is { } badDestination)
        {
            return badDestination;
        }

        if (Text(request.Reason, 2000, "reason", true, out var reason) is { } badReason)
        {
            return badReason;
        }

        if (adjustment && reason is null)
        {
            return V2Failure.Validation("reason", "An adjustment needs a reason.");
        }

        long? countResult = null;
        if (request.CountResultId is not null)
        {
            if (!adjustment)
            {
                return V2Failure.Validation("countResultId", "countResultId is only allowed on adjustments.");
            }

            if (V2Text.Id(request.CountResultId, "countResultId", out var parsed) is { } badResult)
            {
                return badResult;
            }

            countResult = parsed;
        }

        var parsedLines = new List<V2LinePlan>(lines.Count);
        var serials = new HashSet<string>(StringComparer.Ordinal);
        long generated = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            if (Line(type, i, lines[i], serials, out var line) is { } badLine)
            {
                return badLine;
            }

            parsedLines.Add(line!);

            // All serial items of the request, entered plus generated over every line, count toward one limit; checked
            // as the lines are read, before any database work.
            generated += line!.GenerateSerialCount ?? 0;
            if (serials.Count + generated > MaxSerialsPerRequest)
            {
                return V2Failure.Validation(
                    $"lines[{i}]",
                    $"A request may name or generate at most {MaxSerialsPerRequest} serial items in total (entered plus generated, all lines).");
            }
        }

        plan = new V2OperationPlan(type, parsedLines, source, destination, reason, countResult);
        return null;
    }

    private static V2Failure? Line(string type, int index, InventoryLine? line, HashSet<string> serials, out V2LinePlan? plan)
    {
        plan = null;
        var f = $"lines[{index}]";
        if (line is null)
        {
            return V2Failure.Validation(f, "The line is missing.");
        }

        var receipt = type == TransactionTypesV2.Receipt;
        var shipment = type == TransactionTypesV2.Shipment;
        var movement = type == TransactionTypesV2.Movement;
        var adjustment = type == TransactionTypesV2.Adjustment;

        // Members that must be absent for this operation.
        if (!receipt && line.NewProduct is not null)
        {
            return Absent(f, "newProduct");
        }

        if ((receipt || adjustment) && line.FromBinId is not null)
        {
            return Absent(f, "fromBinId");
        }

        if (shipment && line.ToBinId is not null)
        {
            return Absent(f, "toBinId");
        }

        if (shipment && (line.Capacity ?? line.LowThreshold ?? line.HighThreshold) is not null)
        {
            return Absent(f, line.Capacity is not null ? "capacity" : "lowThreshold");
        }

        if (!receipt && (line.PackagingUnit ?? line.PackagingQuantity ?? line.ConversionFactor) is not null)
        {
            return Absent(f, "packagingQuantity");
        }

        if ((shipment || movement) && line.GenerateSerialCount is not null)
        {
            return Absent(f, "generateSerialCount");
        }

        // Product.
        long? productId = null;
        V2InlineProductPlan? newProduct = null;
        if (line.ProductId is not null && line.NewProduct is not null)
        {
            return V2Failure.Validation($"{f}.newProduct", "Send either productId or newProduct, not both.");
        }

        if (line.ProductId is not null)
        {
            if (V2Text.Id(line.ProductId, $"{f}.productId", out var product) is { } badProduct)
            {
                return badProduct;
            }

            productId = product;
        }
        else if (line.NewProduct is not null)
        {
            if (InlineProduct(f, line.NewProduct, out newProduct) is { } badInline)
            {
                return badInline;
            }
        }
        else
        {
            return V2Failure.Validation($"{f}.productId", "productId is required.");
        }

        // Bins.
        var from = OptionalId(line.FromBinId, $"{f}.fromBinId", out var fromBin);
        var to = OptionalId(line.ToBinId, $"{f}.toBinId", out var toBin);
        if ((from ?? to) is { } badBin)
        {
            return badBin;
        }

        if ((shipment || movement) && fromBin is null)
        {
            return V2Failure.Validation($"{f}.fromBinId", "fromBinId is required.");
        }

        if (!shipment && toBin is null)
        {
            return V2Failure.Validation($"{f}.toBinId", "toBinId is required.");
        }

        if (movement && fromBin == toBin)
        {
            return V2Failure.Validation($"{f}.toBinId", "A movement needs two different bins.");
        }

        // Quantities (grammar; precision against the product is checked in the transaction).
        long? quantity = null;
        if (line.Quantity is not null)
        {
            var sign = adjustment ? QuantitySign.NonZeroSigned : QuantitySign.Positive;
            if (!WireQuantity.TryParse(line.Quantity, sign, out var micros))
            {
                return V2Failure.Validation(
                    $"{f}.quantity",
                    adjustment ? "quantity must be a non-zero signed exact decimal string." : "quantity must be a positive exact decimal string.");
            }

            quantity = micros;
        }

        if (Positive(line.Capacity, $"{f}.capacity", out var capacity) is { } badCapacity)
        {
            return badCapacity;
        }

        var badLow = Percent(line.LowThreshold, $"{f}.lowThreshold", out var low);
        var badHigh = Percent(line.HighThreshold, $"{f}.highThreshold", out var high);
        if ((badLow ?? badHigh) is { } badThreshold)
        {
            return badThreshold;
        }

        if (adjustment && quantity < 0 && (capacity ?? low ?? high) is not null)
        {
            return V2Failure.Validation($"{f}.capacity", "capacity and thresholds are only allowed for a positive adjustment of an empty bin.");
        }

        // Packaging (receipts).
        if (Positive(line.PackagingQuantity, $"{f}.packagingQuantity", out var packagingQuantity) is { } badPackaging)
        {
            return badPackaging;
        }

        if (Positive(line.ConversionFactor, $"{f}.conversionFactor", out var factor) is { } badFactor)
        {
            return badFactor;
        }

        if ((packagingQuantity is null) != (factor is null))
        {
            return V2Failure.Validation($"{f}.conversionFactor", "packagingQuantity and conversionFactor go together.");
        }

        string? packagingUnit = null;
        if (line.PackagingUnit is not null)
        {
            if (packagingQuantity is null)
            {
                return V2Failure.Validation($"{f}.packagingUnit", "packagingUnit needs packagingQuantity and conversionFactor.");
            }

            if (V2Text.Name(line.PackagingUnit, 32, $"{f}.packagingUnit", out var unitText) is { } badUnit)
            {
                return badUnit;
            }

            packagingUnit = unitText;
        }

        // Serial members.
        var numbers = new List<string>();
        foreach (var raw in line.SerialNumbers ?? [])
        {
            var number = (raw ?? string.Empty).Trim();
            if (V2Text.Code(number, $"{f}.serialNumbers") is { } badSerial)
            {
                return badSerial;
            }

            if (!serials.Add(V2Text.Normalize(number)))
            {
                return V2Failure.Validation($"{f}.serialNumbers", $"Serial number {number} appears more than once in the request.");
            }

            numbers.Add(number);
            if (serials.Count > MaxSerialsPerRequest)
            {
                return V2Failure.Validation(
                    $"{f}.serialNumbers",
                    $"A request may name or generate at most {MaxSerialsPerRequest} serial items in total (entered plus generated, all lines).");
            }
        }

        if (line.GenerateSerialCount is { } generate)
        {
            if (generate is < 1 or > MaxGeneratedPerLine)
            {
                return V2Failure.Validation($"{f}.generateSerialCount", $"generateSerialCount must be 1-{MaxGeneratedPerLine}.");
            }

            if (numbers.Count > 0)
            {
                return V2Failure.Validation($"{f}.generateSerialCount", "Send either serialNumbers or generateSerialCount, not both.");
            }

            if (adjustment && quantity < 0)
            {
                return V2Failure.Validation($"{f}.generateSerialCount", "Generated serial numbers are only for an increase.");
            }
        }

        plan = new V2LinePlan(
            index, f, productId, newProduct, fromBin, toBin, quantity, capacity, low, high,
            packagingUnit, packagingQuantity, factor, numbers, line.GenerateSerialCount);
        return null;
    }

    private static V2Failure? InlineProduct(string f, InlineNewProduct product, out V2InlineProductPlan? plan)
    {
        plan = null;
        var p = $"{f}.newProduct";
        if (V2Text.Name(product.Name, 128, $"{p}.name", out var name) is { } badName)
        {
            return badName;
        }

        if (product.DecimalPlaces is < 0 or > 6)
        {
            return V2Failure.Validation($"{p}.decimalPlaces", "decimalPlaces must be 0-6.");
        }

        if (product.SerialTracking && product.DecimalPlaces != 0)
        {
            return V2Failure.Validation($"{p}.decimalPlaces", "Serial-tracked products count whole items: decimalPlaces must be 0.");
        }

        if ((product.UnitId is null) == string.IsNullOrWhiteSpace(product.NewUnitName))
        {
            return V2Failure.Validation($"{p}.unitId", "Send exactly one of unitId and newUnitName.");
        }

        long? unitId = null;
        string? newUnit = null;
        if (product.UnitId is not null)
        {
            if (V2Text.Id(product.UnitId, $"{p}.unitId", out var unit) is { } badUnit)
            {
                return badUnit;
            }

            unitId = unit;
        }
        else if (V2Text.Name(product.NewUnitName, 32, $"{p}.newUnitName", out var unitName) is { } badUnitName)
        {
            return badUnitName;
        }
        else
        {
            newUnit = unitName;
        }

        var sku = string.IsNullOrEmpty(product.Sku) ? null : product.Sku;
        var barcode = string.IsNullOrEmpty(product.Barcode) ? null : product.Barcode;
        var badSku = sku is null ? null : V2Text.Code(sku, $"{p}.sku");
        var badBarcode = barcode is null ? null : V2Text.Code(barcode, $"{p}.barcode");
        if ((badSku ?? badBarcode) is { } badCode)
        {
            return badCode;
        }

        plan = new V2InlineProductPlan(name, product.DecimalPlaces, product.SerialTracking, unitId, newUnit, sku, barcode);
        return null;
    }

    private static V2Failure Absent(string f, string member) =>
        V2Failure.Validation($"{f}.{member}", $"{member} is not allowed for this operation.");

    private static V2Failure? OptionalId(string? value, string field, out long? id)
    {
        id = null;
        if (value is null)
        {
            return null;
        }

        if (V2Text.Id(value, field, out var parsed) is { } bad)
        {
            return bad;
        }

        id = parsed;
        return null;
    }

    private static V2Failure? Positive(string? value, string field, out long? micros)
    {
        micros = null;
        if (value is null)
        {
            return null;
        }

        if (!WireQuantity.TryParse(value, QuantitySign.Positive, out var parsed))
        {
            return V2Failure.Validation(field, $"{field} must be a positive exact decimal string (at most 6 decimals).");
        }

        micros = parsed;
        return null;
    }

    private static V2Failure? Percent(string? value, string field, out long? micros)
    {
        micros = null;
        if (value is null)
        {
            return null;
        }

        if (!WirePercent.TryParse(value, out var parsed))
        {
            return V2Failure.Validation(field, $"{field} must be a percentage 0-100 with at most 2 decimals.");
        }

        micros = parsed;
        return null;
    }

    private static V2Failure? Text(string? value, int max, string field, bool allowed, out string? text)
    {
        text = null;
        if (value is null)
        {
            return null;
        }

        if (!allowed)
        {
            return V2Failure.Validation(field, $"{field} is not allowed for this operation.");
        }

        var trimmed = value.Trim();
        if (trimmed.Length > max || trimmed.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t'))
        {
            return V2Failure.Validation(field, $"{field} must be at most {max} characters.");
        }

        text = trimmed.Length == 0 ? null : trimmed;
        return null;
    }
}
