namespace OrderService.Domain;

public enum OrderStatus
{
    Pending,
    Confirmed,
    Cancelled,
    Expired
}

public enum IdempotencyState
{
    Processing,
    Completed
}

public sealed class Product
{
    private Product() { }

    public Product(Guid id, string sku, string name, decimal price, bool isActive, DateTimeOffset createdAt)
    {
        Id = id;
        Sku = sku;
        Name = name;
        Price = price;
        IsActive = isActive;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public string Sku { get; private set; } = null!;
    public string Name { get; private set; } = null!;
    public decimal Price { get; private set; }
    public bool IsActive { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public Inventory? Inventory { get; private set; }
    public ICollection<OrderItem> OrderItems { get; } = new List<OrderItem>();
}

public sealed class Inventory
{
    private Inventory() { }

    public Inventory(Guid productId, int availableQuantity, int reservedQuantity, DateTimeOffset updatedAt)
    {
        ProductId = productId;
        AvailableQuantity = availableQuantity;
        ReservedQuantity = reservedQuantity;
        UpdatedAt = updatedAt;
    }

    public Guid ProductId { get; private set; }
    public int AvailableQuantity { get; private set; }
    public int ReservedQuantity { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public Product Product { get; private set; } = null!;

    public void Reserve(int quantity, DateTimeOffset now)
    {
        if (quantity <= 0 || AvailableQuantity < quantity)
        {
            throw new DomainException("OUT_OF_STOCK", "One or more products do not have enough available stock.");
        }

        AvailableQuantity -= quantity;
        ReservedQuantity = checked(ReservedQuantity + quantity);
        UpdatedAt = now;
    }

    public void ConsumeReservation(int quantity, DateTimeOffset now)
    {
        if (quantity <= 0 || ReservedQuantity < quantity)
        {
            throw new DomainException("INVENTORY_INVARIANT_VIOLATION", "Reserved inventory is inconsistent with the order.");
        }

        ReservedQuantity -= quantity;
        UpdatedAt = now;
    }

    public void ReleaseReservation(int quantity, DateTimeOffset now)
    {
        if (quantity <= 0 || ReservedQuantity < quantity)
        {
            throw new DomainException("INVENTORY_INVARIANT_VIOLATION", "Reserved inventory is inconsistent with the order.");
        }

        ReservedQuantity -= quantity;
        AvailableQuantity = checked(AvailableQuantity + quantity);
        UpdatedAt = now;
    }
}

public sealed class Order
{
    private Order() { }

    public Order(Guid id, string orderNumber, Guid customerId, decimal totalAmount, DateTimeOffset createdAt, DateTimeOffset reservationExpiredAt)
    {
        Id = id;
        OrderNumber = orderNumber;
        CustomerId = customerId;
        Status = OrderStatus.Pending;
        TotalAmount = totalAmount;
        CreatedAt = createdAt;
        ReservationExpiredAt = reservationExpiredAt;
    }

    public Guid Id { get; private set; }
    public string OrderNumber { get; private set; } = null!;
    public Guid CustomerId { get; private set; }
    public OrderStatus Status { get; private set; }
    public decimal TotalAmount { get; private set; }
    public DateTimeOffset ReservationExpiredAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ConfirmedAt { get; private set; }
    public DateTimeOffset? CancelledAt { get; private set; }
    public ICollection<OrderItem> Items { get; } = new List<OrderItem>();

    public void AddItem(Guid productId, int quantity, decimal unitPrice)
        => Items.Add(new OrderItem(Id, productId, quantity, unitPrice));

    public void Confirm(DateTimeOffset now)
    {
        if (Status != OrderStatus.Pending) throw new DomainException("ORDER_STATE_CONFLICT", "The order is not pending.");
        if (ReservationExpiredAt <= now) throw new DomainException("ORDER_EXPIRED", "The order reservation has expired.");
        Status = OrderStatus.Confirmed;
        ConfirmedAt = now;
    }

    public void Cancel(DateTimeOffset now)
    {
        if (Status != OrderStatus.Pending) throw new DomainException(Status == OrderStatus.Expired ? "ORDER_EXPIRED" : "ORDER_STATE_CONFLICT", "The order cannot be cancelled in its current state.");
        if (ReservationExpiredAt <= now) throw new DomainException("ORDER_EXPIRED", "The order reservation has expired.");
        Status = OrderStatus.Cancelled;
        CancelledAt = now;
    }

    public void Expire(DateTimeOffset now)
    {
        if (Status != OrderStatus.Pending) throw new DomainException("ORDER_STATE_CONFLICT", "The order is not pending.");
        if (ReservationExpiredAt > now) throw new DomainException("ORDER_NOT_EXPIRED", "The order reservation has not expired.");
        Status = OrderStatus.Expired;
    }
}

public sealed class OrderItem
{
    private OrderItem() { }

    public OrderItem(Guid orderId, Guid productId, int quantity, decimal unitPrice)
    {
        OrderId = orderId;
        ProductId = productId;
        Quantity = quantity;
        UnitPrice = unitPrice;
    }

    public Guid OrderId { get; private set; }
    public Guid ProductId { get; private set; }
    public int Quantity { get; private set; }
    public decimal UnitPrice { get; private set; }
    public Order Order { get; private set; } = null!;
    public Product Product { get; private set; } = null!;
}

public sealed class IdempotencyRequest
{
    private IdempotencyRequest() { }

    public IdempotencyRequest(Guid id, string scope, string idempotencyKey, string requestPath, byte[] requestFingerprint, DateTimeOffset createdAt)
    {
        Id = id;
        Scope = scope;
        IdempotencyKey = idempotencyKey;
        RequestPath = requestPath;
        RequestFingerprint = requestFingerprint;
        State = IdempotencyState.Processing;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public string Scope { get; private set; } = null!;
    public string IdempotencyKey { get; private set; } = null!;
    public string RequestPath { get; private set; } = null!;
    public byte[] RequestFingerprint { get; private set; } = null!;
    public IdempotencyState State { get; private set; }
    public Guid? OrderId { get; private set; }
    public short? ResponseStatus { get; private set; }
    public string? ResponseBody { get; private set; }
    public string? ResourceLocation { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    public void Complete(short status, string body, DateTimeOffset completedAt, Guid? orderId = null, string? resourceLocation = null)
    {
        State = IdempotencyState.Completed;
        ResponseStatus = status;
        ResponseBody = body;
        CompletedAt = completedAt;
        OrderId = orderId;
        ResourceLocation = resourceLocation;
    }
}

public sealed class DomainException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
