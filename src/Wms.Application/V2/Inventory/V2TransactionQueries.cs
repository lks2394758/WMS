using System.Globalization;
using Wms.Application.V2.Catalog;
using Wms.Application.V2.Common;
using Wms.Contracts.V2.Common;
using Wms.Contracts.V2.Inventory;
using Wms.Contracts.V2.Monitoring;
using Wms.Persistence.Abstractions.V2;

namespace Wms.Application.V2.Inventory;

/// <summary>
/// The immutable ledger (contracts/v2/README.md §2): newest first; filters <c>type</c>, <c>from</c> (inclusive),
/// <c>to</c> (exclusive), <c>productId</c>, <c>binId</c>, <c>actorId</c> in SQL; <c>search</c> (number, source,
/// destination, reason, line product names and SKUs) by Unicode invariant upper case before the page is cut.
/// </summary>
public sealed class V2TransactionQueries(IV2InventoryReader reader)
{
    public async Task<V2Result<Page<TransactionListItem>>> ListAsync(
        string? search, string? type, string? from, string? to, string? productId, string? binId, string? actorId,
        string? cursor, int? limit, CancellationToken cancellationToken)
    {
        if (V2Paging.Parse(cursor, limit, out var before, out var size) is { } badPage)
        {
            return badPage;
        }

        if (type is not null && !TransactionTypesV2.All.Contains(type, StringComparer.Ordinal))
        {
            return V2Failure.Validation("type", "Unknown transaction type.");
        }

        var badFrom = Time(from, "from", out var fromTime);
        var badTo = Time(to, "to", out var toTime);
        var badProduct = OptionalId(productId, "productId", out var product);
        var badBin = OptionalId(binId, "binId", out var bin);
        var badActor = OptionalId(actorId, "actorId", out var actor);
        if ((badFrom ?? badTo ?? badProduct ?? badBin ?? badActor) is { } invalid)
        {
            return invalid;
        }

        var text = string.IsNullOrWhiteSpace(search) ? null : V2Text.Normalize(search);
        var batch = text is null ? size + 1 : Math.Max(100, (size + 1) * 2);
        var page = new List<V2TransactionSummary>(size + 1);
        long? cursorId = before;
        while (page.Count <= size)
        {
            var rows = await reader.ListTransactionsAsync(
                new V2TransactionFilter(type, fromTime, toTime, product, bin, actor, cursorId, batch), cancellationToken);
            foreach (var row in rows)
            {
                if (text is null || Matches(row, text))
                {
                    page.Add(row);
                    if (page.Count > size)
                    {
                        break;
                    }
                }
            }

            if (rows.Count < batch)
            {
                break;
            }

            cursorId = rows[^1].Id;
        }

        return new Page<TransactionListItem>(
            page.Take(size).Select(V2InventoryMapping.ToListItem).ToList(),
            page.Count > size ? V2Paging.Encode(page[size - 1].Id) : null);
    }

    public async Task<V2Result<InventoryTransactionResponse>> GetAsync(string id, CancellationToken cancellationToken)
    {
        if (V2Text.Id(id, "id", out var transactionId) is { } invalid)
        {
            return invalid;
        }

        var transaction = await reader.FindTransactionAsync(transactionId, cancellationToken);
        if (transaction is null)
        {
            return V2Failure.NotFound("Transaction", V2CatalogMapping.Ref(SubjectTypes.Transaction, transactionId));
        }

        return V2InventoryMapping.ToResponse(transaction);
    }

    private static bool Matches(V2TransactionSummary row, string text) =>
        new[] { row.Number, row.Source, row.Destination, row.Reason }.Concat(row.ProductTexts)
            .Any(value => value is not null && V2Text.Normalize(value).Contains(text, StringComparison.Ordinal));

    private static V2Failure? Time(string? value, string field, out DateTimeOffset? time)
    {
        time = null;
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            return V2Failure.Validation(field, $"{field} must be an ISO 8601 time.");
        }

        time = parsed;
        return null;
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
