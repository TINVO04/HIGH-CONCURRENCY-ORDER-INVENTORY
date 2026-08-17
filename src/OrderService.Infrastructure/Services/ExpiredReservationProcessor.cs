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
        await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '2s'; SET LOCAL statement_timeout = '15s'; SET LOCAL idle_in_transaction_session_timeout = '20s';", cancellationToken);
        var now = clock.UtcNow;
        var overdue = await db.Orders.FromSqlRaw("SELECT * FROM orders WHERE status = 'PENDING' AND reservation_expired_at <= {0} ORDER BY reservation_expired_at, id FOR UPDATE SKIP LOCKED LIMIT {1}", now, options.ExpiryBatchSize).ToListAsync(cancellationToken);
        var count = 0;
        foreach (var order in overdue)
        {
            var items = await db.OrderItems.Where(x => x.OrderId == order.Id).OrderBy(x => x.ProductId).ToListAsync(cancellationToken);
            var parameters = items.Select((item, index) => new NpgsqlParameter($"e{index}", item.ProductId)).ToArray();
            var array = $"ARRAY[{string.Join(',', parameters.Select(x => $"@{x.ParameterName}"))}]::uuid[]";
            var inventories = await db.Inventories.FromSqlRaw($"SELECT * FROM inventories WHERE product_id = ANY({array}) ORDER BY product_id FOR UPDATE", parameters).ToListAsync(cancellationToken);
            order.Expire(now);
            foreach (var item in items) inventories.Single(x => x.ProductId == item.ProductId).ReleaseReservation(item.Quantity, now);
            count++;
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (count > 0) logger.LogInformation("Expired {Count} reservations", count);
        return count;
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
