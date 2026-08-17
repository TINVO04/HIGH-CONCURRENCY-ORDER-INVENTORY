# OrderService.IntegrationTests — executable test contract

## 1. Mục đích

Đây là test-first contract cho [`Limited Stock Order Service`](../../docs/architecture.md:1), được tạo trước khi source solution .NET tồn tại. File này không phải test code giả vờ compile và không chứa production implementation. Nhóm .NET phải tạo project thực tế theo contract này ở phase triển khai.

Phạm vi bắt buộc: PostgreSQL integration, API acceptance, transaction atomicity, idempotency, high-concurrency inventory, terminal state transitions và expiry. Không chấp nhận chỉ kiểm thử thủ công trên Swagger.

## 2. Project/package expectation

Expected target là .NET 10 và test project độc lập, dự kiến path `tests/OrderService.IntegrationTests/` hoặc path chính thức được .NET team ghi nhận khi khởi tạo solution.

Expected packages, version phải pin theo `Directory.Packages.props`/solution baseline và tương thích minimum supported runtime:

- `Microsoft.NET.Test.Sdk`.
- `xunit.v3` hoặc xUnit version được solution chuẩn hóa; test names trong contract không phụ thuộc runner-specific behavior.
- `Microsoft.AspNetCore.Mvc.Testing` để host API in-process nếu kiến trúc host cho phép.
- `Testcontainers.PostgreSql` để chạy PostgreSQL thật trong test; nếu CI cấp PostgreSQL riêng, giữ cùng SQL/transaction semantics.
- `Npgsql` cho invariant query/fixture SQL khi cần; không dùng InMemory provider.
- `FluentAssertions` tùy solution convention.
- `coverlet.collector` hoặc coverage collector tương đương để enforce mục tiêu >85% trên các project được test.

Package/API versions phải được xác nhận khi solution .NET 10 được khởi tạo. Không khóa assertion vào API giả định của framework chưa có trong workspace.

## 3. Expected test layout và names

Tên class/namespace là test contract; có thể đổi namespace theo solution convention nhưng không được bỏ scenario:

```text
tests/OrderService.IntegrationTests/
├── Fixtures/
│   ├── PostgreSqlFixture.cs
│   ├── ApiTestFactory.cs
│   ├── DatabaseReset.cs
│   ├── TestClock.cs
│   └── InvariantVerifier.cs
├── Contracts/
│   ├── CreateOrderContractTests.cs
│   ├── ValidationContractTests.cs
│   ├── ConfirmContractTests.cs
│   ├── CancelContractTests.cs
│   ├── ExpiryContractTests.cs
│   └── InventoryReadContractTests.cs
├── Concurrency/
│   ├── CreateOrderConcurrencyTests.cs
│   ├── IdempotencyConcurrencyTests.cs
│   ├── TransitionRaceTests.cs
│   └── MultiItemLockOrderingTests.cs
└── README.md
```

Required test names:

- `create_valid_order_should_reserve_stock_and_set_pending_expiry`
- `create_order_without_idempotency_key_should_return_missing_key`
- `create_order_with_blank_or_invalid_idempotency_key_should_return_validation_error`
- `create_order_with_empty_items_should_return_validation_error`
- `create_order_with_non_positive_quantity_should_return_validation_error`
- `create_order_with_invalid_customer_id_should_return_validation_error`
- `create_order_with_missing_product_should_return_product_not_found`
- `create_order_with_inactive_product_should_return_product_inactive`
- `create_order_with_duplicate_product_items_should_return_validation_error`
- `create_order_when_one_item_is_out_of_stock_should_return_conflict_without_mutation`
- `create_multi_item_should_rollback_all_reservations_when_one_item_is_out_of_stock`
- `twenty_concurrent_quantity_one_orders_should_produce_exactly_ten_successes_and_ten_conflicts`
- `five_sequential_same_key_same_payload_should_replay_one_created_order`
- `five_concurrent_same_key_same_payload_should_replay_one_created_order`
- `same_scope_and_key_with_different_payload_should_return_idempotency_conflict`
- `same_key_in_distinct_allowed_scopes_should_not_replay_the_other_scope`
- `confirm_pending_order_should_consume_reserved_quantity_once`
- `confirm_confirmed_order_retry_should_not_mutate_inventory`
- `concurrent_confirm_requests_should_allow_one_effective_transition`
- `cancel_pending_order_should_release_reserved_and_restore_available_once`
- `cancel_cancelled_order_retry_should_not_double_release`
- `cancel_confirmed_order_should_be_rejected_without_inventory_mutation`
- `cancel_expired_order_should_be_rejected_without_second_release`
- `expired_pending_order_should_release_and_transition_to_expired`
- `expiry_rerun_should_not_release_inventory_twice`
- `confirm_cancel_and_expiry_race_should_have_one_winner_and_one_inventory_delta`
- `multiple_expiry_workers_should_not_double_process_the_same_order`
- `multi_item_create_requests_should_not_deadlock_when_items_are_submitted_in_reverse_payload_order`
- `database_failure_during_create_should_rollback_inventory_order_and_idempotency`
- `invariant_queries_should_return_no_violations_after_each_acceptance_scenario`

## 4. Arrange-Act-Assert contract

Every test follows Arrange-Act-Assert and is independently executable.

### Arrange

- Start or connect to a real PostgreSQL instance/container.
- Apply the implementation's migrations; seed Product A with `AvailableQuantity=10`, `ReservedQuantity=0`, `IsActive=true` without resetting test data unexpectedly.
- Use isolated Product/Customer/Order/Scope/Idempotency-Key identifiers per test. Do not share the hot Product A between concurrently running test cases.
- Set deterministic UTC clock to `2030-01-01T00:00:00Z` through the application test seam. Expiry tests advance the clock; they do not sleep 15 real minutes.
- Create a separate API client/request message for each concurrent operation. Set `Idempotency-Key` on the request message; never mutate shared `HttpClient.DefaultRequestHeaders` from parallel tasks.
- Capture stock and row-count snapshots before the Act.

### Act

- For regular API contracts, send one HTTP request and await the response.
- For sequential idempotency, send the same canonical body/key five times serially.
- For concurrent tests, construct all tasks first, await a readiness barrier, release one start gate, then collect all responses with a bounded timeout.
- For transition races, use one independent database connection/`DbContext` per task and invoke confirm, cancel and expiry through the API/application worker seam.
- Await every task before querying the database.

### Assert

- Assert status and stable error `code`; do not assert volatile `traceId`.
- For replay, compare status, JSON business body and `Location` exactly according to the stored-response contract.
- Query PostgreSQL using a separate read-only connection/context.
- Assert stock deltas, Order/OrderItem/idempotency counts, state/timestamp constraints and no double mutation.
- Run all invariant queries in section 7 after every scenario. Any returned violation fails the test.
- Teardown only rows owned by the test and fail if cleanup leaves stale committed `PROCESSING` rows.

## 5. Mandatory acceptance scenario details

### 5.1 Valid create and validation

`create_valid_order_should_reserve_stock_and_set_pending_expiry` arranges one active product and quantity 2. It expects HTTP 201, `PENDING`, `Available=8`, `Reserved=2`, one OrderItem, price snapshot/total consistency, and `reservationExpiredAt` equal to the injected UTC instant plus 15 minutes within database timestamp precision.

Validation tests cover absent/blank/invalid/over-limit `Idempotency-Key`, empty items, q=0, q<0, malformed customer UUID, missing Product, inactive Product and duplicate Product item. Structural validation happens before the idempotency claim, so invalid shape must not create an idempotency row or mutate inventory. Product existence/active failures use the documented 404 codes and must not create an Order or mutate stock.

### 5.2 Out-of-stock and rollback

A single insufficient item returns HTTP 409 with `OUT_OF_STOCK` and leaves stock unchanged. The multi-item test arranges Product A with sufficient stock and Product B with insufficient stock, submits both in one request, and verifies that neither Product A nor Product B changes and no Order/OrderItem is committed. The test must not accept compensation after a partial commit as a substitute for one atomic transaction.

### 5.3 Required 20 → 10/10 concurrency gate

`twenty_concurrent_quantity_one_orders_should_produce_exactly_ten_successes_and_ten_conflicts`:

1. Arrange one fresh Product A with available 10/reserved 0, 20 distinct valid customers and 20 distinct keys.
2. Build 20 independent requests with quantity 1 and a shared readiness/start barrier.
3. Act concurrently against the same running API and PostgreSQL.
4. Assert exactly 10 HTTP 201 responses and exactly 10 HTTP 409 `OUT_OF_STOCK` responses.
5. Query final state: available 0, reserved 10, exactly 10 PENDING orders/items for the test scope, no negative values, no duplicate reservations and no overselling.
6. Run all invariants before cleanup.

No test is allowed to replace this gate with a sequential loop.

### 5.4 Required 5 → 1 idempotency gates

`five_sequential_same_key_same_payload_should_replay_one_created_order` sends the same canonical payload and scope/key five times. It expects five equivalent stored responses, one Order, one set of OrderItems, one reservation delta and one completed idempotency row.

`five_concurrent_same_key_same_payload_should_replay_one_created_order` repeats the behavior with five independent clients released by a barrier. The unique `(Scope, IdempotencyKey)` constraint must arbitrate the race. It expects one Order/one stock mutation and response replay equality for all five results. No shared in-memory deduplication is accepted as evidence.

`same_scope_and_key_with_different_payload_should_return_idempotency_conflict` changes quantity/customer/items after the first request and expects 409 `IDEMPOTENCY_KEY_CONFLICT`, with the original fingerprint/response, stock and Order count unchanged.

`same_key_in_distinct_allowed_scopes_should_not_replay_the_other_scope` uses the scope provider defined by implementation. If trusted authentication is unavailable, use the documented validated CustomerId fallback and record that assumption in test output.

### 5.5 Confirm/cancel

Confirm starts from a non-expired PENDING order. The winning transition changes state to CONFIRMED and decreases reserved exactly by item quantities; available does not increase. A confirm retry or two concurrent confirms may return the documented current-state replay/rejection, but must not consume reservation again.

Cancel starts from a non-expired PENDING order. The winning transition changes state to CANCELLED, decreases reserved and increases available exactly once. Cancel retry, cancel on CONFIRMED, cancel on CANCELLED and cancel on EXPIRED must never double release or reverse terminal state.

### 5.6 Expiry and races

Expiry tests use the injected clock and worker invocation seam. An overdue PENDING order transitions to EXPIRED and releases stock exactly once. Rerunning the worker skips terminal rows. Multiple workers process disjoint locked batches or conditionally skip rows already won by another worker.

`confirm_cancel_and_expiry_race_should_have_one_winner_and_one_inventory_delta` starts confirm, cancel and expiry together for one order at the deterministic deadline boundary. Exactly one legal terminal transition can win. Final inventory must be derived from final state: CONFIRMED means reserved consumed with no available increase; CANCELLED/EXPIRED means reserved released and available increased. Never accept two inventory mutations merely because HTTP responses arrived in different order.

## 6. PostgreSQL fixture/lifecycle contract

`PostgreSqlFixture` must provide:

- Real PostgreSQL connection string from Testcontainers or CI environment.
- Migration application and idempotent seed hooks.
- Per-test database/schema reset or unique identifier isolation.
- A way to create independent `HttpClient`/host and independent data-access connections.
- `ExecuteInvariantQueriesAsync(testScope)` and cleanup.
- Configurable command/lock/statement timeouts.

`ApiTestFactory` must expose the application with test-only dependency injection for deterministic clock, expiry invocation and optional fault injection. It must not substitute an in-memory database for PostgreSQL. If the API uses an in-process server, concurrent calls still need independent request messages and application DbContexts.

`TestClock` must return UTC only and support setting/advancing time atomically for a test. `InvariantVerifier` must execute after all tasks finish and must not use a tracked context that may contain stale values.

## 7. Invariant verifier contract

The verifier runs after every test and fails on any result from these queries:

```sql
-- No negative inventory.
SELECT product_id
FROM inventories
WHERE available_quantity < 0 OR reserved_quantity < 0;

-- Reserved equals PENDING item quantity.
WITH pending AS (
    SELECT oi.product_id, SUM(oi.quantity)::bigint AS quantity
    FROM order_items oi
    JOIN orders o ON o.id = oi.order_id
    WHERE o.status = 'PENDING'
    GROUP BY oi.product_id
)
SELECT i.product_id
FROM inventories i
LEFT JOIN pending p USING (product_id)
WHERE i.reserved_quantity::bigint <> COALESCE(p.quantity, 0);

-- Order total equals item snapshots.
SELECT o.id
FROM orders o
LEFT JOIN order_items oi ON oi.order_id = o.id
GROUP BY o.id, o.total_amount
HAVING o.total_amount <> COALESCE(SUM(oi.quantity * oi.unit_price), 0);

-- No empty order.
SELECT o.id
FROM orders o
WHERE NOT EXISTS (SELECT 1 FROM order_items oi WHERE oi.order_id = o.id);

-- No stale committed PROCESSING claim after grace period.
SELECT id
FROM idempotency_requests
WHERE state = 'PROCESSING'
  AND created_at < statement_timestamp() - INTERVAL '1 minute';

-- No duplicate OrderItems or idempotency scope/key.
SELECT order_id, product_id
FROM order_items
GROUP BY order_id, product_id
HAVING COUNT(*) > 1;

SELECT scope, idempotency_key
FROM idempotency_requests
GROUP BY scope, idempotency_key
HAVING COUNT(*) > 1;
```

For each test scope, also assert that every successful Order has non-empty OrderItems, the matching idempotency row is `COMPLETED`, response status/body are present, and failed structural validation has no idempotency row.

## 8. Configuration environment names

The implementation must bind the following names, with test-safe defaults supplied by the test host only when not set by CI:

- `ConnectionStrings__PostgreSQL` — PostgreSQL connection string; never commit credentials.
- `TEST_POSTGRES_CONNECTION_STRING` — optional CI override used by fixture.
- `TESTCONTAINERS_POSTGRES_IMAGE` — pinned PostgreSQL image/tag, only when Testcontainers is enabled.
- `TEST_DATABASE_NAME` — isolated database/schema suffix.
- `TEST_DATABASE_RESET_MODE` — `isolated-database`, `isolated-schema` or documented cleanup mode.
- `TEST_API_BASE_URL` — external API base URL; omit when using `WebApplicationFactory`.
- `TEST_HTTP_TIMEOUT_SECONDS` — bounded request timeout.
- `TEST_LOCK_TIMEOUT_SECONDS` — expected PostgreSQL lock timeout.
- `TEST_STATEMENT_TIMEOUT_SECONDS` — expected API statement timeout.
- `TEST_MAX_TRANSACTION_RETRIES` — expected whole-transaction retry cap, baseline 3.
- `TEST_CLOCK_UTC` — deterministic ISO-8601 UTC instant, default `2030-01-01T00:00:00Z` in test host.
- `TEST_EXPIRY_BATCH_SIZE` — expiry worker batch size, baseline 50.
- `TEST_RUN_CONCURRENCY` — enables mandatory concurrency profile; must be true in acceptance CI.
- `TEST_SCOPE_PREFIX` — unique test-run scope prefix.
- `TEST_CLEANUP_REQUIRED` — fail run if test-owned rows remain.

No environment variable may contain a hardcoded production secret. CI should inject secrets through its secret store.

## 9. Retry and timeout assertions

The fixture applies per-request and per-task bounded timeouts. A timeout is a failure, not an implicit pass or an excuse to retry indefinitely. The test harness records SQLSTATE/attempt diagnostics when exposed.

Expected retry behavior:

- Retry whole transaction only for `40P01`, `40001`, and budgeted `55P03`.
- Maximum 3 attempts with exponential backoff and jitter.
- Never retry idempotency conflict as a database exception.
- Never retry constraint errors `23503`, `23514`, `22003`.
- On ambiguous create connection outcome, retry with same key; on transition ambiguity, re-read Order state.
- Never issue a mutation retry after the HTTP response has been emitted.

## 10. Mapping to acceptance criteria and blockers

| Acceptance | Required tests |
|---|---|
| AC-01 valid create | T01 |
| AC-02 validation | T02–T09 |
| AC-03 out-of-stock/rollback | T10–T11, T29 |
| AC-04 20 concurrency | T12 |
| AC-05 idempotency | T13–T16 |
| AC-06 confirm | T17–T19, T26 |
| AC-07 cancel | T20–T23, T26 |
| AC-08 expiry | T24–T27 |
| AC-09 lock/deadlock | T28 |
| AC-10 invariants | T01–T30 |

Known blockers are recorded in [`docs/test-strategy.md`](../../docs/test-strategy.md:190): terminal retry status conflict, Product/Inventory lock SQL shape conflict, scope identity choice, missing expiry invocation seam and missing-Inventory error mapping. These test artifacts do not change the baseline.

## 11. Definition of executable completion

The test project is complete only when:

- `dotnet test` runs against PostgreSQL real/container and all mandatory tests are enabled.
- The 20-request test proves 10 successes/10 `OUT_OF_STOCK`, available 0/reserved 10 and 10 PENDING orders.
- The two 5-same-key tests prove one Order and one stock mutation with replay equality.
- Multi-item rollback, confirm/cancel/expiry race, expiry rerun, terminal retries and invariant verifier pass.
- Coverage report is >85% for covered application/domain paths, without excluding concurrency tests.
- No test depends on shared `DbContext`, process-local lock or manual Swagger interaction.
