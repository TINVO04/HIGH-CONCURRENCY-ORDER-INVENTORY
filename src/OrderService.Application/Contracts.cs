namespace OrderService.Application;

public sealed record CreateOrderRequest(Guid CustomerId, IReadOnlyList<CreateOrderItemRequest> Items);
public sealed record CreateOrderItemRequest(Guid ProductId, int Quantity);
public sealed record OrderItemResponse(Guid ProductId, int Quantity, decimal UnitPrice);
public sealed record OrderResponse(Guid OrderId, string OrderNumber, Guid CustomerId, string Status, decimal TotalAmount, DateTimeOffset ReservationExpiredAt, IReadOnlyList<OrderItemResponse> Items);
public sealed record InventoryResponse(Guid ProductId, int Available, int Reserved);
public sealed record ErrorResponse(string Code, string Message, string TraceId, IReadOnlyList<string> Details);
public sealed record OperationResult<T>(T? Value, int StatusCode, string? Location = null, bool IsReplay = false, ErrorResponse? Error = null);

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public interface IOrderService
{
    Task<OperationResult<OrderResponse>> CreateAsync(CreateOrderRequest request, string idempotencyKey, string requestPath, CancellationToken cancellationToken);
    Task<OperationResult<OrderResponse>> ConfirmAsync(Guid orderId, CancellationToken cancellationToken);
    Task<OperationResult<OrderResponse>> CancelAsync(Guid orderId, CancellationToken cancellationToken);
}

public interface IInventoryService
{
    Task<InventoryResponse?> GetAsync(Guid productId, CancellationToken cancellationToken);
}

public interface IExpiredReservationProcessor
{
    Task<int> ProcessAsync(CancellationToken cancellationToken);
}

public sealed class OrderServiceOptions
{
    public const string SectionName = "OrderService";
    public TimeSpan ReservationDuration { get; set; } = TimeSpan.FromMinutes(15);
    public int MaxTransactionRetries { get; set; } = 3;
    public int ExpiryBatchSize { get; set; } = 50;
    public TimeSpan ExpiryPollInterval { get; set; } = TimeSpan.FromSeconds(10);
    public int LockTimeoutSeconds { get; set; } = 2;
    public int StatementTimeoutSeconds { get; set; } = 5;
}
