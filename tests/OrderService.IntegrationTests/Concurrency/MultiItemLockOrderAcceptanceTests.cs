using System.Net;
using System.Text.Json;
using FluentAssertions;
using OrderService.Application;
using OrderService.IntegrationTests.Fixtures;

namespace OrderService.IntegrationTests.Concurrency;

[Collection(AcceptanceTestCollection.Name)]
public sealed class MultiItemLockOrderAcceptanceTests(PostgreSqlFixture database)
{
    [Fact]
    public async Task multi_item_create_requests_should_not_deadlock_when_items_are_submitted_in_reverse_payload_order()
    {
        await AcceptanceTestSupport.RequirePostgreSqlAsync(database);
        await AcceptanceTestSupport.ResetDatabaseAsync(database);
        var productA = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var productB = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        await AcceptanceTestSupport.SeedProductAsync(database, productA, "LOCK-A", 10m, 10, true);
        await AcceptanceTestSupport.SeedProductAsync(database, productB, "LOCK-B", 20m, 10, true);
        using var factory = AcceptanceTestSupport.CreateApiFactory(database);
        using var clientA = factory.CreateClient();
        using var clientB = factory.CreateClient();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new CountdownEvent(2);
        var first = new CreateOrderRequest(Guid.NewGuid(),
            [new CreateOrderItemRequest(productA, 1), new CreateOrderItemRequest(productB, 1)]);
        var second = new CreateOrderRequest(Guid.NewGuid(),
            [new CreateOrderItemRequest(productB, 1), new CreateOrderItemRequest(productA, 1)]);

        var taskA = Task.Run(async () =>
        {
            ready.Signal();
            await start.Task;
            return await AcceptanceTestSupport.PostCreateAsync(clientA, first, $"lock-a-{Guid.NewGuid():N}");
        });
        var taskB = Task.Run(async () =>
        {
            ready.Signal();
            await start.Task;
            return await AcceptanceTestSupport.PostCreateAsync(clientB, second, $"lock-b-{Guid.NewGuid():N}");
        });
        SpinWait.SpinUntil(() => ready.CurrentCount == 0, TimeSpan.FromSeconds(5)).Should().BeTrue();
        start.SetResult();
        var results = await Task.WhenAll(taskA, taskB).WaitAsync(TimeSpan.FromSeconds(20));

        results.Should().OnlyContain(x => x.StatusCode == HttpStatusCode.Created || x.StatusCode == HttpStatusCode.Conflict, JsonSerializer.Serialize(results));
        results.Count(x => x.StatusCode == HttpStatusCode.Created).Should().Be(2, JsonSerializer.Serialize(results));
        (await AcceptanceTestSupport.ReadInventoryAsync(database, productA)).Should().Be(new InventorySnapshot(productA, 8, 2));
        (await AcceptanceTestSupport.ReadInventoryAsync(database, productB)).Should().Be(new InventorySnapshot(productB, 8, 2));
        (await AcceptanceTestSupport.CountOrdersAsync(database)).Should().Be(2);
        await AcceptanceTestSupport.AssertInvariantsAsync(database);
    }
}
