using Npgsql;

namespace OrderService.IntegrationTests.Fixtures;

public static class InvariantVerifier
{
    public static async Task AssertNoViolationsAsync(NpgsqlConnection connection, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT count(*) FROM inventories WHERE available_quantity < 0 OR reserved_quantity < 0;
            SELECT count(*) FROM (
              SELECT i.product_id FROM inventories i LEFT JOIN (
                SELECT oi.product_id, sum(oi.quantity)::bigint quantity FROM order_items oi JOIN orders o ON o.id = oi.order_id WHERE o.status = 'PENDING' GROUP BY oi.product_id
              ) p USING (product_id) WHERE i.reserved_quantity::bigint <> coalesce(p.quantity, 0)
            ) x;
            SELECT count(*) FROM (
              SELECT o.id FROM orders o LEFT JOIN order_items oi ON oi.order_id = o.id GROUP BY o.id, o.total_amount HAVING o.total_amount <> coalesce(sum(oi.quantity * oi.unit_price), 0)
            ) x;
            SELECT count(*) FROM orders o WHERE NOT EXISTS (SELECT 1 FROM order_items oi WHERE oi.order_id = o.id);
            SELECT count(*) FROM (
              SELECT order_id, product_id FROM order_items GROUP BY order_id, product_id HAVING count(*) > 1
            ) x;
            SELECT count(*) FROM (
              SELECT scope, idempotency_key FROM idempotency_requests GROUP BY scope, idempotency_key HAVING count(*) > 1
            ) x;
            SELECT count(*) FROM idempotency_requests WHERE state = 'PROCESSING' AND created_at < statement_timestamp() - interval '1 minute';
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.GetInt64(0) != 0) throw new Xunit.Sdk.XunitException($"Database invariant violation detected in result set {reader.Depth + 1}.");
            if (!await reader.NextResultAsync(cancellationToken)) break;
        }
    }
}
