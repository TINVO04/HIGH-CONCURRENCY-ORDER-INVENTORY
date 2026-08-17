using Microsoft.EntityFrameworkCore;
using Npgsql;
using OrderService.Application;
using OrderService.Domain;
using OrderService.Infrastructure.Persistence;

namespace OrderService.Infrastructure.Services;

public sealed class ExpiredReservationProcessor(IDbContextFactory<OrderDbContext> contextFactory, IClock clock, OrderServiceOptions options, ILogger<ExpiredReservationProcessor> logger) : IExpiredReservationProcessor
{
    public async Task<int> ProcessAsync(CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken);
        var idleTimeoutSeconds = Math.Max(options.StatementTimeoutSeconds, options.LockTimeoutSeconds) + 5;
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('lock_timeout', {options.LockTimeoutSeconds + "s"}, true); SELECT set_config('statement_timeout', {options.StatementTimeoutSeconds + "s"}, true); SELECT set_config('idle_in_transaction_session_timeout', {idleTimeoutSeconds + "s"}, true);",
            cancellationToken);
        var now = clock.UtcNow;
        var overdue = await db.Orders.FromSqlRaw("SELECT * FROM orders WHERE status = 'PENDING' AND reservation_expired_at <= {0} ORDER BY reservation_expired_at, id FOR UPDATE SKIP LOCKED LIMIT {1}", now, options.ExpiryBatchSize).ToListAsync(cancellationToken);
        var count = 0;
        var orderIds = overdue.Select(x => x.Id).ToArray();
        var orderItems = orderIds.Length == 0
            ? []
            : await db.OrderItems.Where(x => orderIds.Contains(x.OrderId)).OrderBy(x => x.ProductId).ToListAsync(cancellationToken);
        var productIds = orderItems.Select(x => x.ProductId).Distinct().OrderBy(x => x).ToArray();
        var inventories = productIds.Length == 0
            ? []
            : await LockInventoriesAsync(db, productIds, cancellationToken);
        if (inventories.Count != productIds.Length)
            throw new DomainException("INVENTORY_INVARIANT_VIOLATION", "One or more inventory rows are missing for expired orders.");

        var inventoryMap = inventories.ToDictionary(x => x.ProductId);
        foreach (var order in overdue)
        {
            var items = orderItems.Where(x => x.OrderId == order.Id).ToArray();
            if (items.Length == 0)
                throw new DomainException("INVENTORY_INVARIANT_VIOLATION", "An expired order has no items.");

            order.Expire(now);
            foreach (var item in items) inventoryMap[item.ProductId].ReleaseReservation(item.Quantity, now);
            count++;
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (count > 0) logger.LogInformation("Expired {Count} reservations", count);
        return count;
    }
    private static async Task<List<Inventory>> LockInventoriesAsync(OrderDbContext db, Guid[] productIds, CancellationToken cancellationToken)
    {
        var parameters = productIds.Select((id, index) => new NpgsqlParameter($"e{index}", id)).ToArray();
        var array = $"ARRAY[{string.Join(',', parameters.Select(x => $"@{x.ParameterName}"))}]::uuid[]";
        return await db.Inventories.FromSqlRaw($"SELECT * FROM inventories WHERE product_id = ANY({array}) ORDER BY product_id FOR UPDATE", parameters).ToListAsync(cancellationToken);
    }
}

public sealed class ExpiryBackgroundService(IServiceScopeFactory scopeFactory, IOptions<OrderServiceOptions> options, ILogger<ExpiryBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IExpiredReservationProcessor>().ProcessAsync(stoppingToken);
                await Task.Delay(options.Value.ExpiryPollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "Expiry processing failed");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }
}
