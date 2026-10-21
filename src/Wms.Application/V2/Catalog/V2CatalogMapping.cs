using Wms.Application.V2.Common;
using Wms.Contracts.V2.Catalog;
using Wms.Contracts.V2.Common;
using Wms.Contracts.V2.Monitoring;
using Wms.Persistence.Abstractions.V2;

namespace Wms.Application.V2.Catalog;

/// <summary>Records to v2 wire contracts (string IDs, exact quantity strings) and category tree helpers.</summary>
public static class V2CatalogMapping
{
    public const string PathSeparator = " / ";

    public static UnitResponse ToResponse(V2UnitRecord unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return new UnitResponse(WireId.Format(unit.Id), unit.Name, unit.IsSystem, unit.ProductCount, unit.Version);
    }

    public static CategoryResponse ToResponse(V2CategoryRecord category, IReadOnlyDictionary<long, V2CategoryRecord> all)
    {
        ArgumentNullException.ThrowIfNull(category);
        return new CategoryResponse(
            WireId.Format(category.Id),
            category.Name,
            PathOf(category.Id, all),
            category.ProductCount,
            category.ChildCount,
            category.Version,
            category.ParentId is { } parent ? WireId.Format(parent) : null);
    }

    /// <summary>"Root / … / Name". Defensive against a corrupted cycle: stops after visiting every category once.</summary>
    public static string PathOf(long id, IReadOnlyDictionary<long, V2CategoryRecord> all)
    {
        ArgumentNullException.ThrowIfNull(all);
        var names = new List<string>();
        long? current = id;
        for (var guard = 0; current is { } key && all.TryGetValue(key, out var node) && guard <= all.Count; guard++)
        {
            names.Add(node.Name);
            current = node.ParentId;
        }

        names.Reverse();
        return string.Join(PathSeparator, names);
    }

    /// <summary>The category and all its descendants.</summary>
    public static HashSet<long> WithDescendants(long id, IReadOnlyCollection<V2CategoryRecord> all)
    {
        ArgumentNullException.ThrowIfNull(all);
        var children = all.Where(c => c.ParentId is not null).ToLookup(c => c.ParentId!.Value, c => c.Id);
        var result = new HashSet<long> { id };
        var queue = new Queue<long>([id]);
        while (queue.TryDequeue(out var next))
        {
            foreach (var child in children[next])
            {
                if (result.Add(child))
                {
                    queue.Enqueue(child);
                }
            }
        }

        return result;
    }

    public static ProductListItem ToListItem(V2ProductListRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var p = row.Product;
        return new ProductListItem(
            WireId.Format(p.Id),
            p.Name,
            p.Sku,
            p.Barcode,
            WireId.Format(p.UnitId),
            p.UnitName,
            p.DecimalPlaces,
            p.SerialTracking,
            p.CategoryIds.Select(WireId.Format).ToList(),
            V2Quantities.Format(V2Quantities.Sum(row.BinQuantities)),
            p.HasImage,
            p.Version);
    }

    public static ProductResponse ToResponse(V2ProductDetail detail, V2ThresholdDefaults defaults)
    {
        ArgumentNullException.ThrowIfNull(detail);
        ArgumentNullException.ThrowIfNull(defaults);
        var p = detail.Product;
        return new ProductResponse(
            WireId.Format(p.Id),
            p.Name,
            p.Sku,
            p.Barcode,
            WireId.Format(p.UnitId),
            p.UnitName,
            p.DecimalPlaces,
            p.SerialTracking,
            p.CategoryIds.Select(WireId.Format).ToList(),
            V2Quantities.Format(V2Quantities.Sum(detail.Bins.Select(b => b.QuantityMicros))),
            p.HasImage,
            p.HistoricalSkus,
            p.HistoricalBarcodes,
            detail.Bins.Select(b => ToStock(b, defaults)).ToList(),
            detail.HasOpenCountOrInvestigation,
            p.CreatedAt,
            p.UpdatedAt,
            p.Version,
            detail.Image is null ? null : Convert.ToBase64String(detail.Image.Content),
            detail.Image?.ContentType);
    }

    public static V2SubjectRecord ProductSubject(V2ProductRecord product) => new(SubjectTypes.Product, product.Id, product.Sku);

    public static SubjectRef Ref(string type, long id, string? label = null) => new(type, WireId.Format(id), label);

    public static string ImageSummary(string? contentType, int length) => contentType is null ? "none" : $"{contentType}, {length} bytes";

    private static ProductBinStock ToStock(V2BinStockRecord bin, V2ThresholdDefaults defaults)
    {
        var low = bin.LowThresholdMicros ?? defaults.LowMicros;
        var high = bin.HighThresholdMicros ?? defaults.HighMicros;
        return new ProductBinStock(
            WireId.Format(bin.BinId),
            bin.Code,
            WireQuantity.Format(bin.QuantityMicros),
            V2Quantities.Status(bin.QuantityMicros, bin.CapacityMicros, low, high),
            bin.CapacityMicros is { } capacity ? WireQuantity.Format(capacity) : null,
            V2Quantities.Occupancy(bin.QuantityMicros, bin.CapacityMicros),
            bin.FrozenCountTaskId is { } task ? WireId.Format(task) : null);
    }
}
