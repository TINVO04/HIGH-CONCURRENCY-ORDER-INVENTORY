using System.Net;
using System.Net.Http;
using System.Text.Json;
using FluentAssertions;
using OrderService.Application;
using OrderService.IntegrationTests.Fixtures;

namespace OrderService.IntegrationTests.Concurrency;

[Collection(AcceptanceTestCollection.Name)]
public sealed class HttpConcurrencyAcceptanceTests(PostgreSqlFixture database)
{
    [Fact]
    public async Task twenty_http_requests_with_distinct_keys_should_reserve_exactly_ten_units()
    {
        await AcceptanceTestSupport.RequirePostgreSqlAsync(database);
        await AcceptanceTestSupport.ResetDatabaseAsync(database);
        var productId = Guid.NewGuid();
        await AcceptanceTestSupport.SeedProductAsync(database, productId, "CONCURRENT-A", 10m, 10, true);
        using var factory = AcceptanceTestSupport.CreateApiFactory(database);
        using var clients = new HttpClientLease(factory, 20);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new CountdownEvent(20);
        var request = new CreateOrderRequest(Guid.NewGuid(), [new CreateOrderItemRequest(productId, 1)]);

        var tasks = Enumerable.Range(0, 20).Select(async index =>
        {
            ready.Signal();
            await start.Task;
            return await AcceptanceTestSupport.PostCreateAsync(
                clients[index],
                request with { CustomerId = Guid.NewGuid() },
                $"distinct-{Guid.NewGuid():N}");
        }).ToArray();
        SpinWait.SpinUntil(() => ready.CurrentCount == 0, TimeSpan.FromSeconds(5)).Should().BeTrue();
        start.SetResult();
        var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(20));

        results.Select(x => (int)x.StatusCode).GroupBy(x => x).ToDictionary(x => x.Key, x => x.Count())
            .Should().BeEquivalentTo(new Dictionary<int, int> { [201] = 10, [409] = 10 }, options => options.WithStrictOrdering());
        results.Count(x => x.ErrorCode == "OUT_OF_STOCK").Should().Be(10, JsonSerializer.Serialize(results));
        (await AcceptanceTestSupport.ReadInventoryAsync(database, productId)).Should().Be(new InventorySnapshot(productId, 0, 10));
        (await AcceptanceTestSupport.CountOrdersAsync(database)).Should().Be(10);
        await AcceptanceTestSupport.AssertInvariantsAsync(database);
    }

    [Fact]
    public async Task five_concurrent_same_key_same_payload_and_sequential_replays_should_share_one_order_response()
    {
        await AcceptanceTestSupport.RequirePostgreSqlAsync(database);
        await AcceptanceTestSupport.ResetDatabaseAsync(database);
        var productId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var key = $"same-{Guid.NewGuid():N}";
        var request = new CreateOrderRequest(customerId, [new CreateOrderItemRequest(productId, 1)]);
        await AcceptanceTestSupport.SeedProductAsync(database, productId, "IDEMPOTENT-A", 10m, 10, true);
        using var factory = AcceptanceTestSupport.CreateApiFactory(database);
        using var clients = new HttpClientLease(factory, 5);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new CountdownEvent(5);

        var concurrent = Enumerable.Range(0, 5).Select(async index =>
        {
            ready.Signal();
            await start.Task;
            return await AcceptanceTestSupport.PostCreateAsync(clients[index], request, key);
        }).ToArray();
        SpinWait.SpinUntil(() => ready.CurrentCount == 0, TimeSpan.FromSeconds(5)).Should().BeTrue();
        start.SetResult();
        var concurrentResults = await Task.WhenAll(concurrent).WaitAsync(TimeSpan.FromSeconds(20));
        var replayResults = new List<HttpCreateResult>();
        for (var i = 0; i < 5; i++)
        {
            replayResults.Add(await AcceptanceTestSupport.PostCreateAsync(clients[0], request, key));
        }

        var allResults = concurrentResults.Concat(replayResults).ToArray();
        allResults.Should().OnlyContain(x => x.StatusCode == HttpStatusCode.Created, JsonSerializer.Serialize(allResults));
        var orderIds = allResults.Select(x => x.DeserializeOrder().OrderId).Distinct().ToArray();
        orderIds.Should().ContainSingle();
        allResults.Select(x => x.Body).Distinct(StringComparer.Ordinal).Should().ContainSingle();
        (await AcceptanceTestSupport.CountOrdersAsync(database)).Should().Be(1);
        (await AcceptanceTestSupport.CountIdempotencyAsync(database)).Should().Be(1);
        (await AcceptanceTestSupport.ReadInventoryAsync(database, productId)).Should().Be(new InventorySnapshot(productId, 9, 1));
        await AcceptanceTestSupport.AssertInvariantsAsync(database);
    }

    [Fact]
    public async Task same_key_with_different_payload_should_conflict_without_mutation()
    {
        await AcceptanceTestSupport.RequirePostgreSqlAsync(database);
        await AcceptanceTestSupport.ResetDatabaseAsync(database);
        var productId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var key = $"fingerprint-{Guid.NewGuid():N}";
        await AcceptanceTestSupport.SeedProductAsync(database, productId, "FINGERPRINT-A", 10m, 10, true);
        using var factory = AcceptanceTestSupport.CreateApiFactory(database);
        using var client = factory.CreateClient();
        var first = await AcceptanceTestSupport.PostCreateAsync(client, new CreateOrderRequest(customerId, [new CreateOrderItemRequest(productId, 1)]), key);
        var before = await AcceptanceTestSupport.ReadInventoryAsync(database, productId);

        var second = await AcceptanceTestSupport.PostCreateAsync(client, new CreateOrderRequest(customerId, [new CreateOrderItemRequest(productId, 2)]), key);

        first.StatusCode.Should().Be(HttpStatusCode.Created, first.Body);
        second.StatusCode.Should().Be(HttpStatusCode.Conflict, second.Body);
        second.ErrorCode.Should().Be("IDEMPOTENCY_KEY_CONFLICT");
        (await AcceptanceTestSupport.ReadInventoryAsync(database, productId)).Should().Be(before);
        (await AcceptanceTestSupport.CountOrdersAsync(database)).Should().Be(1);
        await AcceptanceTestSupport.AssertInvariantsAsync(database);
    }

    private sealed class HttpClientLease : IDisposable
    {
        private readonly HttpClient[] _clients;
        public HttpClientLease(OrderApiFactory factory, int count) => _clients = Enumerable.Range(0, count).Select(_ => factory.CreateClient()).ToArray();
        public HttpClient this[int index] => _clients[index];
        public void Dispose()
        {
            foreach (var client in _clients) client.Dispose();
        }
    }
}
