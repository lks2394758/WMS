using Wms.Domain.Catalog;
using Wms.Domain.Common;

namespace Wms.Domain.Inventory;

/// <summary>
/// Applies one request line to locked balances and returns the ledger line. Every check runs before any balance
/// changes. If a later line of the same request fails, the caller discards all balances it loaded; the database
/// transaction scope rolls back.
/// </summary>
public static class InventoryOperations
{
    public static Result<InventoryTransactionLine> Receive(
        int lineNo, Product product, Location target, InventoryBalance targetBalance, Quantity quantity)
    {
        EnsureBalance(targetBalance, product, target, nameof(targetBalance));

        var check = Check(RequirePositive(quantity), RequireActive(product), RequireActive(target));
        if (check.IsFailure)
        {
            return check.Error!;
        }

        var change = targetBalance.Increase(quantity);
        if (change.IsFailure)
        {
            return change.Error!;
        }

        return new InventoryTransactionLine(lineNo, product.Id, null, target.Id, quantity.Value, null, change.Value);
    }

    public static Result<InventoryTransactionLine> Move(
        int lineNo,
        Product product,
        Location source,
        Location target,
        InventoryBalance sourceBalance,
        InventoryBalance targetBalance,
        Quantity quantity)
    {
        EnsureBalance(sourceBalance, product, source, nameof(sourceBalance));
        EnsureBalance(targetBalance, product, target, nameof(targetBalance));

        if (source.Id == target.Id)
        {
            return DomainErrors.Validation("Source and target locations must differ."); // rule 4
        }

        var check = Check(RequirePositive(quantity), RequireActive(product), RequireActive(source), RequireActive(target));
        if (check.IsFailure)
        {
            return check.Error!;
        }

        if (targetBalance.AvailableQuantity > int.MaxValue - quantity.Value)
        {
            return DomainErrors.Validation("Resulting quantity exceeds the supported maximum.");
        }

        var from = sourceBalance.Decrease(quantity, source.Code);
        if (from.IsFailure)
        {
            return from.Error!;
        }

        var to = targetBalance.Increase(quantity);
        return new InventoryTransactionLine(lineNo, product.Id, source.Id, target.Id, quantity.Value, from.Value, to.Value);
    }

    public static Result<InventoryTransactionLine> Ship(
        int lineNo, Product product, Location source, InventoryBalance sourceBalance, Quantity quantity)
    {
        EnsureBalance(sourceBalance, product, source, nameof(sourceBalance));

        var check = Check(RequirePositive(quantity), RequireActive(product), RequireActive(source));
        if (check.IsFailure)
        {
            return check.Error!;
        }

        var change = sourceBalance.Decrease(quantity, source.Code);
        if (change.IsFailure)
        {
            return change.Error!;
        }

        return new InventoryTransactionLine(lineNo, product.Id, source.Id, null, quantity.Value, change.Value, null);
    }

    public static Result<InventoryTransactionLine> Count(
        int lineNo, Product product, Location location, InventoryBalance balance, Quantity counted)
    {
        EnsureBalance(balance, product, location, nameof(balance));

        var check = Check(RequireActive(product), RequireActive(location));
        if (check.IsFailure)
        {
            return check.Error!;
        }

        var change = balance.SetCounted(counted);
        return new InventoryTransactionLine(lineNo, product.Id, null, location.Id, counted.Value, null, change);
    }

    /// <summary>A signed manual correction. Positive adds stock (To side), negative removes it (From side).</summary>
    public static Result<InventoryTransactionLine> Adjust(
        int lineNo, Product product, Location location, InventoryBalance balance, int quantityDelta)
    {
        EnsureBalance(balance, product, location, nameof(balance));

        if (quantityDelta is 0 or int.MinValue)
        {
            return DomainErrors.Validation("Adjustment quantity must be a non-zero whole number.");
        }

        var check = Check(RequireActive(product), RequireActive(location));
        if (check.IsFailure)
        {
            return check.Error!;
        }

        var quantity = Quantity.ForTransaction(Math.Abs(quantityDelta)).Value;
        if (quantityDelta > 0)
        {
            var up = balance.Increase(quantity);
            return up.IsFailure
                ? up.Error!
                : new InventoryTransactionLine(lineNo, product.Id, null, location.Id, quantity.Value, null, up.Value);
        }

        var down = balance.Decrease(quantity, location.Code);
        return down.IsFailure
            ? down.Error!
            : new InventoryTransactionLine(lineNo, product.Id, location.Id, null, quantity.Value, down.Value, null);
    }

    private static Result RequirePositive(Quantity quantity) =>
        quantity.Value > 0
            ? Result.Success()
            : DomainErrors.Validation("Transaction quantity must be greater than zero.");

    private static Result RequireActive(Product product) =>
        product.IsActive ? Result.Success() : DomainErrors.ProductInactive(product.Sku); // rule 5

    private static Result RequireActive(Location location) =>
        location.IsActive ? Result.Success() : DomainErrors.LocationInactive(location.Code); // rule 5

    private static Result Check(params Result[] checks)
    {
        foreach (var check in checks)
        {
            if (check.IsFailure)
            {
                return check;
            }
        }

        return Result.Success();
    }

    private static void EnsureBalance(InventoryBalance balance, Product product, Location location, string parameter)
    {
        ArgumentNullException.ThrowIfNull(balance, parameter);
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(location);
        if (!balance.Matches(product, location))
        {
            throw new ArgumentException("Balance does not belong to the given product and location.", parameter);
        }
    }
}
