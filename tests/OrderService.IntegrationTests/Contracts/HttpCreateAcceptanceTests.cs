using System.Net;
using FluentAssertions;
using OrderService.Application;
using OrderService.IntegrationTests.Fixtures;

namespace OrderService.IntegrationTests.Contracts;

[Collection(AcceptanceTestCollection.Name)]
public sealed class HttpCreateAcceptanceTests(PostgreSqlFixture database)
{
    [Fact]
    public async Task create_valid_order_should_reserve_stock_and_set_pending_expiry()
    {
        await AcceptanceTestSupport.RequirePostgreSqlAsync(database);
        using var factory = AcceptanceTestSupport.CreateApiFactory(database);
        using var client = factory.CreateClient();
        await AcceptanceTestSupport.ResetDatabaseAsync(database);
        var productId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        await AcceptanceTestSupport.SeedProductAsync(database, productId, "VALID-A", 10m, 10, true);
        var capturedBefore = DateTimeOffset.UtcNow;

        var result = await AcceptanceTestSupport.PostCreateAsync(
            client,
            new CreateOrderRequest(customerId, [new CreateOrderItemRequest(productId, 2)]),
            Guid.NewGuid().ToString("N"));

        result.StatusCode.Should().Be(HttpStatusCode.Created, result.Body);
        var order = result.DeserializeOrder();
        order.Status.Should().Be("PENDING");
        order.CustomerId.Should().Be(customerId);
        order.Items.Should().ContainSingle().Which.Quantity.Should().Be(2);
        order.ReservationExpiredAt.Should().BeAfter(capturedBefore.AddMinutes(14));
        order.ReservationExpiredAt.Should().BeBefore(DateTimeOffset.UtcNow.AddMinutes(16));
        (await AcceptanceTestSupport.ReadInventoryAsync(database, productId)).Should().Be(new InventorySnapshot(productId, 8, 2));
        await AcceptanceTestSupport.AssertInvariantsAsync(database);
    }

    [Fact]
    public async Task create_order_without_idempotency_key_should_return_missing_key_without_mutation()
    {
        await AcceptanceTestSupport.RequirePostgreSqlAsync(database);
        using var factory = AcceptanceTestSupport.CreateApiFactory(database);
        using var client = factory.CreateClient();
        await AcceptanceTestSupport.ResetDatabaseAsync(database);
        var productId = Guid.NewGuid();
        await AcceptanceTestSupport.SeedProductAsync(database, productId, "VALID-B", 10m, 10, true);

        var result = await AcceptanceTestSupport.PostCreateAsync(
            client,
            new CreateOrderRequest(Guid.NewGuid(), [new CreateOrderItemRequest(productId, 1)]),
            idempotencyKey: null);

        result.StatusCode.Should().Be(HttpStatusCode.BadRequest, result.Body);
        result.ErrorCode.Should().Be("MISSING_IDEMPOTENCY_KEY");
        (await AcceptanceTestSupport.CountOrdersAsync(database)).Should().Be(0);
        (await AcceptanceTestSupport.CountIdempotencyAsync(database)).Should().Be(0);
        (await AcceptanceTestSupport.ReadInventoryAsync(database, productId)).Should().Be(new InventorySnapshot(productId, 10, 0));
    }

    [Fact]
    public async Task create_order_with_blank_or_overlong_idempotency_key_should_return_validation_error_without_claim()
    {
        await AcceptanceTestSupport.RequirePostgreSqlAsync(database);
        using var factory = AcceptanceTestSupport.CreateApiFactory(database);
        using var client = factory.CreateClient();
        await AcceptanceTestSupport.ResetDatabaseAsync(database);
        var productId = Guid.NewGuid();
        await AcceptanceTestSupport.SeedProductAsync(database, productId, "VALID-C", 10m, 10, true);
        var request = new CreateOrderRequest(Guid.NewGuid(), [new CreateOrderItemRequest(productId, 1)]);

        var blank = await AcceptanceTestSupport.PostCreateAsync(client, request, "   ");
        var overlong = await AcceptanceTestSupport.PostCreateAsync(client, request, new string('k', 129));

        blank.StatusCode.Should().Be(HttpStatusCode.BadRequest, blank.Body);
        blank.ErrorCode.Should().Be("MISSING_IDEMPOTENCY_KEY");
        overlong.StatusCode.Should().Be(HttpStatusCode.BadRequest, overlong.Body);
        overlong.ErrorCode.Should().Be("VALIDATION_ERROR");
        (await AcceptanceTestSupport.CountOrdersAsync(database)).Should().Be(0);
        (await AcceptanceTestSupport.CountIdempotencyAsync(database)).Should().Be(0);
        (await AcceptanceTestSupport.ReadInventoryAsync(database, productId)).Should().Be(new InventorySnapshot(productId, 10, 0));
    }

    [Fact]
    public async Task create_order_with_empty_items_should_return_validation_error_without_claim()
    {
        await AcceptanceTestSupport.RequirePostgreSqlAsync(database);
        using var factory = AcceptanceTestSupport.CreateApiFactory(database);
        using var client = factory.CreateClient();
        await AcceptanceTestSupport.ResetDatabaseAsync(database);

        var result = await AcceptanceTestSupport.PostCreateAsync(
            client,
            new CreateOrderRequest(Guid.NewGuid(), []),
            Guid.NewGuid().ToString("N"));

        result.StatusCode.Should().Be(HttpStatusCode.BadRequest, result.Body);
        result.ErrorCode.Should().Be("VALIDATION_ERROR");
        (await AcceptanceTestSupport.CountOrdersAsync(database)).Should().Be(0);
        (await AcceptanceTestSupport.CountIdempotencyAsync(database)).Should().Be(0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task create_order_with_non_positive_quantity_should_return_validation_error_without_mutation(int quantity)
    {
        await AcceptanceTestSupport.RequirePostgreSqlAsync(database);
        using var factory = AcceptanceTestSupport.CreateApiFactory(database);
        using var client = factory.CreateClient();
        await AcceptanceTestSupport.ResetDatabaseAsync(database);
        var productId = Guid.NewGuid();
        await AcceptanceTestSupport.SeedProductAsync(database, productId, "VALID-D", 10m, 10, true);

        var result = await AcceptanceTestSupport.PostCreateAsync(
            client,
            new CreateOrderRequest(Guid.NewGuid(), [new CreateOrderItemRequest(productId, quantity)]),
            Guid.NewGuid().ToString("N"));

        result.StatusCode.Should().Be(HttpStatusCode.BadRequest, result.Body);
        result.ErrorCode.Should().Be("VALIDATION_ERROR");
        (await AcceptanceTestSupport.CountOrdersAsync(database)).Should().Be(0);
        (await AcceptanceTestSupport.ReadInventoryAsync(database, productId)).Should().Be(new InventorySnapshot(productId, 10, 0));
    }

    [Fact]
    public async Task create_order_with_empty_customer_id_should_return_validation_error_without_claim()
    {
        await AcceptanceTestSupport.RequirePostgreSqlAsync(database);
        using var factory = AcceptanceTestSupport.CreateApiFactory(database);
        using var client = factory.CreateClient();
        await AcceptanceTestSupport.ResetDatabaseAsync(database);
        var productId = Guid.NewGuid();
        await AcceptanceTestSupport.SeedProductAsync(database, productId, "VALID-E", 10m, 10, true);

        var result = await AcceptanceTestSupport.PostCreateAsync(
            client,
            new CreateOrderRequest(Guid.Empty, [new CreateOrderItemRequest(productId, 1)]),
            Guid.NewGuid().ToString("N"));

        result.StatusCode.Should().Be(HttpStatusCode.BadRequest, result.Body);
        result.ErrorCode.Should().Be("VALIDATION_ERROR");
        (await AcceptanceTestSupport.CountOrdersAsync(database)).Should().Be(0);
        (await AcceptanceTestSupport.CountIdempotencyAsync(database)).Should().Be(0);
    }

    [Fact]
    public async Task create_order_with_missing_product_should_return_product_not_found_without_stock_mutation()
    {
        await AcceptanceTestSupport.RequirePostgreSqlAsync(database);
        using var factory = AcceptanceTestSupport.CreateApiFactory(database);
        using var client = factory.CreateClient();
        await AcceptanceTestSupport.ResetDatabaseAsync(database);
        var missingProductId = Guid.NewGuid();

        var result = await AcceptanceTestSupport.PostCreateAsync(
            client,
            new CreateOrderRequest(Guid.NewGuid(), [new CreateOrderItemRequest(missingProductId, 1)]),
            Guid.NewGuid().ToString("N"));

        result.StatusCode.Should().Be(HttpStatusCode.NotFound, result.Body);
        result.ErrorCode.Should().Be("PRODUCT_NOT_FOUND");
        (await AcceptanceTestSupport.CountOrdersAsync(database)).Should().Be(0);
        (await AcceptanceTestSupport.CountIdempotencyAsync(database)).Should().Be(1);
        await AcceptanceTestSupport.AssertInvariantsAsync(database);
    }

    [Fact]
    public async Task create_order_with_inactive_product_should_return_product_inactive_without_order()
    {
        await AcceptanceTestSupport.RequirePostgreSqlAsync(database);
        using var factory = AcceptanceTestSupport.CreateApiFactory(database);
        using var client = factory.CreateClient();
        await AcceptanceTestSupport.ResetDatabaseAsync(database);
        var productId = Guid.NewGuid();
        await AcceptanceTestSupport.SeedProductAsync(database, productId, "INACTIVE-A", 10m, 10, false);

        var result = await AcceptanceTestSupport.PostCreateAsync(
            client,
            new CreateOrderRequest(Guid.NewGuid(), [new CreateOrderItemRequest(productId, 1)]),
            Guid.NewGuid().ToString("N"));

        result.StatusCode.Should().Be(HttpStatusCode.NotFound, result.Body);
        result.ErrorCode.Should().Be("PRODUCT_INACTIVE");
        (await AcceptanceTestSupport.CountOrdersAsync(database)).Should().Be(0);
        (await AcceptanceTestSupport.ReadInventoryAsync(database, productId)).Should().Be(new InventorySnapshot(productId, 10, 0));
    }

    [Fact]
    public async Task create_order_with_duplicate_product_items_should_return_validation_error_without_claim()
    {
        await AcceptanceTestSupport.RequirePostgreSqlAsync(database);
        using var factory = AcceptanceTestSupport.CreateApiFactory(database);
        using var client = factory.CreateClient();
        await AcceptanceTestSupport.ResetDatabaseAsync(database);
        var productId = Guid.NewGuid();
        await AcceptanceTestSupport.SeedProductAsync(database, productId, "DUPLICATE-A", 10m, 10, true);

        var result = await AcceptanceTestSupport.PostCreateAsync(
            client,
            new CreateOrderRequest(Guid.NewGuid(),
            [new CreateOrderItemRequest(productId, 1), new CreateOrderItemRequest(productId, 1)]),
            Guid.NewGuid().ToString("N"));

        result.StatusCode.Should().Be(HttpStatusCode.BadRequest, result.Body);
        result.ErrorCode.Should().Be("VALIDATION_ERROR");
        (await AcceptanceTestSupport.CountOrdersAsync(database)).Should().Be(0);
        (await AcceptanceTestSupport.CountIdempotencyAsync(database)).Should().Be(0);
        (await AcceptanceTestSupport.ReadInventoryAsync(database, productId)).Should().Be(new InventorySnapshot(productId, 10, 0));
    }

    [Fact]
    public async Task create_multi_item_should_rollback_all_reservations_when_one_item_is_out_of_stock()
    {
        await AcceptanceTestSupport.RequirePostgreSqlAsync(database);
        using var factory = AcceptanceTestSupport.CreateApiFactory(database);
        using var client = factory.CreateClient();
        await AcceptanceTestSupport.ResetDatabaseAsync(database);
        var productA = Guid.NewGuid();
        var productB = Guid.NewGuid();
        await AcceptanceTestSupport.SeedProductAsync(database, productA, "ROLLBACK-A", 10m, 10, true);
        await AcceptanceTestSupport.SeedProductAsync(database, productB, "ROLLBACK-B", 20m, 1, true);

        var result = await AcceptanceTestSupport.PostCreateAsync(
            client,
            new CreateOrderRequest(Guid.NewGuid(),
            [new CreateOrderItemRequest(productA, 2), new CreateOrderItemRequest(productB, 2)]),
            Guid.NewGuid().ToString("N"));

        result.StatusCode.Should().Be(HttpStatusCode.Conflict, result.Body);
        result.ErrorCode.Should().Be("OUT_OF_STOCK");
        (await AcceptanceTestSupport.CountOrdersAsync(database)).Should().Be(0);
        (await AcceptanceTestSupport.ReadInventoryAsync(database, productA)).Should().Be(new InventorySnapshot(productA, 10, 0));
        (await AcceptanceTestSupport.ReadInventoryAsync(database, productB)).Should().Be(new InventorySnapshot(productB, 1, 0));
        await AcceptanceTestSupport.AssertInvariantsAsync(database);
    }
}
