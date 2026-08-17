using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using OrderService.Api.Controllers;
using OrderService.Application;
using OrderService.Infrastructure.Persistence;
using OrderService.Infrastructure.Services;

namespace OrderService.IntegrationTests.Fixtures;

public sealed record HttpCreateResult(HttpStatusCode StatusCode, string Body)
{
    public string? ErrorCode => TryDeserialize<ErrorResponse>()?.Code;

    public OrderResponse DeserializeOrder()
    {
        var order = TryDeserialize<OrderResponse>();
        if (order is null || order.OrderId == Guid.Empty)
        {
            throw new Xunit.Sdk.XunitException($"Expected non-empty order response, actual status {(int)StatusCode} and body: {Body}");
        }

        return order;
    }

    private T? TryDeserialize<T>()
    {
        try { return JsonSerializer.Deserialize<T>(Body, JsonOptions); }
        catch (JsonException) { return default; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}

public sealed record InventorySnapshot(Guid ProductId, int Available, int Reserved);

public static class AcceptanceTestSupport
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task RequirePostgreSqlAsync(PostgreSqlFixture database)
    {
        if (!database.IsAvailable)
        {
            Assert.Skip("PostgreSQL acceptance test skipped: set TEST_DATABASE_CONNECTION_STRING or TESTCONTAINERS_ENABLED=true.");
        }

        await database.ApplyMigrationsAsync();
    }

    public static OrderApiFactory CreateApiFactory(PostgreSqlFixture database)
        => new(database.ConnectionString ?? throw new InvalidOperationException("PostgreSQL connection string is unavailable."));

    public static async Task ResetDatabaseAsync(PostgreSqlFixture database)
    {
        await database.ApplyMigrationsAsync();
        await using var connection = await database.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            TRUNCATE TABLE idempotency_requests, order_items, orders, inventories, products CASCADE;
            """, connection);
        await command.ExecuteNonQueryAsync();
    }

    public static async Task SeedProductAsync(PostgreSqlFixture database, Guid productId, string sku, decimal price, int available, bool isActive)
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var now = DateTimeOffset.UtcNow;
        await using (var productCommand = new NpgsqlCommand("""
            INSERT INTO products (id, sku, name, price, is_active, created_at)
            VALUES ($1, $2, $3, $4, $5, $6)
            """, connection, transaction))
        {
            productCommand.Parameters.AddWithValue(productId);
            productCommand.Parameters.AddWithValue(sku);
            productCommand.Parameters.AddWithValue($"{sku} product");
            productCommand.Parameters.AddWithValue(price);
            productCommand.Parameters.AddWithValue(isActive);
            productCommand.Parameters.AddWithValue(now);
            await productCommand.ExecuteNonQueryAsync();
        }

        await using (var inventoryCommand = new NpgsqlCommand("""
            INSERT INTO inventories (product_id, available_quantity, reserved_quantity, updated_at)
            VALUES ($1, $2, 0, $3)
            """, connection, transaction))
        {
            inventoryCommand.Parameters.AddWithValue(productId);
            inventoryCommand.Parameters.AddWithValue(available);
            inventoryCommand.Parameters.AddWithValue(now);
            await inventoryCommand.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    public static async Task<HttpCreateResult> PostCreateAsync(HttpClient client, CreateOrderRequest request, string? idempotencyKey)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
        {
            Content = JsonContent.Create(request, options: JsonOptions)
        };
        if (idempotencyKey is not null)
        {
            message.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }

        using var response = await client.SendAsync(message);
        return new(response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    public static async Task<HttpCreateResult> PostTransitionAsync(HttpClient client, Guid orderId, string transition)
    {
        using var response = await client.PostAsync($"/api/orders/{orderId:D}/{transition}", content: null);
        return new(response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    public static async Task UpdateOrderAsExpiredAsync(PostgreSqlFixture database, Guid orderId)
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("UPDATE orders SET reservation_expired_at = statement_timestamp() - interval '1 second' WHERE id = $1", connection);
        command.Parameters.AddWithValue(orderId);
        (await command.ExecuteNonQueryAsync()).Should().Be(1);
    }

    public static async Task<string> ReadOrderStatusAsync(PostgreSqlFixture database, Guid orderId)
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT status FROM orders WHERE id = $1", connection);
        command.Parameters.AddWithValue(orderId);
        return (string)(await command.ExecuteScalarAsync() ?? throw new Xunit.Sdk.XunitException($"Order {orderId} not found."));
    }

    public static IExpiredReservationProcessor CreateExpiryProcessor(PostgreSqlFixture database, IClock clock)
    {
        var options = new DbContextOptionsBuilder<OrderDbContext>().UseNpgsql(database.ConnectionString).Options;
        return new ExpiredReservationProcessor(new TestDbContextFactory(options), clock, new OrderServiceOptions(), NullLogger<ExpiredReservationProcessor>.Instance);
    }

    public static async Task<InventorySnapshot> ReadInventoryAsync(PostgreSqlFixture database, Guid productId)
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT product_id, available_quantity, reserved_quantity FROM inventories WHERE product_id = $1", connection);
        command.Parameters.AddWithValue(productId);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue($"Inventory row {productId} should exist.");
        return new(reader.GetGuid(0), reader.GetInt32(1), reader.GetInt32(2));
    }

    public static async Task<int> CountOrdersAsync(PostgreSqlFixture database)
        => await ScalarIntAsync(database, "SELECT count(*)::int FROM orders");

    public static async Task<int> CountIdempotencyAsync(PostgreSqlFixture database)
        => await ScalarIntAsync(database, "SELECT count(*)::int FROM idempotency_requests");

    public static async Task<string> ReadLastFailureDiagnosticsAsync(PostgreSqlFixture database)
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT coalesce(string_agg(format('%s=%s', code, count), ', '), '') FROM (SELECT 'orders' code, count(*)::text count FROM orders UNION ALL SELECT 'items', count(*)::text FROM order_items UNION ALL SELECT 'idempotency', count(*)::text FROM idempotency_requests) x", connection);
        return (string)(await command.ExecuteScalarAsync() ?? string.Empty);
    }

    public static async Task AssertInvariantsAsync(PostgreSqlFixture database)
    {
        await using var connection = await database.OpenConnectionAsync();
        await InvariantVerifier.AssertNoViolationsAsync(connection);
        await using var duplicateItems = new NpgsqlCommand("SELECT count(*)::int FROM (SELECT order_id, product_id FROM order_items GROUP BY order_id, product_id HAVING count(*) > 1) duplicates", connection);
        (await duplicateItems.ExecuteScalarAsync()).Should().Be(0);
        await using var duplicateKeys = new NpgsqlCommand("SELECT count(*)::int FROM (SELECT scope, idempotency_key FROM idempotency_requests GROUP BY scope, idempotency_key HAVING count(*) > 1) duplicates", connection);
        (await duplicateKeys.ExecuteScalarAsync()).Should().Be(0);
    }

    private sealed class TestDbContextFactory(DbContextOptions<OrderDbContext> options) : IDbContextFactory<OrderDbContext>
    {
        public OrderDbContext CreateDbContext() => new(options);
        public Task<OrderDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(new OrderDbContext(options));
    }

    private static async Task<int> ScalarIntAsync(PostgreSqlFixture database, string sql)
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }
}
