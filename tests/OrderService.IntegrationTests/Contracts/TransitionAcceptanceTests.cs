using System.Net;
using FluentAssertions;
using OrderService.Application;
using OrderService.IntegrationTests.Fixtures;

namespace OrderService.IntegrationTests.Contracts;

[Collection(AcceptanceTestCollection.Name)]
public sealed class TransitionAcceptanceTests(PostgreSqlFixture database)
{
    [Fact]
    public async Task confirm_pending_order_should_consume_reserved_quantity_once()
    {
        await AcceptanceTestSupport.RequirePostgreSqlAsync(database);
        await AcceptanceTestSupport.ResetDatabaseAsync(database);
        var productId = Guid.NewGuid();
        await AcceptanceTestSupport.SeedProductAsync(database, productId, "CONFIRM-A", 10m, 10, true);
        using var factory = AcceptanceTestSupport.CreateApiFactory(database);
        using var client = factory.CreateClient();
        var create = await AcceptanceTestSupport.PostCreateAsync(client, Request(productId, 2), Guid.NewGuid().ToString("N"));
        var orderId = create.DeserializeOrder().OrderId;

        var result = await AcceptanceTestSupport.PostTransitionAsync(client, orderId, "confirm");

        result.StatusCode.Should().Be(HttpStatusCode.OK, result.Body);
        result.DeserializeOrder().Status.Should().Be("CONFIRMED");
        (await AcceptanceTestSupport.ReadInventoryAsync(database, productId)).Should().Be(new InventorySnapshot(productId, 8, 0));
        await AcceptanceTestSupport.AssertInvariantsAsync(database);
    }

    [Fact]
    public async Task repeated_confirm_should_not_consume_reserved_quantity_twice()
    {
        await AcceptanceTestSupport.RequirePostgreSqlAsync(database);
        await AcceptanceTestSupport.ResetDatabaseAsync(database);
        var productId = Guid.NewGuid();
        await AcceptanceTestSupport.SeedProductAsync(database, productId, "CONFIRM-B", 10m, 10, true);
        using var factory = AcceptanceTestSupport.CreateApiFactory(database);
        using var client = factory.CreateClient();
        var create = await AcceptanceTestSupport.PostCreateAsync(client, Request(productId, 2), Guid.NewGuid().ToString("N"));
        var orderId = create.DeserializeOrder().OrderId;
        (await AcceptanceTestSupport.PostTransitionAsync(client, orderId, "confirm")).StatusCode.Should().Be(HttpStatusCode.OK);
        var before = await AcceptanceTestSupport.ReadInventoryAsync(database, productId);

        var retry = await AcceptanceTestSupport.PostTransitionAsync(client, orderId, "confirm");

        retry.StatusCode.Should().Be(HttpStatusCode.OK, retry.Body);
        retry.DeserializeOrder().Status.Should().Be("CONFIRMED");
        (await AcceptanceTestSupport.ReadInventoryAsync(database, productId)).Should().Be(before);
        await AcceptanceTestSupport.AssertInvariantsAsync(database);
    }

    [Fact]
    public async Task concurrent_confirm_requests_should_allow_only_one_inventory_adjustment()
    {
        await AcceptanceTestSupport.RequirePostgreSqlAsync(database);
        await AcceptanceTestSupport.ResetDatabaseAsync(database);
        var productId = Guid.NewGuid();
        await AcceptanceTestSupport.SeedProductAsync(database, productId, "CONFIRM-C", 10m, 10, true);
        using var factory = AcceptanceTestSupport.CreateApiFactory(database);
        using var clientA = factory.CreateClient();
        using var clientB = factory.CreateClient();
        var create = await AcceptanceTestSupport.PostCreateAsync(clientA, Request(productId, 2), Guid.NewGuid().ToString("N"));
        var orderId = create.DeserializeOrder().OrderId;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Task.Run(async () => { await gate.Task; return await AcceptanceTestSupport.PostTransitionAsync(clientA, orderId, "confirm"); });
        var second = Task.Run(async () => { await gate.Task; return await AcceptanceTestSupport.PostTransitionAsync(clientB, orderId, "confirm"); });
        gate.SetResult();

        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(20));

        results.Should().OnlyContain(x => x.StatusCode == HttpStatusCode.OK || x.StatusCode == HttpStatusCode.Conflict, string.Join(Environment.NewLine, results.Select(x => x.Body)));
        results.Count(x => x.StatusCode == HttpStatusCode.OK).Should().BeGreaterThanOrEqualTo(1);
        (await AcceptanceTestSupport.ReadInventoryAsync(database, productId)).Should().Be(new InventorySnapshot(productId, 8, 0));
        (await AcceptanceTestSupport.ReadOrderStatusAsync(database, orderId)).Should().Be("CONFIRMED");
        await AcceptanceTestSupport.AssertInvariantsAsync(database);
    }

    [Fact]
    public async Task cancel_pending_order_should_release_reserved_and_restore_available_once()
    {
        await AcceptanceTestSupport.RequirePostgreSqlAsync(database);
        await AcceptanceTestSupport.ResetDatabaseAsync(database);
        var productId = Guid.NewGuid();
        await AcceptanceTestSupport.SeedProductAsync(database, productId, "CANCEL-A", 10m, 10, true);
        using var factory = AcceptanceTestSupport.CreateApiFactory(database);
        using var client = factory.CreateClient();
        var create = await AcceptanceTestSupport.PostCreateAsync(client, Request(productId, 2), Guid.NewGuid().ToString("N"));
        var orderId = create.DeserializeOrder().OrderId;

        var result = await AcceptanceTestSupport.PostTransitionAsync(client, orderId, "cancel");

        result.StatusCode.Should().Be(HttpStatusCode.OK, result.Body);
        result.DeserializeOrder().Status.Should().Be("CANCELLED");
        (await AcceptanceTestSupport.ReadInventoryAsync(database, productId)).Should().Be(new InventorySnapshot(productId, 10, 0));
        await AcceptanceTestSupport.AssertInvariantsAsync(database);
    }

    [Fact]
    public async Task repeated_cancel_should_not_release_inventory_twice()
    {
        await AcceptanceTestSupport.RequirePostgreSqlAsync(database);
        await AcceptanceTestSupport.ResetDatabaseAsync(database);
        var productId = Guid.NewGuid();
        await AcceptanceTestSupport.SeedProductAsync(database, productId, "CANCEL-B", 10m, 10, true);
        using var factory = AcceptanceTestSupport.CreateApiFactory(database);
        using var client = factory.CreateClient();
        var create = await AcceptanceTestSupport.PostCreateAsync(client, Request(productId, 2), Guid.NewGuid().ToString("N"));
        var orderId = create.DeserializeOrder().OrderId;
        (await AcceptanceTestSupport.PostTransitionAsync(client, orderId, "cancel")).StatusCode.Should().Be(HttpStatusCode.OK);
        var before = await AcceptanceTestSupport.ReadInventoryAsync(database, productId);

        var retry = await AcceptanceTestSupport.PostTransitionAsync(client, orderId, "cancel");

        retry.StatusCode.Should().Be(HttpStatusCode.OK, retry.Body);
        retry.DeserializeOrder().Status.Should().Be("CANCELLED");
        (await AcceptanceTestSupport.ReadInventoryAsync(database, productId)).Should().Be(before);
        await AcceptanceTestSupport.AssertInvariantsAsync(database);
    }

    [Fact]
    public async Task cancel_confirmed_order_should_be_rejected_without_inventory_mutation()
    {
        await AcceptanceTestSupport.RequirePostgreSqlAsync(database);
        await AcceptanceTestSupport.ResetDatabaseAsync(database);
        var productId = Guid.NewGuid();
        await AcceptanceTestSupport.SeedProductAsync(database, productId, "CANCEL-C", 10m, 10, true);
        using var factory = AcceptanceTestSupport.CreateApiFactory(database);
        using var client = factory.CreateClient();
        var create = await AcceptanceTestSupport.PostCreateAsync(client, Request(productId, 2), Guid.NewGuid().ToString("N"));
        var orderId = create.DeserializeOrder().OrderId;
        (await AcceptanceTestSupport.PostTransitionAsync(client, orderId, "confirm")).StatusCode.Should().Be(HttpStatusCode.OK);
        var before = await AcceptanceTestSupport.ReadInventoryAsync(database, productId);

        var result = await AcceptanceTestSupport.PostTransitionAsync(client, orderId, "cancel");

        result.StatusCode.Should().Be(HttpStatusCode.Conflict, result.Body);
        result.ErrorCode.Should().Be("ORDER_STATE_CONFLICT");
        (await AcceptanceTestSupport.ReadInventoryAsync(database, productId)).Should().Be(before);
        await AcceptanceTestSupport.AssertInvariantsAsync(database);
    }

    [Fact]
    public async Task expired_pending_order_should_release_once_and_expiry_rerun_should_be_noop()
    {
        await AcceptanceTestSupport.RequirePostgreSqlAsync(database);
        await AcceptanceTestSupport.ResetDatabaseAsync(database);
        var productId = Guid.NewGuid();
        await AcceptanceTestSupport.SeedProductAsync(database, productId, "EXPIRY-A", 10m, 10, true);
        using var factory = AcceptanceTestSupport.CreateApiFactory(database);
        using var client = factory.CreateClient();
        var create = await AcceptanceTestSupport.PostCreateAsync(client, Request(productId, 2), Guid.NewGuid().ToString("N"));
        var orderId = create.DeserializeOrder().OrderId;
        await AcceptanceTestSupport.UpdateOrderAsExpiredAsync(database, orderId);
        var clock = new TestClock(DateTimeOffset.UtcNow.AddHours(1));
        var processor = AcceptanceTestSupport.CreateExpiryProcessor(database, clock);

        (await processor.ProcessAsync(CancellationToken.None)).Should().Be(1);
        var afterFirst = await AcceptanceTestSupport.ReadInventoryAsync(database, productId);
        (await processor.ProcessAsync(CancellationToken.None)).Should().Be(0);
        var afterSecond = await AcceptanceTestSupport.ReadInventoryAsync(database, productId);

        afterFirst.Should().Be(new InventorySnapshot(productId, 10, 0));
        afterSecond.Should().Be(afterFirst);
        (await AcceptanceTestSupport.ReadOrderStatusAsync(database, orderId)).Should().Be("EXPIRED");
        await AcceptanceTestSupport.AssertInvariantsAsync(database);
    }

    [Fact]
    public async Task confirm_and_cancel_race_should_have_one_terminal_transition_and_one_inventory_delta()
    {
        await AcceptanceTestSupport.RequirePostgreSqlAsync(database);
        await AcceptanceTestSupport.ResetDatabaseAsync(database);
        var productId = Guid.NewGuid();
        await AcceptanceTestSupport.SeedProductAsync(database, productId, "RACE-A", 10m, 10, true);
        using var factory = AcceptanceTestSupport.CreateApiFactory(database);
        using var confirmClient = factory.CreateClient();
        using var cancelClient = factory.CreateClient();
        var create = await AcceptanceTestSupport.PostCreateAsync(confirmClient, Request(productId, 2), Guid.NewGuid().ToString("N"));
        var orderId = create.DeserializeOrder().OrderId;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var confirm = Task.Run(async () => { await gate.Task; return await AcceptanceTestSupport.PostTransitionAsync(confirmClient, orderId, "confirm"); });
        var cancel = Task.Run(async () => { await gate.Task; return await AcceptanceTestSupport.PostTransitionAsync(cancelClient, orderId, "cancel"); });
        gate.SetResult();

        var results = await Task.WhenAll(confirm, cancel).WaitAsync(TimeSpan.FromSeconds(20));
        var status = await AcceptanceTestSupport.ReadOrderStatusAsync(database, orderId);
        var inventory = await AcceptanceTestSupport.ReadInventoryAsync(database, productId);

        status.Should().BeOneOf("CONFIRMED", "CANCELLED");
        results.Should().OnlyContain(x => x.StatusCode == HttpStatusCode.OK || x.StatusCode == HttpStatusCode.Conflict, string.Join(Environment.NewLine, results.Select(x => x.Body)));
        inventory.Should().Be(status == "CONFIRMED" ? new InventorySnapshot(productId, 8, 0) : new InventorySnapshot(productId, 10, 0));
        await AcceptanceTestSupport.AssertInvariantsAsync(database);
    }

    [Fact]
    public async Task confirm_cancel_and_expiry_race_should_have_one_expired_transition_and_one_release()
    {
        await AcceptanceTestSupport.RequirePostgreSqlAsync(database);
        await AcceptanceTestSupport.ResetDatabaseAsync(database);
        var productId = Guid.NewGuid();
        await AcceptanceTestSupport.SeedProductAsync(database, productId, "RACE-EXPIRY-A", 10m, 10, true);
        using var factory = AcceptanceTestSupport.CreateApiFactory(database);
        using var confirmClient = factory.CreateClient();
        using var cancelClient = factory.CreateClient();
        var create = await AcceptanceTestSupport.PostCreateAsync(confirmClient, Request(productId, 2), Guid.NewGuid().ToString("N"));
        var orderId = create.DeserializeOrder().OrderId;
        await AcceptanceTestSupport.UpdateOrderAsExpiredAsync(database, orderId);
        var clock = new TestClock(DateTimeOffset.UtcNow.AddHours(1));
        var processor = AcceptanceTestSupport.CreateExpiryProcessor(database, clock);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var confirm = Task.Run(async () => { await gate.Task; return await AcceptanceTestSupport.PostTransitionAsync(confirmClient, orderId, "confirm"); });
        var cancel = Task.Run(async () => { await gate.Task; return await AcceptanceTestSupport.PostTransitionAsync(cancelClient, orderId, "cancel"); });
        var expiry = Task.Run(async () => { await gate.Task; return await processor.ProcessAsync(CancellationToken.None); });
        gate.SetResult();

        var transitionResults = await Task.WhenAll(confirm, cancel).WaitAsync(TimeSpan.FromSeconds(20));
        var expiryResult = await expiry.WaitAsync(TimeSpan.FromSeconds(20));
        var status = await AcceptanceTestSupport.ReadOrderStatusAsync(database, orderId);
        var inventory = await AcceptanceTestSupport.ReadInventoryAsync(database, productId);

        status.Should().Be("EXPIRED");
        transitionResults[0].StatusCode.Should().Be(HttpStatusCode.Conflict, transitionResults[0].Body);
        transitionResults[1].StatusCode.Should().Be(HttpStatusCode.Conflict, transitionResults[1].Body);
        expiryResult.Should().BeInRange(0, 1);
        inventory.Should().Be(new InventorySnapshot(productId, 10, 0));
        await AcceptanceTestSupport.AssertInvariantsAsync(database);
    }

    [Fact]
    public async Task multiple_expiry_workers_should_process_same_overdue_order_once()
    {
        await AcceptanceTestSupport.RequirePostgreSqlAsync(database);
        await AcceptanceTestSupport.ResetDatabaseAsync(database);
        var productId = Guid.NewGuid();
        await AcceptanceTestSupport.SeedProductAsync(database, productId, "EXPIRY-RACE-A", 10m, 10, true);
        using var factory = AcceptanceTestSupport.CreateApiFactory(database);
        using var client = factory.CreateClient();
        var create = await AcceptanceTestSupport.PostCreateAsync(client, Request(productId, 2), Guid.NewGuid().ToString("N"));
        var orderId = create.DeserializeOrder().OrderId;
        await AcceptanceTestSupport.UpdateOrderAsExpiredAsync(database, orderId);
        var clock = new TestClock(DateTimeOffset.UtcNow.AddHours(1));
        var firstProcessor = AcceptanceTestSupport.CreateExpiryProcessor(database, clock);
        var secondProcessor = AcceptanceTestSupport.CreateExpiryProcessor(database, clock);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Task.Run(async () => { await gate.Task; return await firstProcessor.ProcessAsync(CancellationToken.None); });
        var second = Task.Run(async () => { await gate.Task; return await secondProcessor.ProcessAsync(CancellationToken.None); });
        gate.SetResult();

        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(20));

        results.Sum().Should().Be(1);
        results.Should().OnlyContain(x => x == 0 || x == 1);
        (await AcceptanceTestSupport.ReadOrderStatusAsync(database, orderId)).Should().Be("EXPIRED");
        (await AcceptanceTestSupport.ReadInventoryAsync(database, productId)).Should().Be(new InventorySnapshot(productId, 10, 0));
        await AcceptanceTestSupport.AssertInvariantsAsync(database);
    }

    private static CreateOrderRequest Request(Guid productId, int quantity)
        => new(Guid.NewGuid(), [new CreateOrderItemRequest(productId, quantity)]);
}
