using System.Globalization;

namespace Wms.Contracts.V2.Common;

/// <summary>
/// API v2 identifiers: a positive 64-bit integer written as a JSON string of decimal digits, without sign,
/// leading zeros or whitespace (<c>"1"</c> … <c>"9223372036854775807"</c>). A JSON number is never accepted.
/// </summary>
public static class WireId
{
    public static bool IsValid(string? text) => TryParse(text, out _);

    public static bool TryParse(string? text, out long id)
    {
        id = 0;
        if (string.IsNullOrEmpty(text) || text.Length > 19 || text[0] == '0')
        {
            return false;
        }

        foreach (var c in text)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out id);
    }

    public static string Format(long id)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
        return id.ToString(CultureInfo.InvariantCulture);
    }
}
