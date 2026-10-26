using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Messaging;
using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Results;
using Fms.Inventory.Domain;
using Fms.Inventory.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Inventory.Services;

/// <summary>
/// The one place stock quantities change. Callers load the levels they touch, call <see cref="Post"/> for each movement, then save once, so a
/// business action (an issue, a transfer) is all-or-nothing. Concurrent changes to the same level are caught by its row version and retried by
/// <see cref="RunAsync"/>; a quantity can never go below zero (also a database constraint).
/// </summary>
public sealed class StockLedger(InventoryDbContext db, ICurrentUser user, TimeProvider clock, IOutbox outbox)
{
    private const int Attempts = 6;

    public async Task<StockLevel?> FindLevelAsync(Guid storeId, Guid itemId, CancellationToken ct) =>
        await db.Levels.FirstOrDefaultAsync(l => l.StoreId == storeId && l.ItemId == itemId, ct);

    /// <summary>The level, created empty (and tracked) when this item has never been in the store.</summary>
    public async Task<StockLevel> LevelAsync(Guid storeId, Guid itemId, CancellationToken ct)
    {
        var level = await FindLevelAsync(storeId, itemId, ct);
        if (level is not null) return level;
        level = new StockLevel { StoreId = storeId, ItemId = itemId };
        db.Levels.Add(level);
        return level;
    }

    /// <summary>
    /// Applies a signed quantity at a unit cost and records the movement. A positive quantity re-averages the cost (a receipt, a return, a transfer in);
    /// a negative one leaves the average alone. The caller has already checked the rules (enough available); this only does the arithmetic.
    /// </summary>
    public StockMovement Post(StockLevel level, MovementType type, decimal quantity, decimal unitCost, Action<StockMovement>? details = null)
    {
        if (quantity > 0) level.AverageCost = StockRules.AverageAfterReceipt(level.OnHand, level.AverageCost, quantity, unitCost);
        level.OnHand += quantity;
        var movement = new StockMovement
        {
            StoreId = level.StoreId, ItemId = level.ItemId, Type = type, Quantity = quantity, UnitCost = unitCost, BalanceAfter = level.OnHand,
            At = clock.GetUtcNow(), By = user.UserId,
        };
        details?.Invoke(movement);
        db.Movements.Add(movement);
        Refresh(level);
        return movement;
    }

    /// <summary>Re-evaluates the low-stock flag after any change to on-hand or reserved. One notice goes out each time stock dips to its minimum.</summary>
    public void Refresh(StockLevel level)
    {
        var low = StockRules.IsLow(level.OnHand, level.Reserved, level.MinQty);
        if (low && !level.LowNotified)
        {
            level.LowNotified = true;
            outbox.Enqueue(InventoryMessages.LowStock, new LowStockMessage(level.StoreId, level.ItemId));
        }
        else if (!low && level.LowNotified)
        {
            level.LowNotified = false;
        }
    }

    /// <summary>
    /// Runs one business action, repeating it from a clean slate when someone else changed the same stock first (or created the same level at the
    /// same moment). The action must re-read what it needs each time it is called.
    /// </summary>
    public async Task<Result<T>> RunAsync<T>(Func<Task<Result<T>>> action)
    {
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            try
            {
                return await action();
            }
            catch (Exception ex) when (ex is DbUpdateConcurrencyException || (ex is DbUpdateException dbEx && SqlErrors.IsUniqueViolation(dbEx)))
            {
                db.ChangeTracker.Clear();
                if (attempt < Attempts) await Task.Delay(Random.Shared.Next(5, 40 * attempt));
            }
        }

        return Result<T>.Conflict("Stock changed while you were saving. Please try again.");
    }
}
