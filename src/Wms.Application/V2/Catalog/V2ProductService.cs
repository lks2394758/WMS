using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Wms.Application.V2.Auth;
using Wms.Application.V2.Common;
using Wms.Contracts.V2.Auth;
using Wms.Contracts.V2.Catalog;
using Wms.Contracts.V2.Common;
using Wms.Contracts.V2.Monitoring;
using Wms.Persistence.Abstractions.V2;

namespace Wms.Application.V2.Catalog;

/// <summary>
/// Products (decisions §2, §5, §17.2-17.6):
/// <list type="bullet">
/// <item>name 1-128 (duplicates allowed); exactly one current SKU and barcode, generated as <c>SKU-000001</c>/<c>BC-000001</c>
/// from the SystemSettings counters (atomic in the write transaction, skipping every code ever used) or entered;</item>
/// <item>SKU, barcode: unique case-insensitively (Unicode invariant upper case) against every current and historical code
/// of every product, archived included; a replaced code moves to the history and stays reserved forever;</item>
/// <item>changing SKU, barcode, unit, decimal places or serial tracking, and archiving, require zero stock in every bin
/// and no unfinished count or unresolved investigation; a unit/precision change and archiving clear the product's
/// zero-stock bin capacity and overrides (archiving also the reference) and resolve its capacity alerts;</item>
/// <item>create requires an <c>Idempotency-Key</c>: actor, key and the digest of the canonical body are stored on the row;
/// a retry with the same body returns the existing product, another body is <c>IDEMPOTENCY_CONFLICT</c>.</item>
/// </list>
/// </summary>
public sealed class V2ProductService(IV2CatalogReader reader, V2CatalogWriter writer)
{
    public const string SkuPrefix = "SKU-";
    public const string BarcodePrefix = "BC-";
    private const int MaxName = 128;

    public async Task<V2Result<Page<ProductListItem>>> ListAsync(
        string? search, string? categoryId, string? cursor, int? limit, CancellationToken cancellationToken)
    {
        if (V2Paging.Parse(cursor, limit, out var after, out var size) is { } invalid)
        {
            return invalid;
        }

        IReadOnlyCollection<long>? categories = null;
        if (!string.IsNullOrEmpty(categoryId))
        {
            if (V2Text.Id(categoryId, "categoryId", out var root) is { } badCategory)
            {
                return badCategory;
            }

            var all = await reader.ListCategoriesAsync(cancellationToken);
            categories = all.Any(c => c.Id == root) ? V2CatalogMapping.WithDescendants(root, all) : [];
        }

        var rows = await reader.ListProductsAsync(
            new V2ProductFilter(string.IsNullOrWhiteSpace(search) ? null : search.Trim(), categories, after, size + 1), cancellationToken);
        var page = rows.Take(size).Select(V2CatalogMapping.ToListItem).ToList();
        return new Page<ProductListItem>(page, rows.Count > size ? V2Paging.Encode(rows[size - 1].Product.Id) : null);
    }

    public async Task<V2Result<ProductResponse>> GetAsync(string id, CancellationToken cancellationToken)
    {
        if (V2Text.Id(id, "id", out var productId) is { } invalid)
        {
            return invalid;
        }

        var detail = await reader.FindProductAsync(productId, cancellationToken);
        if (detail is null || detail.Product.IsArchived)
        {
            return V2Failure.NotFound("Product", V2CatalogMapping.Ref(SubjectTypes.Product, productId));
        }

        return V2CatalogMapping.ToResponse(detail, await reader.GetThresholdDefaultsAsync(cancellationToken));
    }

    public async Task<V2Result<ProductResponse>> CreateAsync(
        V2SessionPrincipal principal, ProductCreateRequest request, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var badKey = ParseKey(idempotencyKey, out var key);
        var fields = Validate(request.Name, request.UnitId, request.DecimalPlaces, request.SerialTracking, request.CategoryIds, request.Sku, request.Barcode);
        V2ProductImage? image = null;
        var rejected = badKey ?? fields.Failure ?? V2ImageValidator.TryRead(request.ImageBase64, request.ImageContentType, out image);
        if (rejected is not null)
        {
            return await writer.RejectAsync<ProductResponse>(
                principal, PermissionNames.CatalogWrite, ActivityTypes.ProductCreate, "新增商品", [], rejected, cancellationToken);
        }

        var digest = Digest(fields, request.ImageContentType, request.ImageBase64);
        var defaults = await reader.GetThresholdDefaultsAsync(cancellationToken);
        var replayed = false;
        var result = await writer.RunAsync<ProductResponse>(
            principal, PermissionNames.CatalogWrite, ActivityTypes.ProductCreate, $"新增商品 {fields.Name}", [],
            async (scope, actor, now) =>
            {
                if (await scope.FindProductByCreationRequestAsync(actor.UserId, key, cancellationToken) is { } earlier)
                {
                    if (!string.Equals(earlier.CreationRequestDigest, digest, StringComparison.Ordinal))
                    {
                        return new V2Failure(
                            ProblemCodesV2.IdempotencyConflict,
                            "This Idempotency-Key was already used for a different product. Use a new key for a new product.",
                            Subjects: [V2CatalogMapping.Ref(SubjectTypes.Product, earlier.Id, earlier.Sku)]);
                    }

                    replayed = true;
                    return V2CatalogMapping.ToResponse((await scope.FindProductAsync(earlier.Id, cancellationToken))!, defaults);
                }

                if (await CheckReferencesAsync(scope, fields.UnitId, fields.CategoryIds, cancellationToken) is { } missing)
                {
                    return missing;
                }

                var codes = new CodeIndex(await scope.ListProductCodesAsync(cancellationToken));
                var sku = fields.Sku ?? await codes.GenerateAsync(scope, V2Counter.Sku, SkuPrefix, cancellationToken);
                var barcode = fields.Barcode ?? await codes.GenerateAsync(scope, V2Counter.Barcode, BarcodePrefix, cancellationToken);
                if (codes.SkuOwner(sku) is { } skuOwner)
                {
                    return CodeTaken("sku", sku, skuOwner);
                }

                if (codes.BarcodeOwner(barcode) is { } barcodeOwner)
                {
                    return CodeTaken("barcode", barcode, barcodeOwner);
                }

                var id = await scope.InsertProductAsync(
                    new V2NewProduct(
                        fields.Name, sku, V2Text.Normalize(sku), barcode, V2Text.Normalize(barcode), fields.UnitId, fields.DecimalPlaces,
                        fields.SerialTracking, fields.CategoryIds, image, actor.UserId, key, digest, now),
                    cancellationToken);
                var detail = (await scope.FindProductAsync(id, cancellationToken))!;
                scope.AddActivity(new V2ActivityEntry(
                    now, ActivityTypes.ProductCreate, $"新增商品 {fields.Name}（{sku}）", true,
                    [V2CatalogMapping.ProductSubject(detail.Product)], actor.UserId, actor.DisplayName, actor.Role,
                    Changes: Describe(null, detail.Product)));
                return V2CatalogMapping.ToResponse(detail, defaults);
            },
            cancellationToken);

        return result.IsSuccess ? V2Result<ProductResponse>.Success(result.Value, replayed) : result;
    }

    public async Task<V2Result<ProductResponse>> UpdateAsync(
        V2SessionPrincipal principal, string id, ProductUpdateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var badId = V2Text.Id(id, "id", out var productId);
        var fields = Validate(request.Name, request.UnitId, request.DecimalPlaces, request.SerialTracking, request.CategoryIds, request.Sku, request.Barcode);
        var imageConflict = request.RemoveImage == true && (request.ImageBase64 is not null || request.ImageContentType is not null)
            ? V2Failure.Validation("removeImage", "removeImage cannot be combined with a new image.")
            : null;
        V2ProductImage? image = null;
        var rejected = badId ?? fields.Failure ?? imageConflict
            ?? V2ImageValidator.TryRead(request.ImageBase64, request.ImageContentType, out image);
        if (rejected is not null)
        {
            IReadOnlyList<V2SubjectRecord> subjects = productId > 0 ? [new(SubjectTypes.Product, productId)] : [];
            return await writer.RejectAsync<ProductResponse>(
                principal, PermissionNames.CatalogWrite, ActivityTypes.ProductUpdate, "修改商品", subjects, rejected, cancellationToken);
        }

        var defaults = await reader.GetThresholdDefaultsAsync(cancellationToken);
        var subject = V2CatalogMapping.Ref(SubjectTypes.Product, productId);
        return await writer.RunAsync<ProductResponse>(
            principal, PermissionNames.CatalogWrite, ActivityTypes.ProductUpdate, $"修改商品 {fields.Name}", [new(SubjectTypes.Product, productId)],
            async (scope, actor, now) =>
            {
                var current = await scope.FindProductAsync(productId, cancellationToken);
                if (current is null)
                {
                    return V2Failure.NotFound("Product", subject);
                }

                var p = current.Product;
                if (p.IsArchived)
                {
                    return new V2Failure(ProblemCodesV2.Archived, "The product is archived.", Subjects: [subject]);
                }

                if (p.Version != request.Version)
                {
                    return V2Failure.VersionConflict(subject);
                }

                if (await CheckReferencesAsync(scope, fields.UnitId, fields.CategoryIds, cancellationToken) is { } missing)
                {
                    return missing;
                }

                var sku = fields.Sku ?? p.Sku;
                var barcode = fields.Barcode ?? p.Barcode;
                var skuMoved = V2Text.Normalize(sku) != V2Text.Normalize(p.Sku);
                var barcodeMoved = V2Text.Normalize(barcode) != V2Text.Normalize(p.Barcode);
                var measureChanged = fields.UnitId != p.UnitId || fields.DecimalPlaces != p.DecimalPlaces;
                var keyChanged = sku != p.Sku || barcode != p.Barcode || measureChanged || fields.SerialTracking != p.SerialTracking;
                if (keyChanged && Blocked(current, subject) is { } blocked)
                {
                    return blocked;
                }

                var codes = new CodeIndex(await scope.ListProductCodesAsync(cancellationToken));
                if (skuMoved && codes.SkuOwner(sku) is { } skuOwner)
                {
                    return CodeTaken("sku", sku, skuOwner);
                }

                if (barcodeMoved && codes.BarcodeOwner(barcode) is { } barcodeOwner)
                {
                    return CodeTaken("barcode", barcode, barcodeOwner);
                }

                var replaceImage = image is not null || request.RemoveImage == true;
                var updated = await scope.UpdateProductAsync(
                    new V2ProductUpdate(
                        productId, p.Version, fields.Name, sku, V2Text.Normalize(sku), barcode, V2Text.Normalize(barcode),
                        skuMoved ? [.. p.HistoricalSkus, p.Sku] : p.HistoricalSkus,
                        barcodeMoved ? [.. p.HistoricalBarcodes, p.Barcode] : p.HistoricalBarcodes,
                        fields.UnitId, fields.DecimalPlaces, fields.SerialTracking, fields.CategoryIds, replaceImage, image, Archive: false, now),
                    cancellationToken);
                if (!updated)
                {
                    return V2Failure.VersionConflict(subject);
                }

                var changes = new List<V2FieldChange>();
                var detail = (await scope.FindProductAsync(productId, cancellationToken))!;
                changes.AddRange(Describe(p, detail.Product));
                var subjects = new List<V2SubjectRecord> { V2CatalogMapping.ProductSubject(detail.Product) };
                if (measureChanged)
                {
                    // The old capacity and thresholds were in the old unit/precision: they no longer mean anything.
                    var bins = await scope.ClearZeroStockBinsAsync(productId, dropProduct: false, now, cancellationToken);
                    var alerts = await scope.ResolveCapacityAlertsAsync(productId, now, "商品基础单位或精度已变更，旧容量配置已清除", cancellationToken);
                    subjects.AddRange(bins.Select(b => new V2SubjectRecord(SubjectTypes.Bin, b)));
                    subjects.AddRange(alerts.Select(a => new V2SubjectRecord(SubjectTypes.Alert, a)));
                    if (bins.Count > 0)
                    {
                        changes.Add(new V2FieldChange("binCapacity", $"{bins.Count} bin(s) configured", "cleared"));
                    }
                }

                scope.AddActivity(new V2ActivityEntry(
                    now, ActivityTypes.ProductUpdate, $"修改商品 {detail.Product.Name}（{detail.Product.Sku}）", true, subjects,
                    actor.UserId, actor.DisplayName, actor.Role, Changes: changes));
                return V2CatalogMapping.ToResponse((await scope.FindProductAsync(productId, cancellationToken))!, defaults);
            },
            cancellationToken);
    }

    public async Task<V2Result<V2None>> ArchiveAsync(
        V2SessionPrincipal principal, string id, DeleteRequest? request, CancellationToken cancellationToken)
    {
        if (V2Text.Id(id, "id", out var productId) is { } badId)
        {
            return await writer.RejectAsync<V2None>(
                principal, PermissionNames.CatalogWrite, ActivityTypes.ProductArchive, "归档商品", [], badId, cancellationToken);
        }

        var subject = V2CatalogMapping.Ref(SubjectTypes.Product, productId);
        return await writer.RunAsync<V2None>(
            principal, PermissionNames.CatalogWrite, ActivityTypes.ProductArchive, "归档商品", [new(SubjectTypes.Product, productId)],
            async (scope, actor, now) =>
            {
                var current = await scope.FindProductAsync(productId, cancellationToken);
                if (current is null || current.Product.IsArchived)
                {
                    return V2Failure.NotFound("Product", subject);
                }

                var p = current.Product;
                if (request?.Version is { } version && version != p.Version)
                {
                    return V2Failure.VersionConflict(subject);
                }

                if (Blocked(current, subject) is { } blocked)
                {
                    return blocked;
                }

                if (!await scope.UpdateProductAsync(
                        new V2ProductUpdate(
                            productId, p.Version, p.Name, p.Sku, V2Text.Normalize(p.Sku), p.Barcode, V2Text.Normalize(p.Barcode),
                            p.HistoricalSkus, p.HistoricalBarcodes, p.UnitId, p.DecimalPlaces, p.SerialTracking, p.CategoryIds,
                            ReplaceImage: false, Image: null, Archive: true, now),
                        cancellationToken))
                {
                    return V2Failure.VersionConflict(subject);
                }

                var bins = await scope.ClearZeroStockBinsAsync(productId, dropProduct: true, now, cancellationToken);
                var alerts = await scope.ResolveCapacityAlertsAsync(productId, now, "商品已归档，库位已释放", cancellationToken);
                scope.AddActivity(new V2ActivityEntry(
                    now, ActivityTypes.ProductArchive, $"归档商品 {p.Name}（{p.Sku}）", true,
                    [V2CatalogMapping.ProductSubject(p), .. bins.Select(b => new V2SubjectRecord(SubjectTypes.Bin, b)),
                        .. alerts.Select(a => new V2SubjectRecord(SubjectTypes.Alert, a))],
                    actor.UserId, actor.DisplayName, actor.Role,
                    Changes: [new("archived", "false", "true"), new("releasedBins", null, bins.Count.ToString(CultureInfo.InvariantCulture))]));
                return V2None.Value;
            },
            cancellationToken);
    }

    /// <summary>Key changes and archiving need zero stock in every bin and nothing open in counts or investigations.</summary>
    private static V2Failure? Blocked(V2ProductDetail product, SubjectRef subject)
    {
        if (product.Bins.Any(b => b.QuantityMicros != 0))
        {
            return new V2Failure(
                ProblemCodesV2.StockNotZero,
                "The product still has stock. SKU, barcode, unit, decimal places and serial tracking can change, and the product can be archived, only at zero stock in every bin.",
                Subjects: [subject, .. product.Bins.Where(b => b.QuantityMicros != 0).Select(b => V2CatalogMapping.Ref(SubjectTypes.Bin, b.BinId, b.Code))]);
        }

        if (product.HasOpenCountOrInvestigation)
        {
            return new V2Failure(
                ProblemCodesV2.OpenCountOrInvestigation,
                "An unfinished count or an unresolved count investigation involves this product.",
                Subjects: [subject]);
        }

        return null;
    }

    private static async Task<V2Failure?> CheckReferencesAsync(
        IV2CatalogWriteScope scope, long unitId, IReadOnlyList<long> categoryIds, CancellationToken cancellationToken)
    {
        if (await scope.FindUnitAsync(unitId, cancellationToken) is null)
        {
            return V2Failure.NotFound("Unit", V2CatalogMapping.Ref(SubjectTypes.Unit, unitId)) with { Field = "unitId" };
        }

        if (categoryIds.Count > 0)
        {
            var known = (await scope.ListCategoriesAsync(cancellationToken)).Select(c => c.Id).ToHashSet();
            var unknown = categoryIds.FirstOrDefault(c => !known.Contains(c));
            if (unknown != 0)
            {
                return V2Failure.NotFound("Category", V2CatalogMapping.Ref(SubjectTypes.Category, unknown)) with { Field = "categoryIds" };
            }
        }

        return null;
    }

    private static V2Failure? ParseKey(string? header, out string key)
    {
        key = string.Empty;
        if (string.IsNullOrWhiteSpace(header) || !Guid.TryParseExact(header.Trim(), "D", out var guid))
        {
            return V2Failure.Validation("Idempotency-Key", "The Idempotency-Key header is required and must be a UUID.");
        }

        key = guid.ToString("D");
        return null;
    }

    /// <summary>
    /// SHA-256 (hex) of the canonical create body, built from the already validated fields (never from raw members that
    /// may be null): trimmed name, numeric unit ID, sorted distinct category IDs, entered codes (null = generate), and the
    /// image as type plus the hash of its base64. A missing and an empty category list are the same body.
    /// </summary>
    private static string Digest(Fields fields, string? imageContentType, string? imageBase64)
    {
        var canonical = JsonSerializer.Serialize(new object?[]
        {
            "productCreate/2",
            fields.Name,
            fields.UnitId,
            fields.DecimalPlaces,
            fields.SerialTracking,
            fields.CategoryIds.Order().ToArray(),
            fields.Sku,
            fields.Barcode,
            imageContentType,
            imageBase64 is null ? null : Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(imageBase64))),
        });
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static Fields Validate(
        string? name, string? unitId, int decimalPlaces, bool serialTracking, IReadOnlyList<string>? categoryIds, string? sku, string? barcode)
    {
        if (V2Text.Name(name, MaxName, "name", out var trimmed) is { } badName)
        {
            return new Fields(badName);
        }

        if (V2Text.Id(unitId, "unitId", out var unit) is { } badUnit)
        {
            return new Fields(badUnit);
        }

        if (decimalPlaces is < 0 or > 6)
        {
            return new Fields(V2Failure.Validation("decimalPlaces", "decimalPlaces must be 0-6."));
        }

        if (serialTracking && decimalPlaces != 0)
        {
            return new Fields(V2Failure.Validation("decimalPlaces", "Serial-tracked products count whole items: decimalPlaces must be 0."));
        }

        var categories = new List<long>();
        foreach (var text in categoryIds ?? [])
        {
            if (V2Text.Id(text, "categoryIds", out var category) is { } badCategory)
            {
                return new Fields(badCategory);
            }

            if (!categories.Contains(category))
            {
                categories.Add(category);
            }
        }

        string? Optional(string? code, string field, out V2Failure? failure)
        {
            failure = null;
            if (string.IsNullOrEmpty(code))
            {
                return null;
            }

            failure = V2Text.Code(code, field);
            return code;
        }

        var skuValue = Optional(sku, "sku", out var badSku);
        var barcodeValue = Optional(barcode, "barcode", out var badBarcode);
        if ((badSku ?? badBarcode) is { } badCode)
        {
            return new Fields(badCode);
        }

        return new Fields(null, trimmed, unit, decimalPlaces, serialTracking, categories, skuValue, barcodeValue);
    }

    private static V2Failure CodeTaken(string field, string code, V2CodeOwner owner) =>
        new(
            ProblemCodesV2.AlreadyExists,
            $"{field} '{code}' is used now or was used before (codes are never reused, archived products included).",
            field,
            [V2CatalogMapping.Ref(SubjectTypes.Product, owner.ProductId, owner.Sku)]);

    /// <summary>Changed fields as display text; the image only as type and size.</summary>
    private static List<V2FieldChange> Describe(V2ProductRecord? before, V2ProductRecord after)
    {
        var changes = new List<V2FieldChange>();
        void Add(string field, string? old, string? now)
        {
            if (!string.Equals(old, now, StringComparison.Ordinal))
            {
                changes.Add(new V2FieldChange(field, old, now));
            }
        }

        static string Ids(IEnumerable<long> ids) => string.Join(",", ids.Order().Select(WireId.Format));

        Add("name", before?.Name, after.Name);
        Add("sku", before?.Sku, after.Sku);
        Add("barcode", before?.Barcode, after.Barcode);
        Add("unit", before?.UnitName, after.UnitName);
        Add("decimalPlaces", before?.DecimalPlaces.ToString(CultureInfo.InvariantCulture), after.DecimalPlaces.ToString(CultureInfo.InvariantCulture));
        Add("serialTracking", before?.SerialTracking.ToString(), after.SerialTracking.ToString());
        Add("categoryIds", before is null ? null : Ids(before.CategoryIds), Ids(after.CategoryIds));
        Add(
            "image",
            before is null ? null : V2CatalogMapping.ImageSummary(before.ImageContentType, before.ImageLength),
            V2CatalogMapping.ImageSummary(after.ImageContentType, after.ImageLength));
        return changes;
    }

    private sealed record Fields(
        V2Failure? Failure,
        string Name = "",
        long UnitId = 0,
        int DecimalPlaces = 0,
        bool SerialTracking = false,
        IReadOnlyList<long>? Categories = null,
        string? Sku = null,
        string? Barcode = null)
    {
        public IReadOnlyList<long> CategoryIds => Categories ?? [];
    }

    /// <summary>
    /// Every code ever used by any product (current and historical, archived included), normalized. Shared with inline
    /// receipt products (one rule, one implementation); <see cref="Add"/> reserves the codes of a product created earlier
    /// in the same transaction.
    /// </summary>
    internal sealed class CodeIndex(IReadOnlyList<V2CodeOwner> owners)
    {
        private readonly Dictionary<string, V2CodeOwner> _skus = Index(owners, o => [o.Sku, .. o.HistoricalSkus]);
        private readonly Dictionary<string, V2CodeOwner> _barcodes = Index(owners, o => [o.Barcode, .. o.HistoricalBarcodes]);

        public V2CodeOwner? SkuOwner(string code) => _skus.GetValueOrDefault(V2Text.Normalize(code));

        public V2CodeOwner? BarcodeOwner(string code) => _barcodes.GetValueOrDefault(V2Text.Normalize(code));

        public void Add(V2CodeOwner owner)
        {
            _skus.TryAdd(V2Text.Normalize(owner.Sku), owner);
            _barcodes.TryAdd(V2Text.Normalize(owner.Barcode), owner);
        }

        /// <summary>Next counter value whose code is free in this kind; values are consumed even when skipped.</summary>
        public async Task<string> GenerateAsync(IV2CatalogWriteScope scope, V2Counter counter, string prefix, CancellationToken cancellationToken)
        {
            var taken = counter == V2Counter.Sku ? _skus : _barcodes;
            while (true)
            {
                var number = await scope.NextNumberAsync(counter, cancellationToken);
                var code = prefix + number.ToString("D6", CultureInfo.InvariantCulture);
                if (!taken.ContainsKey(V2Text.Normalize(code)))
                {
                    return code;
                }
            }
        }

        private static Dictionary<string, V2CodeOwner> Index(IReadOnlyList<V2CodeOwner> owners, Func<V2CodeOwner, List<string>> codes)
        {
            var index = new Dictionary<string, V2CodeOwner>(StringComparer.Ordinal);
            foreach (var owner in owners)
            {
                foreach (var code in codes(owner))
                {
                    index.TryAdd(V2Text.Normalize(code), owner);
                }
            }

            return index;
        }
    }
}
