using Microsoft.EntityFrameworkCore;
using OrderService.Domain;

namespace OrderService.Infrastructure.Persistence;

public sealed class OrderDbContext(DbContextOptions<OrderDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Inventory> Inventories => Set<Inventory>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<IdempotencyRequest> IdempotencyRequests => Set<IdempotencyRequest>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("public");
        ConfigureProduct(modelBuilder.Entity<Product>());
        ConfigureInventory(modelBuilder.Entity<Inventory>());
        ConfigureOrder(modelBuilder.Entity<Order>());
        ConfigureOrderItem(modelBuilder.Entity<OrderItem>());
        ConfigureIdempotency(modelBuilder.Entity<IdempotencyRequest>());
    }

    private static void ConfigureProduct(EntityTypeBuilder<Product> entity)
    {
        entity.ToTable("products", table =>
        {
            table.HasCheckConstraint("ck_products_sku_canonical", "length(btrim(sku)) > 0 AND sku = upper(btrim(sku))");
            table.HasCheckConstraint("ck_products_name_not_blank", "length(btrim(name)) > 0");
            table.HasCheckConstraint("ck_products_price_nonnegative", "price >= 0");
        });
        entity.HasKey(x => x.Id).HasName("pk_products");
        entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        entity.Property(x => x.Sku).HasColumnName("sku").HasMaxLength(64).IsRequired();
        entity.Property(x => x.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        entity.Property(x => x.Price).HasColumnName("price").HasPrecision(19, 4);
        entity.Property(x => x.IsActive).HasColumnName("is_active").HasDefaultValue(true);
        entity.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone");
        entity.HasIndex(x => x.Sku).IsUnique().HasDatabaseName("uq_products_sku");
        entity.HasOne(x => x.Inventory).WithOne(x => x.Product).HasForeignKey<Inventory>(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureInventory(EntityTypeBuilder<Inventory> entity)
    {
        entity.ToTable("inventories", table =>
        {
            table.HasCheckConstraint("ck_inventories_available_nonnegative", "available_quantity >= 0");
            table.HasCheckConstraint("ck_inventories_reserved_nonnegative", "reserved_quantity >= 0");
        });
        entity.HasKey(x => x.ProductId).HasName("pk_inventories");
        entity.Property(x => x.ProductId).HasColumnName("product_id");
        entity.Property(x => x.AvailableQuantity).HasColumnName("available_quantity");
        entity.Property(x => x.ReservedQuantity).HasColumnName("reserved_quantity");
        entity.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone");
    }

    private static void ConfigureOrder(EntityTypeBuilder<Order> entity)
    {
        entity.ToTable("orders", table =>
        {
            table.HasCheckConstraint("ck_orders_number_not_blank", "length(btrim(order_number)) > 0");
            table.HasCheckConstraint("ck_orders_status", "status IN ('PENDING', 'CONFIRMED', 'CANCELLED', 'EXPIRED')");
            table.HasCheckConstraint("ck_orders_total_nonnegative", "total_amount >= 0");
            table.HasCheckConstraint("ck_orders_event_times", "(confirmed_at IS NULL OR confirmed_at >= created_at) AND (cancelled_at IS NULL OR cancelled_at >= created_at)");
            table.HasCheckConstraint("ck_orders_terminal_timestamps", "(status = 'PENDING' AND confirmed_at IS NULL AND cancelled_at IS NULL) OR (status = 'CONFIRMED' AND confirmed_at IS NOT NULL AND cancelled_at IS NULL) OR (status = 'CANCELLED' AND confirmed_at IS NULL AND cancelled_at IS NOT NULL) OR (status = 'EXPIRED' AND confirmed_at IS NULL AND cancelled_at IS NULL)");
        });
        entity.HasKey(x => x.Id).HasName("pk_orders");
        entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        entity.Property(x => x.OrderNumber).HasColumnName("order_number").HasMaxLength(40).IsRequired();
        entity.Property(x => x.CustomerId).HasColumnName("customer_id");
        entity.Property(x => x.Status).HasColumnName("status").HasConversion<string>(x => x.ToString().ToUpperInvariant(), x => Enum.Parse<OrderStatus>(x, true)).HasMaxLength(16).HasDefaultValue(OrderStatus.Pending);
        entity.Property(x => x.TotalAmount).HasColumnName("total_amount").HasPrecision(19, 4);
        entity.Property(x => x.ReservationExpiredAt).HasColumnName("reservation_expired_at").HasColumnType("timestamp with time zone");
        entity.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone");
        entity.Property(x => x.ConfirmedAt).HasColumnName("confirmed_at").HasColumnType("timestamp with time zone");
        entity.Property(x => x.CancelledAt).HasColumnName("cancelled_at").HasColumnType("timestamp with time zone");
        entity.HasIndex(x => x.OrderNumber).IsUnique().HasDatabaseName("uq_orders_order_number");
        entity.HasIndex(x => new { x.ReservationExpiredAt, x.Id }).HasDatabaseName("ix_orders_pending_expiry").HasFilter("status = 'PENDING'");
    }

    private static void ConfigureOrderItem(EntityTypeBuilder<OrderItem> entity)
    {
        entity.ToTable("order_items", table =>
        {
            table.HasCheckConstraint("ck_order_items_quantity_positive", "quantity > 0");
            table.HasCheckConstraint("ck_order_items_unit_price_nonnegative", "unit_price >= 0");
        });
        entity.HasKey(x => new { x.OrderId, x.ProductId }).HasName("pk_order_items");
        entity.Property(x => x.OrderId).HasColumnName("order_id");
        entity.Property(x => x.ProductId).HasColumnName("product_id");
        entity.Property(x => x.Quantity).HasColumnName("quantity");
        entity.Property(x => x.UnitPrice).HasColumnName("unit_price").HasPrecision(19, 4);
        entity.HasOne(x => x.Order).WithMany(x => x.Items).HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.Product).WithMany(x => x.OrderItems).HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureIdempotency(EntityTypeBuilder<IdempotencyRequest> entity)
    {
        entity.ToTable("idempotency_requests", table =>
        {
            table.HasCheckConstraint("ck_idempotency_scope_canonical", "length(btrim(scope)) > 0 AND scope = btrim(scope)");
            table.HasCheckConstraint("ck_idempotency_key_canonical", "length(btrim(idempotency_key)) > 0 AND idempotency_key = btrim(idempotency_key)");
            table.HasCheckConstraint("ck_idempotency_path_canonical", "length(btrim(request_path)) > 0 AND request_path = btrim(request_path)");
            table.HasCheckConstraint("ck_idempotency_fingerprint_sha256", "octet_length(request_fingerprint) = 32");
            table.HasCheckConstraint("ck_idempotency_state", "state IN ('PROCESSING', 'COMPLETED')");
            table.HasCheckConstraint("ck_idempotency_http_status", "response_status IS NULL OR response_status BETWEEN 100 AND 599");
            table.HasCheckConstraint("ck_idempotency_completion", "(state = 'PROCESSING' AND completed_at IS NULL AND response_status IS NULL AND response_body IS NULL AND order_id IS NULL AND resource_location IS NULL) OR (state = 'COMPLETED' AND completed_at IS NOT NULL AND response_status IS NOT NULL AND response_body IS NOT NULL)");
        });
        entity.HasKey(x => x.Id).HasName("pk_idempotency_requests");
        entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        entity.Property(x => x.Scope).HasColumnName("scope").HasMaxLength(200).IsRequired();
        entity.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(128).IsRequired();
        entity.Property(x => x.RequestPath).HasColumnName("request_path").HasMaxLength(200).IsRequired();
        entity.Property(x => x.RequestFingerprint).HasColumnName("request_fingerprint").HasColumnType("bytea").IsRequired();
        entity.Property(x => x.State).HasColumnName("state").HasConversion<string>(x => x.ToString().ToUpperInvariant(), x => Enum.Parse<IdempotencyState>(x, true)).HasMaxLength(16).HasDefaultValue(IdempotencyState.Processing);
        entity.Property(x => x.ResponseStatus).HasColumnName("response_status");
        entity.Property(x => x.ResponseBody).HasColumnName("response_body").HasColumnType("jsonb");
        entity.Property(x => x.ResourceLocation).HasColumnName("resource_location").HasMaxLength(500);
        entity.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone");
        entity.Property(x => x.CompletedAt).HasColumnName("completed_at").HasColumnType("timestamp with time zone");
        entity.HasIndex(x => new { x.Scope, x.IdempotencyKey }).IsUnique().HasDatabaseName("uq_idempotency_scope_key");
        entity.HasIndex(x => new { x.CreatedAt, x.Id }).HasDatabaseName("ix_idempotency_completed_created_at").HasFilter("state = 'COMPLETED'");
        entity.Property(x => x.OrderId).HasColumnName("order_id");
        entity.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
    }
}
