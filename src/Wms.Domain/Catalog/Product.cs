using Wms.Domain.Common;

namespace Wms.Domain.Catalog;

public sealed class Product
{
    public const int MaxSkuLength = 64;
    public const int MaxNameLength = 200;

    private Product(ProductId id, string sku, string name, string unit, int? capacity, bool isActive)
    {
        Id = id;
        Sku = sku;
        Name = name;
        Unit = unit;
        Capacity = capacity;
        IsActive = isActive;
    }

    public ProductId Id { get; }
    public string Sku { get; }
    public string Name { get; private set; }
    public string Unit { get; private set; }

    /// <summary>Warehouse-level capacity used for low-stock / overstock alerts. Null = alerts disabled for this product.</summary>
    public int? Capacity { get; private set; }

    public bool IsActive { get; private set; }

    public static Result<Product> Create(ProductId id, string sku, string name, string unit = "EA", int? capacity = null)
    {
        sku = (sku ?? string.Empty).Trim();
        name = (name ?? string.Empty).Trim();
        unit = (unit ?? string.Empty).Trim();

        if (sku.Length is 0 or > MaxSkuLength)
        {
            return DomainErrors.Validation($"SKU must be 1-{MaxSkuLength} characters.");
        }

        if (name.Length is 0 or > MaxNameLength)
        {
            return DomainErrors.Validation($"Product name must be 1-{MaxNameLength} characters.");
        }

        if (unit.Length == 0)
        {
            return DomainErrors.Validation("Unit of measure is required.");
        }

        var capacityCheck = ValidateCapacity(capacity);
        if (capacityCheck.IsFailure)
        {
            return capacityCheck.Error!;
        }

        return new Product(id, sku, name, unit, capacity, isActive: true);
    }

    public Result Rename(string name)
    {
        name = (name ?? string.Empty).Trim();
        if (name.Length is 0 or > MaxNameLength)
        {
            return DomainErrors.Validation($"Product name must be 1-{MaxNameLength} characters.");
        }

        Name = name;
        return Result.Success();
    }

    public Result SetCapacity(int? capacity)
    {
        var check = ValidateCapacity(capacity);
        if (check.IsSuccess)
        {
            Capacity = capacity;
        }

        return check;
    }

    public void Activate() => IsActive = true;

    public void Deactivate() => IsActive = false;

    private static Result ValidateCapacity(int? capacity) =>
        capacity is null or > 0
            ? Result.Success()
            : DomainErrors.Validation("Capacity must be greater than zero, or empty to disable stock alerts.");
}
