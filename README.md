# Limited Stock Order Service

## 1. Mục tiêu và phạm vi

**Limited Stock Order Service** là ASP.NET Core Web API cho bài toán đặt hàng flash-sale với tồn kho giới hạn. Service cho phép khách hàng tạo đơn, giữ hàng trong thời gian ngắn, xác nhận hoặc hủy đơn, đồng thời bảo đảm tính đúng đắn khi nhiều request cùng cập nhật một sản phẩm.

Baseline của bài bàn giao:

- PostgreSQL là source of truth cho Product, Inventory, Order và idempotency.
- Product A được seed với `AvailableQuantity = 10`, `ReservedQuantity = 0`.
- Reservation mặc định có thời hạn 15 phút.
- Không overselling, không tồn kho âm, không giữ hàng lặp khi request được retry.
- Mọi mutation nghiệp vụ của Order/Inventory/Idempotency nằm trong transaction PostgreSQL.

### Non-goals

- Không bao gồm payment, shipment, refund, promotion, restock hoặc stock ledger đầy đủ.
- Không bao gồm authentication/authorization hoặc trusted principal/tenant isolation.
- Không dùng Redis, message broker, event bus, distributed lock service hay microservices trong baseline.
- Không dùng `C# lock`, `Monitor` hoặc semaphore trong process để bảo vệ correctness.
- Không có public endpoint để gọi expiry worker; worker chạy nền trong API host.

Nguồn yêu cầu là [đề bài VietProDev](VietProDev_Bai_Test_01_High_Concurrency_Order_Inventory_NET10_V2.docx:1). Các tài liệu thiết kế chi tiết gồm [architecture](docs/architecture.md:1), [database design](docs/database-design.md:1), [SQL reference](docs/sql-reference.md:1), [test strategy](docs/test-strategy.md:1), [DevOps runbook](docs/devops.md:1) và [code review](docs/code-review.md:1).

## 2. Tech stack

| Thành phần | Baseline thực tế |
|---|---|
| Runtime/SDK | .NET 10, target `net10.0` |
| Web framework | ASP.NET Core Web API |
| ORM | Entity Framework Core 10; package baseline `10.0.10` |
| PostgreSQL | PostgreSQL 17 qua container image `postgres:17-alpine` |
| Provider | Npgsql EF Core provider `10.0.3` |
| API documentation | Swagger/OpenAPI với Swashbuckle `10.2.3` |
| Automated tests | xUnit v3 `3.2.2`, ASP.NET Core MVC Testing, FluentAssertions |
| Database integration tests | Testcontainers PostgreSQL `4.14.0` hoặc PostgreSQL bên ngoài được chỉ định rõ |
| Container runtime | Docker Desktop; [Dockerfile](Dockerfile:1) và [docker-compose.yml](docker-compose.yml:1) |

Phiên bản package được quản lý tập trung trong [Directory.Packages.props](Directory.Packages.props:1). Không suy diễn phiên bản khác ngoài các giá trị trong source hiện tại.

## 3. Cấu trúc solution

Solution được khai báo trong [OrderService.slnx](OrderService.slnx:1):

| Project | Trách nhiệm |
|---|---|
| [OrderService.Api](src/OrderService.Api/OrderService.Api.csproj:1) | HTTP controllers, DTO boundary, header validation, status mapping, global exception handling, correlation ID, health checks và Swagger. |
| [OrderService.Application](src/OrderService.Application/OrderService.Application.csproj:1) | Contracts, request/response records, service interfaces, clock và options dùng giữa API/application/infrastructure. |
| [OrderService.Domain](src/OrderService.Domain/OrderService.Domain.csproj:1) | Entity, enum trạng thái, invariant và transition rule thuần nghiệp vụ. |
| [OrderService.Infrastructure](src/OrderService.Infrastructure/OrderService.Infrastructure.csproj:1) | EF Core/Npgsql, DbContext, migration, startup initializer/seed, order/inventory application services và expiry `BackgroundService`. |
| [OrderService.IntegrationTests](tests/OrderService.IntegrationTests/OrderService.IntegrationTests.csproj:1) | HTTP acceptance, PostgreSQL integration, concurrency, idempotency, transition race, expiry và invariant checks. |

Controller chỉ điều phối HTTP và gọi service; business orchestration nằm trong infrastructure/application service, ví dụ [OrdersController](src/OrderService.Api/Controllers/OrdersController.cs:6) và [OrderApplicationService](src/OrderService.Infrastructure/Services/OrderApplicationService.cs:13).

## 4. Quick start trên Windows/PowerShell

### Prerequisites

- Docker Desktop đang chạy và Docker engine đã sẵn sàng.
- PowerShell trên Windows.
- .NET 10 SDK nếu muốn restore/build/test trực tiếp trên máy.
- Git clone repository và mở PowerShell tại thư mục repository.

### Khởi động local stack

Tạo file override local từ [.env.example](.env.example:1). File `.env` chỉ dành cho máy developer và không được commit. Hãy thay password dev-only bằng password local riêng của máy trước khi khởi động:

```powershell
Copy-Item .env.example .env
# chỉnh POSTGRES_PASSWORD và CONNECTIONSTRINGS__POSTGRESQL trong .env
```

Lệnh khởi động tương thích với Docker Compose legacy CLI hiện có trong môi trường kiểm chứng:

```powershell
docker-compose up --build -d
docker-compose ps
```

Nếu Docker Desktop đã cài Compose v2, lệnh tương đương là:

```powershell
docker compose up --build -d
docker compose ps
```

Nếu `docker compose` không khả dụng nhưng `docker-compose` khả dụng, dùng legacy command như lệnh đầu tiên. Đây là giới hạn tooling CLI, không phải yêu cầu thay đổi [Dockerfile](Dockerfile:1) hoặc [docker-compose.yml](docker-compose.yml:1).

### Kiểm tra health và Swagger

Với port mặc định `API_PORT=8080`:

```powershell
Invoke-WebRequest http://localhost:8080/health/live
Invoke-WebRequest http://localhost:8080/health/ready
Start-Process http://localhost:8080/swagger/index.html
```

- `/health/live`: liveness, không query PostgreSQL.
- `/health/ready`: readiness, kiểm tra kết nối PostgreSQL.
- `/swagger/index.html`: chỉ được bật khi `ASPNETCORE_ENVIRONMENT=Development`.
- Nếu đổi `API_PORT`, thay `8080` trong các URL trên bằng host port mới.

### Dừng và reset dữ liệu

Dừng container nhưng giữ volume PostgreSQL:

```powershell
docker-compose down
```

Dừng container và xóa volume để reset database, migration state và seed data:

```powershell
docker-compose down -v
```

Sau khi reset volume, chạy lại `docker-compose up --build -d`. Không dùng `down -v` trên database chứa dữ liệu cần giữ.

## 5. Cấu hình và secrets

Compose đọc biến từ `.env` tự động. Các biến local hiện được hỗ trợ:

| Biến | Default local | Mục đích |
|---|---|---|
| `POSTGRES_DB` | bắt buộc trong Compose; `.env.example` có giá trị mẫu | Tên database PostgreSQL |
| `POSTGRES_USER` | bắt buộc trong Compose; `.env.example` có giá trị mẫu | User local |
| `POSTGRES_PASSWORD` | bắt buộc trong Compose; `.env.example` có giá trị mẫu cần đổi | Credential local, không dùng production |
| `POSTGRES_PORT` | `5432` | Host port PostgreSQL |
| `API_PORT` | `8080` | Host port API |
| `ASPNETCORE_ENVIRONMENT` | `Development` | Bật Swagger local |
| `CONNECTIONSTRINGS__POSTGRESQL` | connection tới service `postgres` | Override connection string của API Compose |
| `TEST_DATABASE_CONNECTION_STRING` | unset | PostgreSQL database riêng cho integration tests |
| `TESTCONTAINERS_ENABLED` | `false` | Cho phép fixture tự tạo PostgreSQL Testcontainer |

Trong process .NET, tên tương ứng là `ConnectionStrings__PostgreSQL`. Connection string có dạng:

```text
Host=<host>;Port=<port>;Database=<database>;Username=<user>;Password=<injected-at-runtime>
```

Không commit password, token, connection string production hoặc file `.env`. Dùng [.env.example](.env.example:1) làm template, secret store của CI/CD hoặc biến môi trường runtime. Compose fail-fast nếu thiếu `POSTGRES_DB`, `POSTGRES_USER`, `POSTGRES_PASSWORD` hoặc `CONNECTIONSTRINGS__POSTGRESQL`; các giá trị mẫu trong `.env.example` chỉ dành cho local development và cần được thay đổi.

Các option nghiệp vụ được bind từ `OrderService` trong [appsettings.json](src/OrderService.Api/appsettings.json:1):

| Option | Default | Ý nghĩa |
|---|---:|---|
| `ReservationDuration` | `00:15:00` | Thời hạn giữ hàng |
| `MaxTransactionRetries` | `3` | Số attempt tối đa cho transaction retryable |
| `ExpiryBatchSize` | `50` | Số order tối đa mỗi batch expiry |
| `ExpiryPollInterval` | `00:00:10` | Chu kỳ worker |
| `LockTimeoutSeconds` | `2` | Baseline cấu hình lock timeout |
| `StatementTimeoutSeconds` | `5` | Baseline cấu hình statement timeout |

## 6. Migration và seed behavior

Khi API startup, [Program.Main()](src/OrderService.Api/Program.cs:13) gọi [DbInitializer.InitializeAsync()](src/OrderService.Infrastructure/Persistence/DbInitializer.cs:8). Initializer thực hiện theo thứ tự:

1. Gọi `Database.MigrateAsync()` để apply các EF Core migration chưa có.
2. Tìm Product theo SKU `PRODUCT-A`.
3. Nếu Product chưa tồn tại, tạo Product A active và Inventory tương ứng với `available=10`, `reserved=0`.
4. Nếu Product đã tồn tại nhưng thiếu Inventory row, tạo lại Inventory với `10/0`.
5. Nếu Product và Inventory đã tồn tại, không reset số lượng hiện tại.

Seed ID Product A trong source là `aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa`; giá seed là `10.00`. Seed không tạo Order hoặc IdempotencyRequest mẫu. Migration hiện có tại [InitialCreate](src/OrderService.Infrastructure/Persistence/Migrations/20260817052012_InitialCreate.cs:9).

Behavior này idempotent cho startup bình thường nhưng không phải cơ chế repair/audit cho database bị can thiệp ngoài hệ thống. Muốn reset hoàn toàn local database, xóa volume bằng `docker-compose down -v` rồi khởi động lại.

## 7. API contract

Error envelope chung:

```json
{
  "code": "OUT_OF_STOCK",
  "message": "One or more products do not have enough available stock.",
  "traceId": "request-trace-id",
  "details": []
}
```

`traceId` có thể thay đổi giữa các request; client không nên dùng nó làm business identity. `X-Correlation-ID` là optional và chỉ được chấp nhận khi có ký tự an toàn. `Idempotency-Key` không được log raw.

### Endpoint summary

| Method | Route | Header/body | Success | Important errors |
|---|---|---|---:|---|
| POST | `/api/orders` | Bắt buộc `Idempotency-Key`, body create order | `201 Created` | `400`, `404`, `409`, `503`, `500` |
| POST | `/api/orders/{id:guid}/confirm` | Không có body | `200 OK` | `404 ORDER_NOT_FOUND`, `409 ORDER_EXPIRED`/`ORDER_STATE_CONFLICT` |
| POST | `/api/orders/{id:guid}/cancel` | Không có body | `200 OK` | `404 ORDER_NOT_FOUND`, `409 ORDER_EXPIRED`/`ORDER_STATE_CONFLICT` |
| GET | `/api/inventory/{productId:guid}` | Không có body | `200 OK` | `404 INVENTORY_NOT_FOUND` |

### 7.1 Create order và reserve stock

`POST /api/orders` yêu cầu header `Idempotency-Key`, được trim và giới hạn tối đa 128 ký tự. Structural validation xảy ra trước idempotency claim.

Request:

```http
POST /api/orders HTTP/1.1
Content-Type: application/json
Idempotency-Key: order-demo-001

{
  "customerId": "11111111-1111-1111-1111-111111111111",
  "items": [
    {
      "productId": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
      "quantity": 2
    }
  ]
}
```

Response `201 Created`:

```json
{
  "orderId": "22222222-2222-2222-2222-222222222222",
  "orderNumber": "ORD-xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx",
  "customerId": "11111111-1111-1111-1111-111111111111",
  "status": "PENDING",
  "totalAmount": 20.00,
  "reservationExpiredAt": "2030-01-01T00:15:00Z",
  "items": [
    {
      "productId": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
      "quantity": 2,
      "unitPrice": 10.00
    }
  ]
}
```

Response có `Location: /api/orders/{orderId}`. Baseline không cung cấp `GET /api/orders/{id}`; Location được trả và lưu để replay nhất quán.

Create behavior:

- `201`: Order mới ở `PENDING`, `available -= quantity`, `reserved += quantity`.
- `400 MISSING_IDEMPOTENCY_KEY`: thiếu hoặc blank header.
- `400 VALIDATION_ERROR`: customer rỗng, items rỗng/null, ProductId rỗng, quantity `<= 0`, duplicate Product hoặc key dài hơn 128 ký tự.
- `404 PRODUCT_NOT_FOUND`: Product hoặc Inventory row không tồn tại.
- `404 PRODUCT_INACTIVE`: Product tồn tại nhưng không active.
- `409 OUT_OF_STOCK`: một item không đủ available; multi-item request không reserve một phần.
- `409 IDEMPOTENCY_KEY_CONFLICT`: cùng scope/key nhưng payload có fingerprint khác.
- `503 TRANSIENT_DATABASE_ERROR`: lỗi database retryable/không hoàn tất được transaction.
- `500 INTERNAL_ERROR`: lỗi nội bộ hoặc invariant không được phép lộ chi tiết ra client.

Các business failure `PRODUCT_NOT_FOUND`, `PRODUCT_INACTIVE` và `OUT_OF_STOCK` được lưu response trong idempotency transaction để retry cùng key/fingerprint trả lại kết quả nhất quán. Validation HTTP xảy ra trước claim nên không tạo idempotency row.

### 7.2 Confirm order

```http
POST /api/orders/22222222-2222-2222-2222-222222222222/confirm HTTP/1.1
```

Response `200 OK` là `OrderResponse` với `status = "CONFIRMED"`:

```json
{
  "orderId": "22222222-2222-2222-2222-222222222222",
  "orderNumber": "ORD-xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx",
  "customerId": "11111111-1111-1111-1111-111111111111",
  "status": "CONFIRMED",
  "totalAmount": 20.00,
  "reservationExpiredAt": "2030-01-01T00:15:00Z",
  "items": []
}
```

Confirm khóa Order trước, rồi khóa Inventory theo ProductId tăng dần. `PENDING -> CONFIRMED` làm `reserved -= quantity`; `available` không tăng. Gọi lại confirm khi Order đã `CONFIRMED` trả `200` representation hiện tại và không consume lần hai. Order không tồn tại trả `404 ORDER_NOT_FOUND`; Order đã hết hạn trả `409 ORDER_EXPIRED` sau khi release/expire đã commit; trạng thái khác trả `409 ORDER_STATE_CONFLICT`.

### 7.3 Cancel order

```http
POST /api/orders/22222222-2222-2222-2222-222222222222/cancel HTTP/1.1
```

Response `200 OK` là `OrderResponse` với `status = "CANCELLED"`. Với reservation quantity 2, inventory chuyển từ `available=8,reserved=2` về `available=10,reserved=0`.

`PENDING -> CANCELLED` làm `reserved -= quantity` và `available += quantity` trong cùng transaction. Gọi lại cancel khi đã `CANCELLED` trả representation hiện tại mà không release lần hai. Không thể cancel `CONFIRMED`; response là `409 ORDER_STATE_CONFLICT`. Order đã hết hạn hoặc đã `EXPIRED` trả `409 ORDER_EXPIRED` và không release thêm.

### 7.4 Read inventory

```http
GET /api/inventory/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa HTTP/1.1
```

Response `200 OK`:

```json
{
  "productId": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
  "available": 8,
  "reserved": 2
}
```

Đây là snapshot read-only tại một thời điểm, không phải cam kết giữ hàng cho request tiếp theo. Thiếu Inventory row trả `404 INVENTORY_NOT_FOUND`.

## 8. Reservation flow

1. **Create:** validate request, reserve stock và tạo Order `PENDING` với `ReservationExpiredAt = now + 15 phút`.
2. **Confirm:** trước deadline, chuyển `PENDING -> CONFIRMED` và consume `ReservedQuantity`.
3. **Cancel:** trước deadline, chuyển `PENDING -> CANCELLED`, giảm reserved và trả quantity về available.
4. **Expiry worker:** [ExpiredReservationProcessor](src/OrderService.Infrastructure/Services/ExpiredReservationProcessor.cs:9) chạy nền mỗi 10 giây, chọn tối đa 50 PENDING order quá hạn, chuyển sang `EXPIRED` và release reservation.
5. **Exactly once:** chỉ transition thành công từ `PENDING` mới được điều chỉnh Inventory. Terminal state không được mutation lại.

Nếu confirm/cancel nhận thấy PENDING đã quá deadline trước khi worker chạy, API tự xử lý nhánh expire trong transaction rồi trả `409 ORDER_EXPIRED`. Worker dùng `SKIP LOCKED`, vì vậy nhiều API instance có thể chạy mà không chọn trùng batch đang bị lock.

## 9. Concurrency Strategy

Baseline correctness nằm trong PostgreSQL, không nằm trong memory của API process:

- **Source of truth:** PostgreSQL lưu state Order, Inventory và IdempotencyRequest.
- **Isolation:** mỗi use case mutation mở explicit `READ COMMITTED` transaction.
- **Atomic idempotency claim:** dùng unique `(scope, idempotency_key)` và `INSERT ... ON CONFLICT DO NOTHING RETURNING`; không dùng check-then-insert.
- **Create order locks:** khóa Product theo `ProductId ASC`, sau đó khóa Inventory theo `ProductId ASC` bằng PostgreSQL `FOR UPDATE` trước khi validate/mutate.
- **Transition locks:** confirm/cancel khóa Order trước, sau đó khóa các Inventory liên quan theo `ProductId ASC`.
- **Expiry:** chọn PENDING quá hạn bằng `FOR UPDATE SKIP LOCKED`, giữ Order locks và Inventory locks đến khi batch commit.
- **One transaction:** Inventory mutation, Order state transition, OrderItem insert và idempotency response commit hoặc rollback cùng nhau.
- **No C# lock:** `lock`, `Monitor` hoặc semaphore chỉ bảo vệ một process, không bảo vệ khi có nhiều API server/restart/scale-out.
- **Defense-in-depth:** domain methods và database check constraints giữ available/reserved không âm; source hiện mutate tracked entities sau row lock. Guarded set-based `UPDATE` với affected-row check là P2 residual, chưa thay baseline.

Với 20 request khác key, quantity 1 và available stock 10, PostgreSQL serialize các transaction cùng lock row. Sau khi chờ lock, mỗi transaction đọc trạng thái committed mới nhất: đúng 10 request reserve thành công, 10 request nhận `OUT_OF_STOCK`, kết quả cuối là `available=0,reserved=10`.

## 10. Idempotency Flow

Luồng create order:

1. **Request:** nhận `POST /api/orders`, payload và `Idempotency-Key`.
2. **Structural validation:** reject key blank/over-limit, customer không hợp lệ, items rỗng, quantity không dương hoặc Product trùng; các lỗi này không claim key.
3. **Canonical request:** trim key; giữ customer UUID dạng chuẩn; sort items theo `ProductId ASC`.
4. **Canonical fingerprint:** tạo SHA-256 từ schema version, method, path, scope, customer UUID và danh sách ProductId/quantity đã sort. Source hiện dùng scope `POST:/api/orders:{CustomerId:D}` vì chưa có trusted authentication principal.
5. **Atomic claim:** trong `READ COMMITTED` transaction, insert trạng thái `PROCESSING` bằng `ON CONFLICT DO NOTHING RETURNING id`.
6. **Existing key:**
   - fingerprint khác: trả `409 IDEMPOTENCY_KEY_CONFLICT`, không đụng stock;
   - fingerprint giống và response `COMPLETED`: replay nguyên status/body/location;
   - record `PROCESSING` đã commit bất thường: không chạy business lần hai, trả lỗi vận hành retryable.
7. **Business transaction:** với claim mới, lock Product/Inventory, kiểm tra active/stock, rồi insert Order/Items hoặc hoàn tất business failure.
8. **Store response:** lưu `response_status`, `response_body`, `order_id` nếu có và `resource_location` trong cùng transaction.
9. **Commit then response:** chỉ trả HTTP sau commit. Lỗi database làm rollback claim, stock và order; retry create phải dùng lại cùng key.

Business rejection xác định được như `OUT_OF_STOCK` được lưu để retry nhất quán. Không có committed `PROCESSING` bình thường vì claim và completion cùng transaction.

## 11. Test và quality evidence

### Build/restore và default test behavior

```powershell
dotnet restore .\OrderService.slnx
dotnet build .\OrderService.slnx --no-restore
dotnet test .\OrderService.slnx
```

`dotnet test` mặc định cần PostgreSQL thật, PostgreSQL container hoặc connection string test. Nếu không có database và không bật Testcontainers, [PostgreSqlFixture](tests/OrderService.IntegrationTests/Fixtures/PostgreSqlFixture.cs:8) không có connection string và acceptance tests gọi `Assert.Skip`. **Skip không phải pass và không được dùng làm quality evidence.**

### Testcontainers PostgreSQL explicit

Docker Desktop phải đang chạy. Xóa override external DB trong session nếu cần để chắc chắn fixture tạo container:

```powershell
Remove-Item Env:TEST_DATABASE_CONNECTION_STRING -ErrorAction SilentlyContinue
$env:TESTCONTAINERS_ENABLED='true'; dotnet test .\tests\OrderService.IntegrationTests\OrderService.IntegrationTests.csproj --verbosity minimal
```

Fixture dùng PostgreSQL `17-alpine` khi Testcontainers được bật. Nếu `TEST_DATABASE_CONNECTION_STRING` có giá trị, fixture ưu tiên database đó và không tạo container.

### External PostgreSQL explicit

Dùng database riêng cho test; không dùng database `orderservice` đang phục vụ API dev:

```powershell
$env:TEST_DATABASE_CONNECTION_STRING = $env:CI_TEST_DATABASE_CONNECTION_STRING
if ([string]::IsNullOrWhiteSpace($env:TEST_DATABASE_CONNECTION_STRING)) { throw "CI_TEST_DATABASE_CONNECTION_STRING is required." }
dotnet test .\tests\OrderService.IntegrationTests\OrderService.IntegrationTests.csproj --verbosity minimal
```

Connection string được inject từ CI secret store hoặc runtime environment, không ghi credential thật vào README. Fixture sẽ apply migration và test reset database theo lifecycle của test.

Các acceptance areas nằm trong [HttpConcurrencyAcceptanceTests](tests/OrderService.IntegrationTests/Concurrency/HttpConcurrencyAcceptanceTests.cs:11), [MultiItemLockOrderAcceptanceTests](tests/OrderService.IntegrationTests/Concurrency/MultiItemLockOrderAcceptanceTests.cs:10), [HttpCreateAcceptanceTests](tests/OrderService.IntegrationTests/Contracts/HttpCreateAcceptanceTests.cs:9) và [TransitionAcceptanceTests](tests/OrderService.IntegrationTests/Contracts/TransitionAcceptanceTests.cs:9).

### Evidence đã ghi nhận

Theo quality gate trong [docs/code-review.md](docs/code-review.md:5):

- Full PostgreSQL Testcontainers: **25 passed, 0 failed, 0 skipped**.
- Build `dotnet build OrderService.slnx --no-restore`: **0 warnings, 0 errors**.
- Đây là **local evidence được ghi nhận**, không thay thế một lần chạy CI độc lập. CI cần tự restore/build/test với PostgreSQL thật hoặc Testcontainers và không được silently skip acceptance gates.

## 12. Bảy câu lý thuyết Phần A

### Câu 1 — Race condition

Read-check-write không khóa cho phép nhiều request cùng đọc một available value cũ. Ví dụ 20 request cùng thấy stock 10 rồi cùng trừ từ bản copy riêng; các bản ghi cuối có thể ghi đè nhau, tạo lost update hoặc tạo nhiều Order hơn số stock thực tế. Implementation khóa row Product/Inventory bằng PostgreSQL trước khi đọc và mutate, nên request sau đọc giá trị committed mới.

### Câu 2 — Transaction

Transaction bảo đảm atomicity, consistency, isolation và durability cho một use case: Order, OrderItem, Inventory và idempotency response cùng commit hoặc rollback. Transaction tự nó không giải quyết mọi race condition; nếu vẫn đọc-check-ghi không lock/guard, các transaction vẫn có thể cạnh tranh. Baseline kết hợp transaction `READ COMMITTED`, row lock, thứ tự lock và state predicate.

### Câu 3 — Optimistic và pessimistic concurrency

Optimistic concurrency đọc version rồi phát hiện conflict khi update; phù hợp conflict thấp nhưng có thể phải retry nhiều và khó bao phủ nhiều row/idempotency. Pessimistic locking khóa row trước khi validate/mutate; chịu contention bằng lock wait nhưng dễ chứng minh atomicity hơn. Flash sale có hot product nên baseline chọn pessimistic PostgreSQL row lock, lock ngắn và thứ tự `ProductId ASC`.

### Câu 4 — Isolation level

`READ COMMITTED` tạo snapshot mới cho từng statement và vẫn cho phép non-repeatable read; `REPEATABLE READ` giữ snapshot lâu hơn; `SERIALIZABLE` mạnh hơn nhưng tăng serialization failure/retry. Service chọn `READ COMMITTED` vì mọi mutation quan trọng đều lock row và đọc lại sau lock. Không được xem isolation level là thay thế cho lock hoặc invariant.

### Câu 5 — Idempotency

Client gửi cùng key khi retry phải nhận cùng kết quả, không tạo Order/giữ stock lần hai. Service canonicalize payload, tạo SHA-256 fingerprint, claim unique `(scope,key)` bằng `ON CONFLICT DO NOTHING RETURNING`, thực hiện business trong cùng transaction và lưu status/body/location. Cùng fingerprint replay; khác fingerprint trả `409 IDEMPOTENCY_KEY_CONFLICT`.

### Câu 6 — Distributed C# lock

Không thể chỉ dùng `lock` trong C# khi có ba API server: mỗi process có vùng nhớ và lock object riêng, không chia sẻ giữa instance, restart hoặc scale-out. PostgreSQL row lock và unique constraint là cơ chế dùng chung mà mọi instance đều nhìn thấy.

### Câu 7 — Available, Reserved, Sold

`Available` là hàng chưa giữ; `Reserved` là hàng đang giữ cho PENDING order; `Sold` là phần reservation đã consume khi Order CONFIRMED. Schema baseline không có cột/ledger Sold riêng: confirm giảm Reserved, còn sold history là trạng thái Order và phần stock đã tiêu thụ được suy ra hạn chế. Reservation tách bước tạo đơn khỏi thanh toán, cho phép timeout/cancel trả hàng; ledger bán hàng đầy đủ nằm ngoài scope.

## 13. Known limitations và residual risks

Các hạn chế dưới đây lấy từ [code review](docs/code-review.md:86), không che giấu trong bàn giao:

- **Trusted principal/auth chưa có:** idempotency scope hiện fallback theo `CustomerId` từ request. Đây không phải security boundary; cần principal/tenant đã xác thực trước production multi-tenant.
- **Expiry retry/observability:** worker có catch và delay ngắn rồi poll lại, nhưng chưa có retry toàn transaction riêng với budget như API; counters `scanned`, `skipped-terminal`, duration, oldest-overdue và metrics/alert đầy đủ chưa hoàn thiện.
- **Không có stock ledger:** hai bucket available/reserved đáp ứng baseline nhưng không cung cấp audit opening stock, restock và sold history đầy đủ.
- **Idempotency retention:** response body và fingerprint được lưu để replay nhưng chưa có cleanup/retention policy; bảng có thể tăng không giới hạn.
- **Hot-product contention:** một Product flash-sale tập trung nhiều request vào cùng row lock. Đây là trade-off correctness; cần benchmark/ADR trước khi đổi sang guarded atomic update thuần.
- **Guarded set-based update:** source hiện dùng tracked entity mutation sau `FOR UPDATE`; affected-row guard cho mọi inventory mutation chưa được triển khai theo reference SQL.
- **Test scope:** evidence 25/25 không chứng minh crash sau commit trước response, production-scale pool exhaustion, query-plan performance, fuzzing, rate limiting hoặc authentication.
- **Security scope:** baseline không bao gồm authentication/authorization và TLS termination; production phải đặt API sau ingress/reverse proxy có HTTPS, authentication, authorization, rate limiting và secret manager. Swagger chỉ nên bật ở Development hoặc bảo vệ bằng access control.

## 14. Submission checklist đối chiếu đề

| Yêu cầu đề | Bằng chứng bàn giao |
|---|---|
| Four APIs | `POST /api/orders`, confirm, cancel và `GET /api/inventory/{productId}` trong [controllers](src/OrderService.Api/Controllers:1). |
| EF Core migration | [InitialCreate migration](src/OrderService.Infrastructure/Persistence/Migrations/20260817052012_InitialCreate.cs:9), startup `MigrateAsync`. |
| DTO và validation | Contracts trong [Contracts.cs](src/OrderService.Application/Contracts.cs:3), HTTP/header/payload validation trong [OrdersController](src/OrderService.Api/Controllers/OrdersController.cs:12). |
| Global exception handling | Exception handler và stable error envelope trong [Program.cs](src/OrderService.Api/Program.cs:34). |
| Structured/operational logging | `ILogger` cho retry và expiry worker; correlation ID qua middleware; residual metrics chi tiết được nêu ở limitations. |
| Swagger/OpenAPI | Swagger registration và Development UI trong [Program.cs](src/OrderService.Api/Program.cs:23). |
| Docker/Docker Compose | [Dockerfile](Dockerfile:1), PostgreSQL 17 container và health dependencies trong [docker-compose.yml](docker-compose.yml:1). |
| Automated concurrency tests | 20 request trên stock 10, multi-item lock order trong [concurrency tests](tests/OrderService.IntegrationTests/Concurrency/HttpConcurrencyAcceptanceTests.cs:13). |
| Automated idempotency tests | Concurrent same-key replay và fingerprint conflict trong [HttpConcurrencyAcceptanceTests](tests/OrderService.IntegrationTests/Concurrency/HttpConcurrencyAcceptanceTests.cs:47). |
| Automated expiry tests | Expiry, rerun, confirm/cancel race và multiple workers trong [TransitionAcceptanceTests](tests/OrderService.IntegrationTests/Contracts/TransitionAcceptanceTests.cs:143). |
| Không có business logic trong Controller | Controller chỉ validate HTTP boundary/delegate; mutation nằm trong [OrderApplicationService](src/OrderService.Infrastructure/Services/OrderApplicationService.cs:37) và [ExpiredReservationProcessor](src/OrderService.Infrastructure/Services/ExpiredReservationProcessor.cs:11). |
| Concurrency/idempotency explanation | Sections [Concurrency Strategy](#9-concurrency-strategy) và [Idempotency Flow](#10-idempotency-flow). |
| Test command cho mentor | Section [Test và quality evidence](#11-test-và-quality-evidence), gồm default, Testcontainers và external DB. |

### Tiêu chí nghiệm thu cốt lõi

- 20 request khác key, quantity 1 trên stock 10: đúng 10 success, 10 `OUT_OF_STOCK`, `available=0`, `reserved=10`.
- 5 request cùng key/payload: đúng 1 Order, một lần stock mutation, các response replay tương đương.
- Multi-item thiếu một sản phẩm: rollback toàn bộ reservation.
- Confirm/cancel/expiry race: chỉ một transition hợp lệ và một inventory delta phù hợp state cuối.
- Expiry rerun: không release lần hai.
- Mọi invariant PostgreSQL không có stock âm, Order rỗng, duplicate OrderItem hoặc duplicate idempotency scope/key.

---

**Trạng thái tài liệu:** handoff README cho baseline hiện tại. Evidence chất lượng là local evidence từ code review; mọi pipeline CI/release phải chạy độc lập và phải báo rõ khi database thiếu thay vì coi test bị skip là pass.
