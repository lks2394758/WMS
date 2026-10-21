using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Wms.Application.V2.Auth;
using Wms.Application.V2.Catalog;
using Wms.Application.V2.Common;
using Wms.Contracts.V2.Auth;
using Wms.Contracts.V2.Common;
using Wms.Contracts.V2.Inventory;
using Wms.Contracts.V2.Monitoring;
using Wms.Domain.Common;
using Wms.Domain.V2.Inventory;
using Wms.Persistence.Abstractions.V2;

namespace Wms.Application.V2.Inventory;

/// <summary>
/// Receipts, shipments, movements and adjustments (decisions §3-5, §9, §17; contracts/v2/README.md §1.2, §7).
/// <para>
/// One request = one write transaction on the shared v2 writer: the clock is read after the lock; the session, current
/// role and permission are re-checked inside it; every line acts on the accumulated in-transaction state of its bins
/// and serial items (<see cref="V2BinState"/>); bins, items, inline products and units, the immutable ledger
/// (transaction, lines, item links), capacity alerts of the changed bins and the success log commit together, or
/// nothing does. A rejection is logged afterwards in its own transaction.
/// </para>
/// <para>
/// Idempotency: the client's <c>Idempotency-Key</c> (UUID) and a SHA-256 digest of the canonical request are stored on
/// the transaction (unique per account and key). The same key and body return the stored, immutable transaction
/// (<c>Idempotent-Replayed</c>); another body is <c>IDEMPOTENCY_CONFLICT</c>; the session and permission are checked
/// first in both cases.
/// </para>
/// </summary>
public sealed class V2InventoryService(V2CatalogWriter writer)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<V2Result<InventoryTransactionResponse>> ExecuteAsync(
        string type, V2SessionPrincipal principal, InventoryOperationRequest? request, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var (permission, activity, label) = Kind(type);
        var badKey = ParseKey(idempotencyKey, out var key);
        var badRequest = V2InventoryRequestParser.Parse(type, request, out var plan);
        if ((badKey ?? badRequest) is { } rejected)
        {
            return await writer.RejectAsync<InventoryTransactionResponse>(principal, permission, activity, label, [], rejected, cancellationToken);
        }

        var digest = Digest(type, request!);
        var replayed = false;
        var result = await writer.RunAsync<InventoryTransactionResponse>(
            principal, permission, activity, label, RequestSubjects(plan),
            async (scope, actor, now) =>
            {
                if (await scope.FindTransactionByRequestAsync(actor.UserId, key, cancellationToken) is { } earlier)
                {
                    if (!string.Equals(earlier.RequestDigest, digest, StringComparison.Ordinal))
                    {
                        return new V2Failure(
                            ProblemCodesV2.IdempotencyConflict,
                            "This Idempotency-Key was already used for a different request. Use a new key for a new action.",
                            Subjects: [V2CatalogMapping.Ref(SubjectTypes.Transaction, earlier.Id)]);
                    }

                    replayed = true;
                    return V2InventoryMapping.ToResponse((await scope.FindTransactionAsync(earlier.Id, cancellationToken))!);
                }

                return await new Execution(scope, actor, now, plan, key, digest, label, activity, cancellationToken).RunAsync();
            },
            cancellationToken);

        return result.IsSuccess ? V2Result<InventoryTransactionResponse>.Success(result.Value, replayed) : result;
    }

    private static (string Permission, string Activity, string Label) Kind(string type) => type switch
    {
        TransactionTypesV2.Receipt => (PermissionNames.InventoryReceive, ActivityTypes.Receipt, "入库"),
        TransactionTypesV2.Shipment => (PermissionNames.InventoryShip, ActivityTypes.Shipment, "出库"),
        TransactionTypesV2.Movement => (PermissionNames.InventoryMove, ActivityTypes.Movement, "移库"),
        TransactionTypesV2.Adjustment => (PermissionNames.InventoryAdjust, ActivityTypes.Adjustment, "库存调整"),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown inventory operation."),
    };

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

    /// <summary>SHA-256 (hex) of the operation type and the request as bound (fixed member order, no formatting).</summary>
    private static string Digest(string type, InventoryOperationRequest request) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { version = "inventory/1", type, request }, Json))));

    private static IReadOnlyList<V2SubjectRecord> RequestSubjects(V2OperationPlan plan) =>
        plan.Lines.Where(l => l.ProductId is not null).Select(l => new V2SubjectRecord(SubjectTypes.Product, l.ProductId!.Value))
            .Concat(plan.Lines.SelectMany(l => new[] { l.FromBinId, l.ToBinId }).OfType<long>().Select(b => new V2SubjectRecord(SubjectTypes.Bin, b)))
            .Distinct()
            .ToList();

    /// <summary>The work of one request inside its write transaction.</summary>
    private sealed class Execution(
        IV2CatalogWriteScope scope,
        V2Actor actor,
        DateTimeOffset now,
        V2OperationPlan plan,
        string key,
        string digest,
        string label,
        string activity,
        CancellationToken ct)
    {
        private readonly Dictionary<long, V2ProductRecord> _products = [];
        private readonly Dictionary<int, long> _lineProducts = [];
        private readonly Dictionary<long, V2BinState> _bins = [];
        private readonly Dictionary<long, List<long>> _binTasks = [];
        private readonly Dictionary<string, ItemState> _current = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ItemState> _superseded = new(StringComparer.Ordinal);
        private readonly List<V2SubjectRecord> _created = [];
        private readonly List<V2TransactionLineRecord> _lines = [];
        private V2SettingsRecord _settings = null!;
        private V2ProductService.CodeIndex? _codes;
        private HashSet<string>? _usedSerials;

        private string Type => plan.Type;

        public async Task<V2Result<InventoryTransactionResponse>> RunAsync()
        {
            _settings = await scope.GetSettingsAsync(ct);
            var failure = await CheckCountResultAsync()
                ?? await LoadProductsAsync()
                ?? await CreateInlineProductsAsync()
                ?? await LoadBinsAsync()
                ?? await LoadSerialItemsAsync();
            if (failure is not null)
            {
                return failure;
            }

            foreach (var line in plan.Lines)
            {
                if (await ApplyAsync(line) is { } lineFailure)
                {
                    return lineFailure;
                }
            }

            return await PersistAsync();
        }

        // ---- loading and checks ----

        private async Task<V2Failure?> CheckCountResultAsync()
        {
            if (plan.CountResultId is not { } resultId)
            {
                return null;
            }

            var link = await scope.FindCountResultAsync(resultId, ct);
            if (link is null)
            {
                return V2Failure.NotFound("Count result", V2CatalogMapping.Ref(SubjectTypes.CountResult, resultId)) with { Field = "countResultId" };
            }

            return plan.Lines.All(l => l.ToBinId == link.BinId && l.ProductId == link.ProductId)
                ? null
                : new V2Failure(
                    ProblemCodesV2.ValidationFailed,
                    "An adjustment linked to a count result may only adjust that result's bin and product.",
                    "countResultId",
                    [V2CatalogMapping.Ref(SubjectTypes.CountResult, resultId), V2CatalogMapping.Ref(SubjectTypes.Bin, link.BinId),
                        V2CatalogMapping.Ref(SubjectTypes.Product, link.ProductId)]);
        }

        private async Task<V2Failure?> LoadProductsAsync()
        {
            var ids = plan.Lines.Where(l => l.ProductId is not null).Select(l => l.ProductId!.Value).Distinct().ToList();
            foreach (var (id, record) in await scope.FindProductRecordsAsync(ids, ct))
            {
                _products[id] = record;
            }

            foreach (var line in plan.Lines.Where(l => l.ProductId is not null))
            {
                var id = line.ProductId!.Value;
                if (!_products.TryGetValue(id, out var product))
                {
                    return V2Failure.NotFound("Product", V2CatalogMapping.Ref(SubjectTypes.Product, id)) with { Field = $"{line.Field}.productId" };
                }

                if (product.IsArchived)
                {
                    return new V2Failure(
                        ProblemCodesV2.Archived, $"Product {product.Sku} is archived.", $"{line.Field}.productId",
                        [V2CatalogMapping.Ref(SubjectTypes.Product, id, product.Sku)]);
                }

                _lineProducts[line.Index] = id;
            }

            return null;
        }

        /// <summary>Receipt-only products (and units) created inside this transaction; rolled back with it.</summary>
        private async Task<V2Failure?> CreateInlineProductsAsync()
        {
            foreach (var line in plan.Lines.Where(l => l.NewProduct is not null))
            {
                var p = line.NewProduct!;
                var field = $"{line.Field}.newProduct";
                long unitId;
                if (p.UnitId is { } existingUnit)
                {
                    if (await scope.FindUnitAsync(existingUnit, ct) is null)
                    {
                        return V2Failure.NotFound("Unit", V2CatalogMapping.Ref(SubjectTypes.Unit, existingUnit)) with { Field = $"{field}.unitId" };
                    }

                    unitId = existingUnit;
                }
                else
                {
                    var normalized = V2Text.Normalize(p.NewUnitName!);
                    if (await scope.FindUnitByNormalizedNameAsync(normalized, ct) is { } same)
                    {
                        unitId = same.Id; // an existing name (case-insensitive) reuses that unit
                    }
                    else
                    {
                        unitId = await scope.InsertUnitAsync(p.NewUnitName!, normalized, now, ct);
                        _created.Add(new V2SubjectRecord(SubjectTypes.Unit, unitId, p.NewUnitName));
                        scope.AddActivity(new V2ActivityEntry(
                            now, ActivityTypes.UnitCreate, $"入库时新增单位 {p.NewUnitName}", true, [new(SubjectTypes.Unit, unitId, p.NewUnitName)],
                            actor.UserId, actor.DisplayName, actor.Role));
                    }
                }

                _codes ??= new V2ProductService.CodeIndex(await scope.ListProductCodesAsync(ct));
                var sku = p.Sku ?? await _codes.GenerateAsync(scope, V2Counter.Sku, V2ProductService.SkuPrefix, ct);
                var barcode = p.Barcode ?? await _codes.GenerateAsync(scope, V2Counter.Barcode, V2ProductService.BarcodePrefix, ct);
                if (_codes.SkuOwner(sku) is { } skuOwner)
                {
                    return CodeTaken($"{field}.sku", sku, skuOwner);
                }

                if (_codes.BarcodeOwner(barcode) is { } barcodeOwner)
                {
                    return CodeTaken($"{field}.barcode", barcode, barcodeOwner);
                }

                var id = await scope.InsertProductAsync(
                    new V2NewProduct(
                        p.Name, sku, V2Text.Normalize(sku), barcode, V2Text.Normalize(barcode), unitId, p.DecimalPlaces, p.SerialTracking,
                        [], null, actor.UserId, null, null, now),
                    ct);
                _codes.Add(new V2CodeOwner(id, sku, barcode, [], []));
                _products[id] = (await scope.FindProductRecordsAsync([id], ct))[id];
                _lineProducts[line.Index] = id;
                _created.Add(new V2SubjectRecord(SubjectTypes.Product, id, sku));
                scope.AddActivity(new V2ActivityEntry(
                    now, ActivityTypes.ProductCreate, $"入库时新增商品 {p.Name}（{sku}）", true, [new(SubjectTypes.Product, id, sku)],
                    actor.UserId, actor.DisplayName, actor.Role,
                    Changes: [new("name", null, p.Name), new("sku", null, sku), new("barcode", null, barcode),
                        new("decimalPlaces", null, p.DecimalPlaces.ToString(CultureInfo.InvariantCulture)),
                        new("serialTracking", null, p.SerialTracking.ToString())]));
            }

            return null;
        }

        private async Task<V2Failure?> LoadBinsAsync()
        {
            foreach (var line in plan.Lines)
            {
                foreach (var (binId, member) in new[] { (line.FromBinId, "fromBinId"), (line.ToBinId, "toBinId") })
                {
                    if (binId is not { } id || _bins.ContainsKey(id))
                    {
                        continue;
                    }

                    var bin = await scope.FindBinAsync(id, ct);
                    if (bin is null)
                    {
                        return V2Failure.NotFound("Bin", V2CatalogMapping.Ref(SubjectTypes.Bin, id)) with { Field = $"{line.Field}.{member}" };
                    }

                    _bins[id] = new V2BinState(
                        bin.Id, bin.Code, bin.ProductId, bin.QuantityMicros, bin.CapacityMicros, bin.LowThresholdMicros, bin.HighThresholdMicros,
                        archived: bin.IsArchived,
                        frozen: bin.FrozenCountTaskId is not null || bin.HasOpenCount,
                        investigationOpen: bin.HasUnresolvedInvestigation,
                        version: bin.Version);

                    // The real count task(s) behind a freeze or an open investigation, for problem subjects (none invented).
                    _binTasks[id] = new[] { bin.FrozenCountTaskId, bin.OpenCountTaskId }.OfType<long>().Distinct().ToList();
                }
            }

            return null;
        }

        private async Task<V2Failure?> LoadSerialItemsAsync()
        {
            var wanted = plan.Lines.SelectMany(l => l.SerialNumbers).Select(V2Text.Normalize).ToList();
            if (wanted.Count == 0)
            {
                return null;
            }

            foreach (var item in await scope.FindSerializedItemCandidatesAsync(wanted, ct))
            {
                var state = new ItemState(item.Id, item.SerialNumber, item.ProductId, item.BinId, item.Version, isNew: false);
                _current[V2Text.Normalize(item.SerialNumber)] = state;
                foreach (var old in item.HistoricalSerialNumbers)
                {
                    _superseded[V2Text.Normalize(old)] = state;
                }
            }

            return null;
        }

        // ---- one line ----

        private async Task<V2Failure?> ApplyAsync(V2LinePlan line)
        {
            var product = _products[_lineProducts[line.Index]];
            var f = line.Field;
            if (Quantity(line, product, out var quantity) is { } badQuantity)
            {
                return badQuantity;
            }

            var from = line.FromBinId is { } fromId ? _bins[fromId] : null;
            var to = line.ToBinId is { } toId ? _bins[toId] : null;
            var (fromBefore, toBefore) = (from?.QuantityMicros, to?.QuantityMicros);
            var outgoing = Type is TransactionTypesV2.Shipment or TransactionTypesV2.Movement
                || (Type == TransactionTypesV2.Adjustment && quantity < 0);
            var source = Type == TransactionTypesV2.Adjustment ? to : from;

            DomainError? error = null;
            V2BinState? failedBin = null;
            if (outgoing)
            {
                error = source!.Remove(product.Id, Math.Abs(quantity), f, Type == TransactionTypesV2.Adjustment ? "toBinId" : "fromBinId");
                failedBin = source;
            }

            var incoming = Type is TransactionTypesV2.Receipt or TransactionTypesV2.Movement || (Type == TransactionTypesV2.Adjustment && quantity > 0);
            if (error is null && incoming)
            {
                error = to!.Add(
                    product.Id, product.DecimalPlaces, Math.Abs(quantity), line.CapacityMicros, line.LowThresholdMicros, line.HighThresholdMicros,
                    _settings.LowThresholdMicros, _settings.HighThresholdMicros, f);
                failedBin = to;
            }

            if (error is not null)
            {
                // A freeze or an investigation names the count task(s) actually responsible for it.
                var tasks = error.Code is V2InventoryCodes.BinFrozen or V2InventoryCodes.InvestigationUnresolved
                    ? _binTasks[failedBin!.BinId].Select(task => V2CatalogMapping.Ref(SubjectTypes.CountTask, task))
                    : [];
                return V2Failure.From(error) with
                {
                    Subjects = [V2CatalogMapping.Ref(SubjectTypes.Bin, failedBin!.BinId, failedBin.Code),
                        V2CatalogMapping.Ref(SubjectTypes.Product, product.Id, product.Sku), .. tasks],
                };
            }

            var items = new List<V2LineItem>();
            if (product.SerialTracking && await SerialsAsync(line, product, outgoing, source, to, items) is { } badSerial)
            {
                return badSerial;
            }

            _lines.Add(new V2TransactionLineRecord(
                line.Index + 1, product.Id, product.Name, product.Sku, product.Barcode, product.UnitName, product.DecimalPlaces, quantity,
                from?.BinId, from?.Code, fromBefore, from?.QuantityMicros,
                to?.BinId, to?.Code, toBefore, to?.QuantityMicros,
                line.PackagingUnit, line.PackagingQuantityMicros, line.ConversionFactorMicros, items));
            return null;
        }

        /// <summary>The signed line quantity: entered, packaging product (exact, never rounded), or serial item count.</summary>
        private V2Failure? Quantity(V2LinePlan line, V2ProductRecord product, out long quantity)
        {
            quantity = 0;
            var f = line.Field;
            long? packaging = null;
            if (line.PackagingQuantityMicros is { } pq)
            {
                switch (V2Micro.Multiply(pq, line.ConversionFactorMicros!.Value, out var exact))
                {
                    case MultiplyOutcome.NeedsRounding:
                        return new V2Failure(
                            ProblemCodesV2.PrecisionExceeded,
                            "packagingQuantity × conversionFactor needs more than 6 decimal places; it is never rounded.", $"{f}.conversionFactor");
                    case MultiplyOutcome.OutOfRange:
                        return new V2Failure(ProblemCodesV2.QuantityOutOfRange, "packagingQuantity × conversionFactor is out of range.", $"{f}.conversionFactor");
                }

                if (line.QuantityMicros is { } entered && entered != exact)
                {
                    return V2Failure.Validation($"{f}.quantity", "quantity must equal packagingQuantity × conversionFactor.");
                }

                packaging = exact;
            }

            if (product.SerialTracking)
            {
                var count = (long)(line.GenerateSerialCount ?? line.SerialNumbers.Count) * V2Micro.PerUnit;
                if (count == 0)
                {
                    return new V2Failure(
                        ProblemCodesV2.SerialNumbersRequired, $"Product {product.Sku} is serial-tracked: name the items (or generate them).", $"{f}.serialNumbers");
                }

                if (Type == TransactionTypesV2.Adjustment && line.GenerateSerialCount is null)
                {
                    if (line.QuantityMicros is not { } signed)
                    {
                        return V2Failure.Validation($"{f}.quantity", "An adjustment of serial items needs the signed quantity (+n to add, -n to remove).");
                    }

                    if (Math.Abs(signed) != count)
                    {
                        return CountMismatch(f, count);
                    }

                    quantity = signed;
                    return null;
                }

                if ((line.QuantityMicros ?? packaging) is { } declared && declared != count)
                {
                    return CountMismatch(f, count);
                }

                quantity = count;
                return null;
            }

            if (line.SerialNumbers.Count > 0 || line.GenerateSerialCount is not null)
            {
                var known = line.SerialNumbers.Any(n => _current.TryGetValue(V2Text.Normalize(n), out var item) && item.ProductId == product.Id);
                return known
                    ? new V2Failure(
                        ProblemCodesV2.SerialTrackingDisabled,
                        $"Product {product.Sku} no longer tracks serial items; a known item cannot come back as such.", $"{f}.serialNumbers")
                    : new V2Failure(
                        ProblemCodesV2.SerialNumbersRequired, $"Product {product.Sku} is not serial-tracked: send no serial members.", $"{f}.serialNumbers");
            }

            if ((line.QuantityMicros ?? packaging) is not { } value)
            {
                return V2Failure.Validation($"{f}.quantity", "quantity is required (or packagingQuantity and conversionFactor).");
            }

            if (!V2Micro.FitsPrecision(Math.Abs(value), product.DecimalPlaces))
            {
                return new V2Failure(
                    ProblemCodesV2.PrecisionExceeded,
                    $"Product {product.Sku} allows {product.DecimalPlaces} decimal places; quantities are never rounded.", $"{f}.quantity");
            }

            quantity = value;
            return null;
        }

        private static V2Failure CountMismatch(string f, long count) =>
            new(ProblemCodesV2.SerialCountMismatch, $"The quantity must equal the number of serial items ({count / V2Micro.PerUnit}).", $"{f}.quantity");

        // ---- serial items ----

        private async Task<V2Failure?> SerialsAsync(
            V2LinePlan line, V2ProductRecord product, bool outgoing, V2BinState? source, V2BinState? to, List<V2LineItem> items)
        {
            var f = $"{line.Field}.serialNumbers";
            if (outgoing)
            {
                var target = Type == TransactionTypesV2.Movement ? to!.BinId : (long?)null;
                foreach (var number in line.SerialNumbers)
                {
                    var normalized = V2Text.Normalize(number);
                    if (Superseded(normalized, number, f) is { } old)
                    {
                        return old;
                    }

                    if (!_current.TryGetValue(normalized, out var item) || item.ProductId != product.Id || item.BinId != source!.BinId)
                    {
                        return new V2Failure(
                            ProblemCodesV2.SerialNotInBin,
                            $"Serial number {number} is not an in-stock item of {product.Sku} in bin {source!.Code}.", f,
                            [V2CatalogMapping.Ref(SubjectTypes.Bin, source.BinId, source.Code)]);
                    }

                    item.BinId = target;
                    items.Add(new V2LineItem(item.Id, item.SerialNumber));
                }

                return null;
            }

            var bin = to!.BinId;
            var fresh = new List<(string Number, string Normalized)>();
            if (line.GenerateSerialCount is { } generate)
            {
                _usedSerials ??= await UsedSerialsAsync();
                for (var i = 0; i < generate; i++)
                {
                    string number;
                    do
                    {
                        number = "SN-" + (await scope.NextNumberAsync(V2Counter.SerialNumber, ct)).ToString("D6", CultureInfo.InvariantCulture);
                    }
                    while (!_usedSerials.Add(V2Text.Normalize(number)));

                    fresh.Add((number, V2Text.Normalize(number)));
                }
            }

            foreach (var number in line.SerialNumbers)
            {
                var normalized = V2Text.Normalize(number);
                if (Superseded(normalized, number, f) is { } old)
                {
                    return old;
                }

                if (_current.TryGetValue(normalized, out var known))
                {
                    if (known.ProductId != product.Id)
                    {
                        return new V2Failure(
                            ProblemCodesV2.AlreadyExists, $"Serial number {number} belongs to another product.", f,
                            [V2CatalogMapping.Ref(SubjectTypes.SerializedItem, known.Id, known.SerialNumber)]);
                    }

                    if (known.BinId is not null)
                    {
                        return new V2Failure(
                            ProblemCodesV2.SerialAlreadyInStock, $"Serial item {number} is already in stock.", f,
                            [V2CatalogMapping.Ref(SubjectTypes.SerializedItem, known.Id, known.SerialNumber)]);
                    }

                    known.BinId = bin; // the same physical item comes back: same ID, continuous history
                    items.Add(new V2LineItem(known.Id, known.SerialNumber));
                    continue;
                }

                _usedSerials?.Add(normalized);
                fresh.Add((number, normalized));
            }

            if (fresh.Count > 0)
            {
                var ids = await scope.InsertSerializedItemsAsync(
                    fresh.Select(n => new V2NewSerializedItem(n.Number, n.Normalized, product.Id, bin)).ToList(), now, ct);
                for (var i = 0; i < fresh.Count; i++)
                {
                    var state = new ItemState(ids[i], fresh[i].Number, product.Id, bin, version: 1, isNew: true);
                    _current[fresh[i].Normalized] = state;
                    items.Add(new V2LineItem(ids[i], fresh[i].Number));
                }
            }

            return null;
        }

        private V2Failure? Superseded(string normalized, string number, string field) =>
            _superseded.TryGetValue(normalized, out var item)
                ? new V2Failure(
                    ProblemCodesV2.SerialNumberSuperseded,
                    $"Serial number {number} was corrected to {item.SerialNumber}; use the current number.", field,
                    [V2CatalogMapping.Ref(SubjectTypes.SerializedItem, item.Id, item.SerialNumber)])
                : null;

        private async Task<HashSet<string>> UsedSerialsAsync()
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in await scope.ListSerialNumbersAsync(ct))
            {
                used.Add(V2Text.Normalize(item.SerialNumber));
                foreach (var old in item.HistoricalSerialNumbers)
                {
                    used.Add(V2Text.Normalize(old));
                }
            }

            foreach (var line in plan.Lines)
            {
                foreach (var number in line.SerialNumbers)
                {
                    used.Add(V2Text.Normalize(number)); // never generate a number entered elsewhere in this request
                }
            }

            return used;
        }

        // ---- commit ----

        private async Task<V2Result<InventoryTransactionResponse>> PersistAsync()
        {
            var changedBins = _bins.Values.Where(b => b.Changed).ToList();
            foreach (var bin in changedBins)
            {
                if (!await scope.UpdateBinStockAsync(
                        new V2BinStockUpdate(
                            bin.BinId, bin.Version, bin.ProductId, bin.QuantityMicros, bin.CapacityMicros, bin.LowThresholdMicros, bin.HighThresholdMicros),
                        now, ct))
                {
                    return Conflict();
                }
            }

            var moves = _current.Values.Concat(_superseded.Values).Distinct()
                .Where(i => i.BinId != i.OriginalBinId)
                .Select(i => new V2SerializedItemMove(i.Id, i.Version, i.BinId))
                .ToList();
            if (moves.Count > 0 && !await scope.MoveSerializedItemsAsync(moves, now, ct))
            {
                return Conflict();
            }

            var number = "TX-" + (await scope.NextNumberAsync(V2Counter.Transaction, ct)).ToString("D6", CultureInfo.InvariantCulture);
            var id = await scope.InsertTransactionAsync(
                new V2NewTransaction(
                    number, Type, actor.UserId, actor.DisplayName, actor.Role, now, plan.Source, plan.Destination, plan.Reason, plan.CountResultId,
                    key, digest, _lines),
                ct);

            var alerts = await V2CapacityAlerts.ReconcileAsync(
                scope, changedBins.Select(b => b.BinId).ToList(), _settings.LowThresholdMicros, _settings.HighThresholdMicros, now, number, ct);

            var subjects = new List<V2SubjectRecord> { new(SubjectTypes.Transaction, id, number) };
            subjects.AddRange(_lines.Select(l => new V2SubjectRecord(SubjectTypes.Product, l.ProductId, l.Sku)));
            subjects.AddRange(_bins.Values.Select(b => new V2SubjectRecord(SubjectTypes.Bin, b.BinId, b.Code)));
            subjects.AddRange(_lines.SelectMany(l => l.Items).Select(i => new V2SubjectRecord(SubjectTypes.SerializedItem, i.SerializedItemId, i.SerialNumber)));
            if (plan.CountResultId is { } result)
            {
                subjects.Add(new V2SubjectRecord(SubjectTypes.CountResult, result));
            }

            subjects.AddRange(_created);
            subjects.AddRange(alerts.Subjects);
            scope.AddActivity(new V2ActivityEntry(
                now, activity, $"{label} {number}：{_lines.Count} 行", true, subjects.Distinct().ToList(), actor.UserId, actor.DisplayName, actor.Role));
            return V2InventoryMapping.ToResponse((await scope.FindTransactionAsync(id, ct))!);
        }

        private static V2Failure Conflict() =>
            new(ProblemCodesV2.ConcurrencyConflict, "A bin or item changed underneath this operation; nothing was committed. Retry.");

        private static V2Failure CodeTaken(string field, string code, V2CodeOwner owner) =>
            new(
                ProblemCodesV2.AlreadyExists,
                $"'{code}' is used now or was used before (codes are never reused, archived products included).",
                field,
                [V2CatalogMapping.Ref(SubjectTypes.Product, owner.ProductId, owner.Sku)]);
    }

    /// <summary>Working state of one serial item inside the transaction (position changes are applied once at commit).</summary>
    private sealed class ItemState(long id, string serialNumber, long productId, long? binId, int version, bool isNew)
    {
        public long Id { get; } = id;
        public string SerialNumber { get; } = serialNumber;
        public long ProductId { get; } = productId;
        public long? OriginalBinId { get; } = binId;
        public long? BinId { get; set; } = binId;
        public int Version { get; } = version;
        public bool IsNew { get; } = isNew;
    }
}
