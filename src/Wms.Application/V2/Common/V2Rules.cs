using System.Buffers.Text;
using System.Globalization;
using System.Text;
using Wms.Contracts.V2.Bins;
using Wms.Contracts.V2.Common;

namespace Wms.Application.V2.Common;

/// <summary>Cursor paging for v2 lists: opaque base64url of the last row ID.</summary>
public static class V2Paging
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    public static V2Failure? Parse(string? cursor, int? limit, out long? afterId, out int size)
    {
        afterId = null;
        size = limit ?? DefaultLimit;
        if (size is < 1 or > MaxLimit)
        {
            return V2Failure.Validation("limit", $"limit must be between 1 and {MaxLimit}.");
        }

        if (string.IsNullOrEmpty(cursor))
        {
            return null;
        }

        try
        {
            var text = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cursor));
            if (text.StartsWith('i') && WireId.TryParse(text[1..], out var id))
            {
                afterId = id;
                return null;
            }
        }
        catch (FormatException)
        {
        }

        return V2Failure.Validation("cursor", "cursor is not valid.");
    }

    public static string Encode(long lastId) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes("i" + WireId.Format(lastId)));
}

/// <summary>Text rules of docs/database-redesign-decisions.zh-CN.md §17.3/§17.6.</summary>
public static class V2Text
{
    /// <summary>Case-insensitive identity of names and codes: invariant upper case (Unicode-aware, not ASCII-only).</summary>
    public static string Normalize(string value) => value.Trim().ToUpperInvariant();

    /// <summary>A trimmed, non-blank name of at most <paramref name="max"/> characters, or a validation failure.</summary>
    public static V2Failure? Name(string? value, int max, string field, out string trimmed)
    {
        trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0 || trimmed.Length > max || trimmed.Any(char.IsControl))
        {
            return V2Failure.Validation(field, $"{field} must be 1-{max} characters without control characters.");
        }

        return null;
    }

    /// <summary>SKU, barcode or serial number: 1-128 characters, no control characters, no leading/trailing whitespace.</summary>
    public static V2Failure? Code(string value, string field)
    {
        if (value.Length is 0 or > 128 || value != value.Trim() || value.Any(char.IsControl))
        {
            return V2Failure.Validation(field, $"{field} must be 1-128 characters without control characters or surrounding spaces.");
        }

        return null;
    }

    public static V2Failure? Id(string? value, string field, out long id)
    {
        id = 0;
        return WireId.TryParse(value, out id) ? null : V2Failure.Validation(field, $"{field} must be a v2 ID string.");
    }
}

/// <summary>Exact aggregates and capacity state, in micro-units, never floating point.</summary>
public static class V2Quantities
{
    private static readonly Int128 Micros = WireQuantity.MicrosPerUnit;

    /// <summary>Canonical text of any exact micro-unit value, including sums beyond the 64-bit range.</summary>
    public static string Format(Int128 micros)
    {
        var negative = micros < 0;
        var magnitude = negative ? -micros : micros;
        var whole = magnitude / Micros;
        var fraction = (long)(magnitude % Micros);
        var text = whole.ToString(CultureInfo.InvariantCulture);
        if (fraction != 0)
        {
            text += "." + fraction.ToString("D6", CultureInfo.InvariantCulture).TrimEnd('0');
        }

        return negative ? "-" + text : text;
    }

    public static Int128 Sum(IEnumerable<long> values)
    {
        Int128 total = 0;
        foreach (var value in values)
        {
            total += value;
        }

        return total;
    }

    /// <summary>Occupancy percentage rounded half-up to 2 decimals, as text; null without capacity.</summary>
    public static string? Occupancy(long quantityMicros, long? capacityMicros)
    {
        if (capacityMicros is not > 0)
        {
            return null;
        }

        // hundredths of a percent = quantity * 10000 / capacity, rounded half-up.
        Int128 capacity = capacityMicros.Value;
        var hundredths = ((Int128)quantityMicros * 10_000 * 2 + capacity) / (2 * capacity);
        return Format(hundredths * 10_000);
    }

    /// <summary>One capacity state, priority overCapacity &gt; high &gt; low &gt; normal; strict comparisons.</summary>
    public static string Status(long quantityMicros, long? capacityMicros, long lowMicros, long highMicros)
    {
        if (capacityMicros is not > 0)
        {
            return CapacityStatuses.NotConfigured;
        }

        // quantity / capacity * 100 compared with a threshold in micro-percent: quantity * 100e6 vs threshold * capacity.
        var scaled = (Int128)quantityMicros * 100 * Micros;
        Int128 capacity = capacityMicros.Value;
        if ((Int128)quantityMicros > capacity)
        {
            return CapacityStatuses.OverCapacity;
        }

        if (scaled > highMicros * capacity)
        {
            return CapacityStatuses.High;
        }

        return scaled < lowMicros * capacity ? CapacityStatuses.Low : CapacityStatuses.Normal;
    }
}
