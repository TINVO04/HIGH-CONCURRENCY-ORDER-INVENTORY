using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OrderService.Application;
using OrderService.Domain;
using OrderService.Infrastructure.Persistence;

namespace OrderService.Infrastructure.Services;

public sealed class OrderApplicationService(
    IDbContextFactory<OrderDbContext> contextFactory,
    IClock clock,
    OrderServiceOptions options,
    ILogger<OrderApplicationService> logger) : IOrderService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<OperationResult<OrderResponse>> CreateAsync(CreateOrderRequest request, string idempotencyKey, string requestPath, CancellationToken cancellationToken)
    {
        var canonicalItems = request.Items.OrderBy(x => x.ProductId).ToArray();
        var scope = $"POST:{requestPath}:{request.CustomerId:D}";
        var fingerprint = Fingerprint(scope, requestPath, request, canonicalItems);
        return ExecuteWithRetryAsync(
            async (db, ct) => await CreateAttemptAsync(db, request, canonicalItems, idempotencyKey, requestPath, scope, fingerprint, ct),
            cancellationToken);
    }

    public Task<OperationResult<OrderResponse>> ConfirmAsync(Guid orderId, CancellationToken cancellationToken)
        => ExecuteWithRetryAsync((db, ct) => TransitionAsync(db, orderId, true, ct), cancellationToken);

    public Task<OperationResult<OrderResponse>> CancelAsync(Guid orderId, CancellationToken cancellationToken)
        => ExecuteWithRetryAsync((db, ct) => TransitionAsync(db, orderId, false, ct), cancellationToken);

    private async Task<OperationResult<OrderResponse>> CreateAttemptAsync(OrderDbContext db, CreateOrderRequest request, IReadOnlyList<CreateOrderItemRequest> items, string key, string path, string scope, byte[] fingerprint, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken);
        await ConfigureTransactionAsync(db, cancellationToken);
        var now = clock.UtcNow;
        var claimId = Guid.NewGuid();
        var inserted = await db.Database.SqlQueryRaw<Guid>("""
            INSERT INTO idempotency_requests (id, scope, idempotency_key, request_path, request_fingerprint, state, created_at)
            VALUES ({0}, {1}, {2}, {3}, {4}, 'PROCESSING', {5})
            ON CONFLICT (scope, idempotency_key) DO NOTHING
            RETURNING id
            """, claimId, scope, key, path, fingerprint, now).SingleOrDefaultAsync(cancellationToken);

        if (inserted == Guid.Empty)
        {
            var existing = await db.IdempotencyRequests.SingleOrDefaultAsync(x => x.Scope == scope && x.IdempotencyKey == key, cancellationToken)
                ?? throw new DomainException("TRANSIENT_DATABASE_ERROR", "The idempotency record was not available after conflict resolution.");
            if (!CryptographicOperations.FixedTimeEquals(existing.RequestFingerprint, fingerprint))
                return new(null, 409, Error: new ErrorResponse("IDEMPOTENCY_KEY_CONFLICT", "The idempotency key was already used with a different request.", string.Empty, []));
            if (existing.State != IdempotencyState.Completed || existing.ResponseBody is null || existing.ResponseStatus is null)
                throw new DomainException("TRANSIENT_DATABASE_ERROR", "The idempotency request is still processing.");
            var replayOrder = JsonSerializer.Deserialize<OrderResponse>(existing.ResponseBody, JsonOptions);
            var replayError = replayOrder is null ? JsonSerializer.Deserialize<ErrorResponse>(existing.ResponseBody, JsonOptions) : null;
            if (replayOrder is not null) return new(replayOrder, existing.ResponseStatus.Value, existing.ResourceLocation, true);
            return new(null, existing.ResponseStatus.Value, existing.ResourceLocation, true, replayError);
        }

        var productIds = items.Select(x => x.ProductId).ToArray();
        var productParameters = productIds.Select((id, index) => new NpgsqlParameter($"p{index}", id)).ToArray();
        var inventoryParameters = productIds.Select((id, index) => new NpgsqlParameter($"i{index}", id)).ToArray();
        var productArray = $"ARRAY[{string.Join(',', productParameters.Select(x => $"@{x.ParameterName}"))}]::uuid[]";
        var inventoryArray = $"ARRAY[{string.Join(',', inventoryParameters.Select(x => $"@{x.ParameterName}"))}]::uuid[]";
        var products = await db.Products.FromSqlRaw($"SELECT * FROM products WHERE id = ANY({productArray}) ORDER BY id FOR UPDATE", productParameters).ToListAsync(cancellationToken);
        var inventories = await db.Inventories.FromSqlRaw($"SELECT * FROM inventories WHERE product_id = ANY({inventoryArray}) ORDER BY product_id FOR UPDATE", inventoryParameters).ToListAsync(cancellationToken);
        if (products.Count != items.Count || inventories.Count != items.Count)
            return await CompleteBusinessFailureAsync(db, transaction, claimId, 404, new ErrorResponse("PRODUCT_NOT_FOUND", "One or more products or inventory rows were not found.", string.Empty, []), now, cancellationToken);

        var productMap = products.ToDictionary(x => x.Id);
        var inventoryMap = inventories.ToDictionary(x => x.ProductId);
        if (products.Any(x => !x.IsActive))
            return await CompleteBusinessFailureAsync(db, transaction, claimId, 404, new ErrorResponse("PRODUCT_INACTIVE", "One or more products are inactive.", string.Empty, []), now, cancellationToken);
        if (items.Any(x => inventoryMap[x.ProductId].AvailableQuantity < x.Quantity))
            return await CompleteBusinessFailureAsync(db, transaction, claimId, 409, new ErrorResponse("OUT_OF_STOCK", "One or more products do not have enough available stock.", string.Empty, []), now, cancellationToken);

        foreach (var item in items) inventoryMap[item.ProductId].Reserve(item.Quantity, now);
        var order = new Order(Guid.NewGuid(), CreateOrderNumber(), request.CustomerId, items.Sum(x => x.Quantity * productMap[x.ProductId].Price), now, now.Add(options.ReservationDuration));
        foreach (var item in items) order.AddItem(item.ProductId, item.Quantity, productMap[item.ProductId].Price);
        db.Orders.Add(order);
        await db.SaveChangesAsync(cancellationToken);
        var responseBody = ToResponse(order);
        var json = JsonSerializer.Serialize(responseBody, JsonOptions);
        var affected = await db.Database.ExecuteSqlRawAsync("UPDATE idempotency_requests SET state = 'COMPLETED', order_id = {0}, response_status = 201, response_body = {1}::jsonb, resource_location = {2}, completed_at = {3} WHERE id = {4} AND state = 'PROCESSING'", [order.Id, json, $"/api/orders/{order.Id}", now, claimId], cancellationToken);
        if (affected != 1) throw new DomainException("INVENTORY_INVARIANT_VIOLATION", "The idempotency claim could not be completed.");
        await transaction.CommitAsync(cancellationToken);
        return new(responseBody, 201, $"/api/orders/{order.Id}");
    }

    private async Task<OperationResult<OrderResponse>> TransitionAsync(OrderDbContext db, Guid orderId, bool confirm, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken);
        await ConfigureTransactionAsync(db, cancellationToken);
        var order = await db.Orders.FromSqlRaw("SELECT * FROM orders WHERE id = {0} FOR UPDATE", orderId).SingleOrDefaultAsync(cancellationToken);
        if (order is null) return new(null, 404, Error: new ErrorResponse("ORDER_NOT_FOUND", "The order was not found.", string.Empty, []));
        var now = clock.UtcNow;
        if (order.Status == OrderStatus.Pending && order.ReservationExpiredAt <= now)
        {
            var itemsExpired = await db.OrderItems.Where(x => x.OrderId == orderId).OrderBy(x => x.ProductId).ToListAsync(cancellationToken);
            var expiredInventories = await LockInventoriesAsync(db, itemsExpired.Select(x => x.ProductId).ToArray(), cancellationToken);
            foreach (var item in itemsExpired) expiredInventories.Single(x => x.ProductId == item.ProductId).ReleaseReservation(item.Quantity, now);
            order.Expire(now);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(null, 409, Error: new ErrorResponse("ORDER_EXPIRED", "The order reservation has expired.", string.Empty, []));
        }

        if (order.Status == (confirm ? OrderStatus.Confirmed : OrderStatus.Cancelled))
        {
            await transaction.CommitAsync(cancellationToken);
            return new(ToResponse(order), 200, $"/api/orders/{order.Id}", true);
        }
        if (order.Status != OrderStatus.Pending)
            return new(null, 409, Error: new ErrorResponse(order.Status == OrderStatus.Expired ? "ORDER_EXPIRED" : "ORDER_STATE_CONFLICT", "The order cannot transition from its current state.", string.Empty, []));

        var items = await db.OrderItems.Where(x => x.OrderId == orderId).OrderBy(x => x.ProductId).ToListAsync(cancellationToken);
        var inventories = await LockInventoriesAsync(db, items.Select(x => x.ProductId).ToArray(), cancellationToken);
        if (confirm)
        {
            order.Confirm(now);
            foreach (var item in items) inventories.Single(x => x.ProductId == item.ProductId).ConsumeReservation(item.Quantity, now);
        }
        else
        {
            order.Cancel(now);
            foreach (var item in items) inventories.Single(x => x.ProductId == item.ProductId).ReleaseReservation(item.Quantity, now);
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(ToResponse(order), 200, $"/api/orders/{order.Id}");
    }

    private static async Task<List<Inventory>> LockInventoriesAsync(OrderDbContext db, Guid[] productIds, CancellationToken cancellationToken)
    {
        var parameters = productIds.Select((id, index) => new NpgsqlParameter($"l{index}", id)).ToArray();
        var array = $"ARRAY[{string.Join(',', parameters.Select(x => $"@{x.ParameterName}"))}]::uuid[]";
        return await db.Inventories.FromSqlRaw($"SELECT * FROM inventories WHERE product_id = ANY({array}) ORDER BY product_id FOR UPDATE", parameters).ToListAsync(cancellationToken);
    }

    private static async Task<OperationResult<OrderResponse>> CompleteBusinessFailureAsync(OrderDbContext db, IDbContextTransaction transaction, Guid claimId, short status, ErrorResponse error, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(error, JsonOptions);
        await db.Database.ExecuteSqlRawAsync("UPDATE idempotency_requests SET state = 'COMPLETED', response_status = {0}, response_body = {1}::jsonb, completed_at = {2} WHERE id = {3} AND state = 'PROCESSING'", [status, json, now, claimId], cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(null, status, Error: error);
    }

    private async Task<OperationResult<T>> ExecuteWithRetryAsync<T>(Func<OrderDbContext, CancellationToken, Task<OperationResult<T>>> action, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            try { return await action(db, cancellationToken); }
            catch (Exception ex) when (attempt < options.MaxTransactionRetries && IsRetryable(ex))
            {
                logger.LogWarning(ex, "Retrying transaction attempt {Attempt}", attempt + 1);
                await Task.Delay(TimeSpan.FromMilliseconds(25 * attempt), cancellationToken);
            }
        }
    }

    private static bool IsRetryable(Exception exception) => exception is PostgresException { SqlState: "40P01" or "40001" or "55P03" };
    private static async Task ConfigureTransactionAsync(OrderDbContext db, CancellationToken cancellationToken) => await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '2s'; SET LOCAL statement_timeout = '5s'; SET LOCAL idle_in_transaction_session_timeout = '10s';", cancellationToken);
    private static byte[] Fingerprint(string scope, string path, CreateOrderRequest request, IReadOnlyList<CreateOrderItemRequest> items) => SHA256.HashData(Encoding.UTF8.GetBytes($"v1|POST|{path}|{scope}|{request.CustomerId:D}|{string.Join(';', items.Select(x => $"{x.ProductId:D}:{x.Quantity}"))}"));
    private static string CreateOrderNumber() => $"ORD-{Guid.NewGuid():N}"[..40];
    private static OrderResponse ToResponse(Order order) => new(order.Id, order.OrderNumber, order.CustomerId, order.Status.ToString().ToUpperInvariant(), order.TotalAmount, order.ReservationExpiredAt, order.Items.OrderBy(x => x.ProductId).Select(x => new OrderItemResponse(x.ProductId, x.Quantity, x.UnitPrice)).ToArray());
}
