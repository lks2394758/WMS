using Wms.Domain.Common;

namespace Wms.Domain.Catalog;

/// <summary>A scannable code that resolves to exactly one product. Uniqueness is enforced by the database.</summary>
public sealed class Barcode
{
    public const int MaxLength = 128;

    private Barcode(string value, ProductId productId, bool isActive)
    {
        Value = value;
        ProductId = productId;
        IsActive = isActive;
    }

    public string Value { get; }
    public ProductId ProductId { get; }
    public bool IsActive { get; private set; }

    public static Result<Barcode> Create(string value, ProductId productId)
    {
        value = (value ?? string.Empty).Trim();
        if (value.Length is 0 or > MaxLength)
        {
            return DomainErrors.Validation($"Barcode must be 1-{MaxLength} characters.");
        }

        if (value.Any(char.IsControl))
        {
            return DomainErrors.Validation("Barcode cannot contain control characters.");
        }

        return new Barcode(value, productId, isActive: true);
    }

    public void Deactivate() => IsActive = false;

    public void Activate() => IsActive = true;
}
