using System.Globalization;

namespace Wms.Contracts.V2.Common;

/// <summary>Which signs a quantity field accepts.</summary>
public enum QuantitySign
{
    /// <summary>Greater than zero (receipt, shipment, movement, capacity, packaging values).</summary>
    Positive,

    /// <summary>Zero or greater (counted actual quantity).</summary>
    NonNegative,

    /// <summary>Non-zero, either sign (adjustment delta).</summary>
    NonZeroSigned,
}

/// <summary>
/// API v2 exact decimal quantities, always JSON strings and never binary floating point.
/// <para>
/// Request grammar: optional <c>-</c> (signed fields only), 1+ integer digits, optional <c>.</c> and 1-6 fraction
/// digits; no <c>+</c>, exponent, whitespace or grouping. Leading/trailing zeros are accepted (<c>"01.50"</c> = 1.5).
/// The absolute value is at most <see cref="MaxText"/>. The product's own precision (0-6) is a separate business
/// check on the value (<see cref="FractionDigits"/>), not on the number of digits typed.
/// </para>
/// <para>
/// Response form (<see cref="Format"/>): canonical, no leading zeros, no trailing fraction zeros, <c>"0"</c> for zero.
/// Values are carried as micro-units (value × 1 000 000) in a <see cref="long"/>.
/// </para>
/// </summary>
public static class WireQuantity
{
    public const int Scale = 6;
    public const long MicrosPerUnit = 1_000_000;
    public const long MaxMicros = 999_999_999_999_999_999;
    public const string MaxText = "999999999999.999999";

    public static bool TryParse(string? text, QuantitySign sign, out long micros)
    {
        micros = 0;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var negative = text[0] == '-';
        if (negative && sign != QuantitySign.NonZeroSigned)
        {
            return false;
        }

        var span = negative ? text.AsSpan(1) : text.AsSpan();
        var dot = span.IndexOf('.');
        var whole = dot < 0 ? span : span[..dot];
        var fraction = dot < 0 ? ReadOnlySpan<char>.Empty : span[(dot + 1)..];
        if (whole.IsEmpty || !AllDigits(whole) || (dot >= 0 && (fraction.IsEmpty || fraction.Length > Scale || !AllDigits(fraction))))
        {
            return false;
        }

        whole = whole.TrimStart('0');
        if (whole.Length > 12)
        {
            return false;
        }

        long value = 0;
        foreach (var c in whole)
        {
            value = (value * 10) + (c - '0');
        }

        value *= MicrosPerUnit;
        long fractionMicros = 0;
        for (var i = 0; i < Scale; i++)
        {
            fractionMicros = (fractionMicros * 10) + (i < fraction.Length ? fraction[i] - '0' : 0);
        }

        value += fractionMicros;
        if (value == 0 && sign != QuantitySign.NonNegative)
        {
            return false;
        }

        micros = negative ? -value : value;
        return true;
    }

    /// <summary>Canonical response text of a micro-unit value of any size (also aggregates beyond <see cref="MaxMicros"/>).</summary>
    public static string Format(long micros)
    {
        var magnitude = micros < 0 ? (ulong)(-(micros + 1)) + 1 : (ulong)micros;
        var whole = magnitude / MicrosPerUnit;
        var fraction = magnitude % MicrosPerUnit;
        var text = whole.ToString(CultureInfo.InvariantCulture);
        if (fraction != 0)
        {
            text += "." + fraction.ToString("D6", CultureInfo.InvariantCulture).TrimEnd('0');
        }

        return micros < 0 ? "-" + text : text;
    }

    /// <summary>True when <paramref name="text"/> is exactly the canonical response form of a value within the single-value range.</summary>
    public static bool IsCanonical(string? text) =>
        (TryParse(text, QuantitySign.NonZeroSigned, out var micros) || TryParse(text, QuantitySign.NonNegative, out micros))
        && Format(micros) == text;

    /// <summary>Significant fraction digits of a value (0-6); compare with the product's decimal places.</summary>
    public static int FractionDigits(long micros)
    {
        var fraction = Math.Abs(micros % MicrosPerUnit);
        var digits = Scale;
        while (digits > 0 && fraction % 10 == 0)
        {
            fraction /= 10;
            digits--;
        }

        return digits;
    }

    private static bool AllDigits(ReadOnlySpan<char> span)
    {
        foreach (var c in span)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }
}
