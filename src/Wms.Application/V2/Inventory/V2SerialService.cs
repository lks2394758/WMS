using Wms.Application.V2.Auth;
using Wms.Application.V2.Catalog;
using Wms.Application.V2.Common;
using Wms.Contracts.V2.Auth;
using Wms.Contracts.V2.Common;
using Wms.Contracts.V2.Inventory;
using Wms.Contracts.V2.Monitoring;
using Wms.Persistence.Abstractions.V2;

namespace Wms.Application.V2.Inventory;

/// <summary>
/// Serialized items (decisions §5, §13 "实物序列号纠错" to "旧序列号的扫描与操作", §17.3; contracts/v2/README.md §8).
/// <list type="bullet">
/// <item>List: exact <c>productId</c>/<c>binId</c>/<c>inStock</c> filters in SQL; <c>search</c> matches current and corrected
/// (historical) numbers by Unicode invariant upper case, before the page is cut; a row found only by an old number
/// carries it as <c>matchedHistoricalSerialNumber</c>.</item>
/// <item>Detail: the item, its product (archived ones included) and bin, the ledger lines that touched it with the number
/// and bin codes as they were then, and its corrections from the immutable activity log.</item>
/// <item>Correction (<c>SerialNumbers.Correct</c> of the current role): new number 1-128 characters without control
/// characters or surrounding spaces, unique against every current and historical number of every item (the item's own
/// old numbers included, Unicode case-insensitive); reason required; the current version is required and checked first
/// (a stale version is <c>VERSION_CONFLICT</c> whatever the number). The old number is appended to the history once and
/// stays reserved; the item ID, product, bin, stock flag, bin quantities and every earlier snapshot are untouched. With the
/// current version, the current number again is a no-op (no log, no history entry, no version change).</item>
/// </list>
/// </summary>
public sealed class V2SerialService(IV2SerialReader reader, V2CatalogWriter writer)
{
    public const int MaxReason = 2000;

    public async Task<V2Result<Page<SerializedItemListItem>>> ListAsync(
        string? search, string? productId, string? binId, bool? inStock, string? cursor, int? limit, CancellationToken cancellationToken)
    {
        if (V2Paging.Parse(cursor, limit, out var after, out var size) is { } badPage)
        {
            return badPage;
        }

        var badProduct = OptionalId(productId, "productId", out var product);
        var badBin = OptionalId(binId, "binId", out var bin);
        if ((badProduct ?? badBin) is { } invalid)
        {
            return invalid;
        }

        var text = string.IsNullOrWhiteSpace(search) ? null : V2Text.Normalize(search);
        var page = new List<(V2SerializedItemView View, string? Matched)>(size + 1);
        await foreach (var view in reader.StreamSerializedItemsAsync(product, bin, inStock, after, cancellationToken))
        {
            string? matched = null;
            if (text is not null && !V2Text.Normalize(view.Item.SerialNumber).Contains(text, StringComparison.Ordinal))
            {
                matched = view.Item.HistoricalSerialNumbers.FirstOrDefault(old => V2Text.Normalize(old).Contains(text, StringComparison.Ordinal));
                if (matched is null)
                {
                    continue;
                }
            }

            page.Add((view, matched));
            if (page.Count > size)
            {
                break;
            }
        }

        return new Page<SerializedItemListItem>(
            page.Take(size).Select(x => ToListItem(x.View, x.Matched)).ToList(),
            page.Count > size ? V2Paging.Encode(page[size - 1].View.Item.Id) : null);
    }

    public async Task<V2Result<SerializedItemResponse>> GetAsync(string id, CancellationToken cancellationToken)
    {
        if (V2Text.Id(id, "id", out var itemId) is { } invalid)
        {
            return invalid;
        }

        var detail = await reader.FindSerializedItemAsync(itemId, cancellationToken);
        if (detail is null)
        {
            return V2Failure.NotFound("Serialized item", V2CatalogMapping.Ref(SubjectTypes.SerializedItem, itemId));
        }

        return ToResponse(detail);
    }

    public async Task<V2Result<SerializedItemResponse>> CorrectAsync(
        V2SessionPrincipal principal, string id, SerialCorrectionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var badId = V2Text.Id(id, "id", out var itemId);
        var badRequest = Validate(request, out var number, out var reason);
        var rejected = badId ?? badRequest;
        IReadOnlyList<V2SubjectRecord> requestSubjects = itemId > 0 ? [new(SubjectTypes.SerializedItem, itemId)] : [];
        if (rejected is not null)
        {
            return await writer.RejectAsync<SerializedItemResponse>(
                principal, PermissionNames.SerialNumbersCorrect, ActivityTypes.SerialCorrect, "序列号纠错", requestSubjects, rejected, cancellationToken);
        }

        var subject = V2CatalogMapping.Ref(SubjectTypes.SerializedItem, itemId);
        var result = await writer.RunAsync<long>(
            principal, PermissionNames.SerialNumbersCorrect, ActivityTypes.SerialCorrect, "序列号纠错", requestSubjects,
            async (scope, actor, now) =>
            {
                var item = await scope.FindSerializedItemAsync(itemId, cancellationToken);
                if (item is null)
                {
                    return V2Failure.NotFound("Serialized item", subject);
                }

                // The contract's version rule comes first (contracts/v2/README.md §1): any stale version is 409, also when
                // the requested number is already the current one.
                if (item.Version != request.Version)
                {
                    return V2Failure.VersionConflict(subject);
                }

                if (string.Equals(item.SerialNumber, number, StringComparison.Ordinal))
                {
                    return itemId; // current version and already the current number: nothing to change, nothing to log
                }

                // Every number ever used by any item, this item's current and old numbers included, stays reserved.
                var normalized = V2Text.Normalize(number);
                foreach (var other in await scope.ListSerialNumbersAsync(cancellationToken))
                {
                    if (other.HistoricalSerialNumbers.Prepend(other.SerialNumber).Any(used => V2Text.Normalize(used) == normalized))
                    {
                        return new V2Failure(
                            ProblemCodesV2.AlreadyExists,
                            other.Id == itemId
                                ? $"{number} is (or was) a number of this item; numbers are never reused."
                                : $"{number} is used now or was used before by another item; numbers are never reused.",
                            "serialNumber",
                            [V2CatalogMapping.Ref(SubjectTypes.SerializedItem, other.Id, other.SerialNumber)]);
                    }
                }

                if (!await scope.CorrectSerialNumberAsync(
                        itemId, item.Version, number, normalized, [.. item.HistoricalSerialNumbers, item.SerialNumber], now, cancellationToken))
                {
                    return V2Failure.VersionConflict(subject);
                }

                var subjects = new List<V2SubjectRecord> { new(SubjectTypes.SerializedItem, itemId, number) };
                if ((await scope.FindProductRecordsAsync([item.ProductId], cancellationToken)).TryGetValue(item.ProductId, out var product))
                {
                    subjects.Add(new V2SubjectRecord(SubjectTypes.Product, product.Id, product.Sku));
                }

                if (item.BinId is { } binId && await scope.FindBinAsync(binId, cancellationToken) is { } bin)
                {
                    subjects.Add(new V2SubjectRecord(SubjectTypes.Bin, bin.Id, bin.Code));
                }

                scope.AddActivity(new V2ActivityEntry(
                    now, ActivityTypes.SerialCorrect, $"序列号纠错 {item.SerialNumber} → {number}", true, subjects,
                    actor.UserId, actor.DisplayName, actor.Role,
                    Changes: [new("serialNumber", item.SerialNumber, number), new("reason", null, reason)]));
                return itemId;
            },
            cancellationToken);

        if (!result.IsSuccess)
        {
            return result.Failure!;
        }

        // The committed state (or the unchanged one of a no-op), read like any detail.
        return await GetAsync(id, cancellationToken);
    }

    private static V2Failure? Validate(SerialCorrectionRequest request, out string number, out string reason)
    {
        number = request.SerialNumber ?? string.Empty;
        reason = (request.Reason ?? string.Empty).Trim();
        if (V2Text.Code(number, "serialNumber") is { } badNumber)
        {
            return badNumber;
        }

        if (reason.Length == 0 || reason.Length > MaxReason)
        {
            return V2Failure.Validation("reason", $"reason is required (at most {MaxReason} characters).");
        }

        return request.Version < 1 ? V2Failure.Validation("version", "The current version of the item is required.") : null;
    }

    private static SerializedItemListItem ToListItem(V2SerializedItemView view, string? matched)
    {
        var item = view.Item;
        return new SerializedItemListItem(
            WireId.Format(item.Id), item.SerialNumber, WireId.Format(item.ProductId), view.ProductName, view.Sku, item.InStock,
            item.HistoricalSerialNumbers, item.Version,
            item.BinId is { } bin ? WireId.Format(bin) : null, view.BinCode, matched);
    }

    private static SerializedItemResponse ToResponse(V2SerializedItemDetail detail)
    {
        var item = detail.View.Item;
        return new SerializedItemResponse(
            WireId.Format(item.Id),
            item.SerialNumber,
            WireId.Format(item.ProductId),
            detail.View.ProductName,
            detail.View.Sku,
            item.InStock,
            item.HistoricalSerialNumbers,
            detail.History.Select(h => new SerialHistoryEntry(
                WireId.Format(h.TransactionId), h.TransactionNumber, h.Type, h.OccurredAt, h.ActorName, h.ActorRole, h.SerialNumber,
                h.FromBinCode, h.ToBinCode)).ToList(),
            detail.Corrections.Select(c => new SerialCorrectionEntry(
                WireId.Format(c.ActivityLogId), c.PreviousSerialNumber, c.NewSerialNumber, c.Reason, c.ActorName, c.ActorRole, c.OccurredAt)).ToList(),
            item.Version,
            item.BinId is { } bin ? WireId.Format(bin) : null,
            detail.View.BinCode);
    }

    private static V2Failure? OptionalId(string? value, string field, out long? id)
    {
        id = null;
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (V2Text.Id(value, field, out var parsed) is { } bad)
        {
            return bad;
        }

        id = parsed;
        return null;
    }
}
