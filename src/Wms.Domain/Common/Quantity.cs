namespace Wms.Domain.Common;

/// <summary>
/// A non-negative whole number of stock units. Release 1 tracks integer units only (no lots or fractions).
/// Use <see cref="ForTransaction"/> for moved quantities (must be &gt; 0) and <see cref="ForCount"/> for counted quantities (may be 0).
/// </summary>
public readonly record struct Quantity : IComparable<Quantity>
{
    private Quantity(int value) => Value = value;

    public int Value { get; }

    public static Quantity Zero => new(0);

    /// <summary>Rule 1: transaction quantities must be greater than zero.</summary>
    public static Result<Quantity> ForTransaction(int value) =>
        value > 0
            ? new Quantity(value)
            : DomainErrors.Validation($"Transaction quantity must be greater than zero; got {value}.");

    /// <summary>Rule 1: a counted quantity may be zero but never negative.</summary>
    public static Result<Quantity> ForCount(int value) =>
        value >= 0
            ? new Quantity(value)
            : DomainErrors.Validation($"Counted quantity cannot be negative; got {value}.");

    public int CompareTo(Quantity other) => Value.CompareTo(other.Value);

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
