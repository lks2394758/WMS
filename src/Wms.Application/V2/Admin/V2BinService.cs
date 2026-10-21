using System.Globalization;
using Wms.Application.V2.Auth;
using Wms.Application.V2.Catalog;
using Wms.Application.V2.Common;
using Wms.Contracts.V2.Auth;
using Wms.Contracts.V2.Bins;
using Wms.Contracts.V2.Common;
using Wms.Contracts.V2.Monitoring;
using Wms.Persistence.Abstractions.V2;

namespace Wms.Application.V2.Admin;

/// <summary>
/// Bins and racks (decisions §3, §17.3; contracts/v2/README.md §5):
/// <list type="bullet">
/// <item>zone, rack, level, cell: 1-16 characters each, no whitespace, control characters or '-'; code = the four parts
/// joined by '-';</item>
/// <item>a position is unique by Unicode invariant upper case against every current and historical code of every bin,
/// archived included; historical codes are kept forever and never address a bin;</item>
/// <item>moving or archiving a bin needs zero stock, no frozen or open count and no unresolved investigation;</item>
/// <item>a rack is generated as a whole from the given or current default sizes (1-100 each), levels and cells labelled
/// 01, 02, …, 100; any taken position rejects the whole rack.</item>
/// </list>
/// </summary>
public sealed class V2BinService(IV2AdminReader reader, V2CatalogWriter writer)
{
    public const int MaxPart = 16;
    public const int MaxRackSize = 100;
    private const int MaxConflictSubjects = 50;

    public async Task<V2Result<Page<BinResponse>>> ListAsync(
        string? search, string? zone, string? rack, string? level, string? productId, string? cursor, int? limit,
        CancellationToken cancellationToken)
    {
        if (V2Paging.Parse(cursor, limit, out var after, out var size) is { } invalid)
        {
            return invalid;
        }

        long? product = null;
        if (!string.IsNullOrEmpty(productId))
        {
            if (V2Text.Id(productId, "productId", out var parsed) is { } badProduct)
            {
                return badProduct;
            }

            product = parsed;
        }

        // Matching uses Unicode invariant upper case in code (SQLite folds ASCII only); the page is cut after matching,
        // in code order, so paging stays exact.
        var text = Normalized(search);
        var (zoneKey, rackKey, levelKey) = (Normalized(zone), Normalized(rack), Normalized(level));
        var settings = await reader.GetSettingsAsync(cancellationToken);
        var page = new List<V2BinRecord>(size + 1);
        var started = after is null;
        await foreach (var bin in reader.StreamBinsAsync(product, cancellationToken))
        {
            if (!started)
            {
                started = bin.Id == after;
                continue;
            }

            if ((zoneKey is null || V2Text.Normalize(bin.Zone) == zoneKey)
                && (rackKey is null || V2Text.Normalize(bin.Rack) == rackKey)
                && (levelKey is null || V2Text.Normalize(bin.Level) == levelKey)
                && (text is null
                    || V2Text.Normalize(bin.Code).Contains(text, StringComparison.Ordinal)
                    || (bin.ProductName is not null && V2Text.Normalize(bin.ProductName).Contains(text, StringComparison.Ordinal))
                    || (bin.Sku is not null && V2Text.Normalize(bin.Sku).Contains(text, StringComparison.Ordinal))))
            {
                page.Add(bin);
                if (page.Count > size)
                {
                    break;
                }
            }
        }

        var items = page.Take(size).Select(b => ToResponse(b, settings)).ToList();
        return new Page<BinResponse>(items, page.Count > size ? V2Paging.Encode(page[size - 1].Id) : null);
    }

    public async Task<V2Result<BinResponse>> GetAsync(string id, CancellationToken cancellationToken)
    {
        if (V2Text.Id(id, "id", out var binId) is { } invalid)
        {
            return invalid;
        }

        var bin = await reader.FindBinAsync(binId, cancellationToken);
        if (bin is null || bin.IsArchived)
        {
            return V2Failure.NotFound("Bin", V2CatalogMapping.Ref(SubjectTypes.Bin, binId));
        }

        return ToResponse(bin, await reader.GetSettingsAsync(cancellationToken));
    }

    public async Task<V2Result<BinResponse>> CreateAsync(V2SessionPrincipal principal, BinWriteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Position(request.Zone, request.Rack, request.Level, request.Cell) is { } invalid)
        {
            return await writer.RejectAsync<BinResponse>(
                principal, PermissionNames.CatalogWrite, ActivityTypes.BinCreate, "新增库位", [], invalid, cancellationToken);
        }

        var code = CodeOf(request.Zone, request.Rack, request.Level, request.Cell);
        return await writer.RunAsync<BinResponse>(
            principal, PermissionNames.CatalogWrite, ActivityTypes.BinCreate, $"新增库位 {code}", [],
            async (scope, actor, now) =>
            {
                var codes = new BinCodeIndex(await scope.ListBinCodesAsync(cancellationToken));
                if (codes.Owner(code) is { } owner)
                {
                    return Taken([code], [owner]);
                }

                var id = (await scope.InsertBinsAsync(
                    [new V2NewBin(request.Zone, request.Rack, request.Level, request.Cell, code, V2Text.Normalize(code))], now, cancellationToken))[0];
                scope.AddActivity(new V2ActivityEntry(
                    now, ActivityTypes.BinCreate, $"新增库位 {code}", true, [new(SubjectTypes.Bin, id, code)],
                    actor.UserId, actor.DisplayName, actor.Role));
                return ToResponse((await scope.FindBinAsync(id, cancellationToken))!, await scope.GetSettingsAsync(cancellationToken));
            },
            cancellationToken);
    }

    public async Task<V2Result<BinResponse>> UpdateAsync(
        V2SessionPrincipal principal, string id, BinWriteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var badId = V2Text.Id(id, "id", out var binId);
        var rejected = badId
            ?? Position(request.Zone, request.Rack, request.Level, request.Cell)
            ?? (request.Version is null ? V2Failure.Validation("version", "version is required to move a bin.") : null);
        if (rejected is not null)
        {
            IReadOnlyList<V2SubjectRecord> subjects = binId > 0 ? [new(SubjectTypes.Bin, binId)] : [];
            return await writer.RejectAsync<BinResponse>(
                principal, PermissionNames.CatalogWrite, ActivityTypes.BinUpdate, "修改库位", subjects, rejected, cancellationToken);
        }

        var version = request.Version!.Value;
        var code = CodeOf(request.Zone, request.Rack, request.Level, request.Cell);
        var subject = V2CatalogMapping.Ref(SubjectTypes.Bin, binId);
        return await writer.RunAsync<BinResponse>(
            principal, PermissionNames.CatalogWrite, ActivityTypes.BinUpdate, $"修改库位为 {code}", [new(SubjectTypes.Bin, binId)],
            async (scope, actor, now) =>
            {
                var bin = await scope.FindBinAsync(binId, cancellationToken);
                if (bin is null)
                {
                    return V2Failure.NotFound("Bin", subject);
                }

                if (bin.IsArchived)
                {
                    return new V2Failure(ProblemCodesV2.Archived, "The bin is archived.", Subjects: [subject]);
                }

                if (bin.Version != version)
                {
                    return V2Failure.VersionConflict(subject);
                }

                if (Blocked(bin, subject, "moved") is { } blocked)
                {
                    return blocked;
                }

                var moved = V2Text.Normalize(code) != V2Text.Normalize(bin.Code);
                if (moved && new BinCodeIndex(await scope.ListBinCodesAsync(cancellationToken)).Owner(code) is { } owner)
                {
                    return Taken([code], [owner]);
                }

                var history = moved ? [.. bin.HistoricalCodes, bin.Code] : bin.HistoricalCodes;
                if (!await scope.UpdateBinPositionAsync(
                        new V2BinPositionUpdate(
                            binId, version, request.Zone, request.Rack, request.Level, request.Cell, code, V2Text.Normalize(code), history, now),
                        cancellationToken))
                {
                    return V2Failure.VersionConflict(subject);
                }

                scope.AddActivity(new V2ActivityEntry(
                    now, ActivityTypes.BinUpdate, $"修改库位 {bin.Code} → {code}", true, [new(SubjectTypes.Bin, binId, code)],
                    actor.UserId, actor.DisplayName, actor.Role, Changes: [new("code", bin.Code, code)]));
                return ToResponse((await scope.FindBinAsync(binId, cancellationToken))!, await scope.GetSettingsAsync(cancellationToken));
            },
            cancellationToken);
    }

    public async Task<V2Result<V2None>> ArchiveAsync(
        V2SessionPrincipal principal, string id, DeleteRequest? request, CancellationToken cancellationToken)
    {
        if (V2Text.Id(id, "id", out var binId) is { } badId)
        {
            return await writer.RejectAsync<V2None>(
                principal, PermissionNames.CatalogWrite, ActivityTypes.BinArchive, "归档库位", [], badId, cancellationToken);
        }

        var subject = V2CatalogMapping.Ref(SubjectTypes.Bin, binId);
        return await writer.RunAsync<V2None>(
            principal, PermissionNames.CatalogWrite, ActivityTypes.BinArchive, "归档库位", [new(SubjectTypes.Bin, binId)],
            async (scope, actor, now) =>
            {
                var bin = await scope.FindBinAsync(binId, cancellationToken);
                if (bin is null || bin.IsArchived)
                {
                    return V2Failure.NotFound("Bin", subject);
                }

                if (request?.Version is { } version && version != bin.Version)
                {
                    return V2Failure.VersionConflict(subject);
                }

                if (Blocked(bin, subject, "archived") is { } blocked)
                {
                    return blocked;
                }

                if (!await scope.ArchiveBinAsync(binId, bin.Version, now, cancellationToken))
                {
                    return V2Failure.VersionConflict(subject);
                }

                var alerts = await scope.ResolveBinCapacityAlertsAsync(binId, now, "库位已归档", cancellationToken);
                scope.AddActivity(new V2ActivityEntry(
                    now, ActivityTypes.BinArchive, $"归档库位 {bin.Code}", true,
                    [new(SubjectTypes.Bin, binId, bin.Code), .. alerts.Select(a => new V2SubjectRecord(SubjectTypes.Alert, a))],
                    actor.UserId, actor.DisplayName, actor.Role, Changes: [new("archived", "false", "true")]));
                return V2None.Value;
            },
            cancellationToken);
    }

    public async Task<V2Result<RackCreateResponse>> CreateRackAsync(
        V2SessionPrincipal principal, RackCreateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var rejected = Part(request.Zone, "zone") ?? Part(request.Rack, "rack")
            ?? Size(request.Levels, "levels") ?? Size(request.CellsPerLevel, "cellsPerLevel");
        if (rejected is not null)
        {
            return await writer.RejectAsync<RackCreateResponse>(
                principal, PermissionNames.CatalogWrite, ActivityTypes.RackCreate, "生成货架", [], rejected, cancellationToken);
        }

        return await writer.RunAsync<RackCreateResponse>(
            principal, PermissionNames.CatalogWrite, ActivityTypes.RackCreate, $"生成货架 {request.Zone}-{request.Rack}", [],
            async (scope, actor, now) =>
            {
                // Omitted sizes come from the settings as they are inside this transaction.
                var settings = await scope.GetSettingsAsync(cancellationToken);
                var levels = request.Levels ?? settings.DefaultRackLevels;
                var cells = request.CellsPerLevel ?? settings.DefaultCellsPerLevel;
                var bins = new List<V2NewBin>(levels * cells);
                for (var l = 1; l <= levels; l++)
                {
                    for (var c = 1; c <= cells; c++)
                    {
                        var (level, cell) = (Label(l), Label(c));
                        var code = CodeOf(request.Zone, request.Rack, level, cell);
                        bins.Add(new V2NewBin(request.Zone, request.Rack, level, cell, code, V2Text.Normalize(code)));
                    }
                }

                var index = new BinCodeIndex(await scope.ListBinCodesAsync(cancellationToken));
                var conflicts = bins.Select(b => (b.Code, Owner: index.Owner(b.Code))).Where(x => x.Owner is not null).ToList();
                if (conflicts.Count > 0)
                {
                    return Taken(conflicts.Select(c => c.Code).ToList(), conflicts.Select(c => c.Owner!).ToList());
                }

                var ids = await scope.InsertBinsAsync(bins, now, cancellationToken);
                scope.AddActivity(new V2ActivityEntry(
                    now, ActivityTypes.RackCreate,
                    $"生成货架 {request.Zone}-{request.Rack}：{levels} 层 × {cells} 格，{bins.Count} 个库位（{bins[0].Code} … {bins[^1].Code}）", true,
                    // Every created bin is a subject (up to 100 × 100 = 10 000), so any bin's history finds this entry.
                    bins.Select((bin, i) => new V2SubjectRecord(SubjectTypes.Bin, ids[i], bin.Code)).ToList(),
                    actor.UserId, actor.DisplayName, actor.Role));
                return new RackCreateResponse(request.Zone, request.Rack, levels, cells, bins.Count, bins[0].Code, bins[^1].Code);
            },
            cancellationToken);
    }

    public static BinResponse ToResponse(V2BinRecord bin, V2SettingsRecord settings)
    {
        ArgumentNullException.ThrowIfNull(bin);
        ArgumentNullException.ThrowIfNull(settings);
        var low = bin.LowThresholdMicros ?? settings.LowThresholdMicros;
        var high = bin.HighThresholdMicros ?? settings.HighThresholdMicros;
        var status = bin.ProductId is null ? CapacityStatuses.NotConfigured : V2Quantities.Status(bin.QuantityMicros, bin.CapacityMicros, low, high);
        return new BinResponse(
            WireId.Format(bin.Id),
            bin.Code,
            bin.Zone,
            bin.Rack,
            bin.Level,
            bin.Cell,
            WireQuantity.Format(bin.QuantityMicros),
            status,
            WireQuantity.Format(low),
            WireQuantity.Format(high),
            bin.HasUnresolvedInvestigation,
            bin.HistoricalCodes,
            bin.Version,
            bin.ProductId is { } product ? WireId.Format(product) : null,
            bin.ProductName,
            bin.Sku,
            bin.UnitName,
            bin.DecimalPlaces,
            bin.SerialTracking,
            bin.CapacityMicros is { } capacity ? WireQuantity.Format(capacity) : null,
            bin.ProductId is null ? null : V2Quantities.Occupancy(bin.QuantityMicros, bin.CapacityMicros),
            bin.LowThresholdMicros is { } ownLow ? WireQuantity.Format(ownLow) : null,
            bin.HighThresholdMicros is { } ownHigh ? WireQuantity.Format(ownHigh) : null,
            bin.FrozenCountTaskId is { } task ? WireId.Format(task) : null);
    }

    public static string CodeOf(string zone, string rack, string level, string cell) => $"{zone}-{rack}-{level}-{cell}";

    /// <summary>Level/cell label: 1-based, at least two digits (01 … 99, 100).</summary>
    public static string Label(int number) => number.ToString("D2", CultureInfo.InvariantCulture);

    private static string? Normalized(string? value) => string.IsNullOrWhiteSpace(value) ? null : V2Text.Normalize(value);

    private static V2Failure? Position(string? zone, string? rack, string? level, string? cell) =>
        Part(zone, "zone") ?? Part(rack, "rack") ?? Part(level, "level") ?? Part(cell, "cell");

    private static V2Failure? Part(string? value, string field) =>
        value is { Length: >= 1 and <= MaxPart } && !value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == '-')
            ? null
            : V2Failure.Validation(field, $"{field} must be 1-{MaxPart} characters without spaces, control characters or '-'.");

    private static V2Failure? Size(int? value, string field) =>
        value is null or (>= 1 and <= MaxRackSize) ? null : V2Failure.Validation(field, $"{field} must be 1-{MaxRackSize}.");

    /// <summary>
    /// Moving or archiving (master-data changes) needs an empty bin with nothing open in counts or investigations:
    /// <c>STOCK_NOT_ZERO</c>, else <c>OPEN_COUNT_OR_INVESTIGATION</c> for a frozen or open count and for an unresolved
    /// investigation alike (contracts/v2/README.md §1.3). <c>BIN_FROZEN</c> and <c>INVESTIGATION_UNRESOLVED</c> are
    /// reserved for inventory operations. The subjects name the bin and the count task concerned.
    /// </summary>
    private static V2Failure? Blocked(V2BinRecord bin, SubjectRef subject, string action)
    {
        if (bin.QuantityMicros != 0)
        {
            return new V2Failure(ProblemCodesV2.StockNotZero, $"The bin holds stock and cannot be {action}.", Subjects: [subject]);
        }

        var countOpen = bin.FrozenCountTaskId is not null || bin.HasOpenCount;
        if (!countOpen && !bin.HasUnresolvedInvestigation)
        {
            return null;
        }

        var tasks = new[] { bin.FrozenCountTaskId, bin.OpenCountTaskId }
            .OfType<long>()
            .Distinct()
            .Select(task => V2CatalogMapping.Ref(SubjectTypes.CountTask, task));
        return new V2Failure(
            ProblemCodesV2.OpenCountOrInvestigation,
            countOpen
                ? $"A count is open on the bin (it is frozen); it cannot be {action}."
                : $"A count investigation of the bin is unresolved; it cannot be {action}.",
            Subjects: [subject, .. tasks]);
    }

    private static V2Failure Taken(IReadOnlyList<string> codes, IReadOnlyList<V2BinCodeOwner> owners) =>
        new(
            ProblemCodesV2.AlreadyExists,
            codes.Count == 1
                ? $"Position {codes[0]} is used now or was used before (positions are never reused, archived bins included)."
                : $"{codes.Count} positions of the rack are used now or were used before; nothing was created.",
            codes.Count == 1 ? "code" : null,
            owners.DistinctBy(o => o.BinId).Take(MaxConflictSubjects)
                .Select(o => V2CatalogMapping.Ref(SubjectTypes.Bin, o.BinId, o.Code)).ToList());

    /// <summary>Every code ever used by a bin (current and historical, archived included), normalized.</summary>
    private sealed class BinCodeIndex
    {
        private readonly Dictionary<string, V2BinCodeOwner> _codes = new(StringComparer.Ordinal);

        public BinCodeIndex(IReadOnlyList<V2BinCodeOwner> owners)
        {
            foreach (var owner in owners)
            {
                foreach (var code in owner.HistoricalCodes.Prepend(owner.Code))
                {
                    _codes.TryAdd(V2Text.Normalize(code), owner);
                }
            }
        }

        public V2BinCodeOwner? Owner(string code) => _codes.GetValueOrDefault(V2Text.Normalize(code));
    }
}
