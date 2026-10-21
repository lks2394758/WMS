namespace Wms.Contracts.V2.Common;

/// <summary>
/// Alert thresholds: a JSON string percentage from <c>"0"</c> to <c>"100"</c> with at most two significant fraction
/// digits (<c>"20"</c>, <c>"82.5"</c>). Same grammar as <see cref="WireQuantity"/> without a sign. Values are micro-units.
/// Occupancy percentages in responses are not thresholds: they may exceed 100 and use <see cref="WireQuantity.Format"/>.
/// </summary>
public static class WirePercent
{
    public const long MaxMicros = 100 * WireQuantity.MicrosPerUnit;

    public static bool TryParse(string? text, out long micros)
    {
        if (WireQuantity.TryParse(text, QuantitySign.NonNegative, out micros)
            && WireQuantity.FractionDigits(micros) <= 2
            && micros <= MaxMicros)
        {
            return true;
        }

        micros = 0;
        return false;
    }
}
