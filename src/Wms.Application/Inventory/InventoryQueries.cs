using Wms.Application.Common;
using Wms.Contracts.Common;
using Wms.Contracts.Inventory;
using Wms.Domain.Common;
using Wms.Persistence.Abstractions;

namespace Wms.Application.Inventory;

/// <summary>Committed balance reads. Results reflect the last committed transaction, never pending client state.</summary>
public sealed class InventoryQueries(IInventoryReader inventory)
{
    public async Task<Result<CursorPage<InventoryBalanceResponse>>> ListBalancesAsync(
        Guid? productId, Guid? locationId, string? cursor, int? limit, CancellationToken cancellationToken)
    {
        var page = Paging.Parse(cursor, limit);
        if (page.IsFailure)
        {
            return page.Error!;
        }

        var filter = new BalanceFilter(
            productId.HasValue ? new ProductId(productId.Value) : (ProductId?)null,
            locationId.HasValue ? new LocationId(locationId.Value) : (LocationId?)null);
        var balances = await inventory.ListBalancesAsync(filter, page.Value, cancellationToken);
        return Paging.ToPage(balances, b => ContractMapper.ToResponse(b));
    }
}
