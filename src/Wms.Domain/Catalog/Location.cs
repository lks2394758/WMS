using Wms.Domain.Common;

namespace Wms.Domain.Catalog;

/// <summary>A storage position inside the warehouse, e.g. A-01-01. Code is unique per warehouse.</summary>
public sealed class Location
{
    public const int MaxCodeLength = 32;

    private Location(LocationId id, WarehouseId warehouseId, string code, bool isActive)
    {
        Id = id;
        WarehouseId = warehouseId;
        Code = code;
        IsActive = isActive;
    }

    public LocationId Id { get; }
    public WarehouseId WarehouseId { get; }
    public string Code { get; }
    public bool IsActive { get; private set; }

    public static Result<Location> Create(LocationId id, WarehouseId warehouseId, string code)
    {
        code = (code ?? string.Empty).Trim().ToUpperInvariant();
        if (code.Length is 0 or > MaxCodeLength)
        {
            return DomainErrors.Validation($"Location code must be 1-{MaxCodeLength} characters.");
        }

        return new Location(id, warehouseId, code, isActive: true);
    }

    public void Activate() => IsActive = true;

    public void Deactivate() => IsActive = false;
}
