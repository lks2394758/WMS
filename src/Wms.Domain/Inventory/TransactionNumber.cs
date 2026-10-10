using System.Globalization;

namespace Wms.Domain.Inventory;

/// <summary>Immutable business number, e.g. <c>SHP-20261010-000042</c>: prefix, business date, 6-digit daily sequence.</summary>
public readonly record struct TransactionNumber
{
    public const int MaxSequence = 999_999;

    private TransactionNumber(string value, InventoryTransactionType type)
    {
        Value = value;
        Type = type;
    }

    public string Value { get; }
    public InventoryTransactionType Type { get; }

    public static TransactionNumber Create(InventoryTransactionType type, DateOnly businessDate, int sequence)
    {
        if (sequence is < 1 or > MaxSequence)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence), sequence, $"Daily sequence must be 1-{MaxSequence}.");
        }

        var value = string.Create(
            CultureInfo.InvariantCulture,
            $"{type.Prefix()}-{businessDate:yyyyMMdd}-{sequence:D6}");
        return new TransactionNumber(value, type);
    }

    public static bool TryParse(string? text, out TransactionNumber number)
    {
        number = default;
        if (text is not { Length: 19 } || text[3] != '-' || text[12] != '-')
        {
            return false;
        }

        InventoryTransactionType? type = null;
        foreach (var candidate in Enum.GetValues<InventoryTransactionType>())
        {
            if (text.StartsWith(candidate.Prefix(), StringComparison.Ordinal))
            {
                type = candidate;
            }
        }

        if (type is null
            || !DateOnly.TryParseExact(text.AsSpan(4, 8), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            || !int.TryParse(text.AsSpan(13, 6), NumberStyles.None, CultureInfo.InvariantCulture, out var sequence)
            || sequence < 1)
        {
            return false;
        }

        number = Create(type.Value, date, sequence);
        return true;
    }

    public override string ToString() => Value;
}
