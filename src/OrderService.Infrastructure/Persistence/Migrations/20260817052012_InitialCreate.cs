using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderService.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "public");

            migrationBuilder.CreateTable(
                name: "orders",
                schema: "public",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "PENDING"),
                    total_amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    reservation_expired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_orders", x => x.id);
                    table.CheckConstraint("ck_orders_event_times", "(confirmed_at IS NULL OR confirmed_at >= created_at) AND (cancelled_at IS NULL OR cancelled_at >= created_at)");
                    table.CheckConstraint("ck_orders_number_not_blank", "length(btrim(order_number)) > 0");
                    table.CheckConstraint("ck_orders_status", "status IN ('PENDING', 'CONFIRMED', 'CANCELLED', 'EXPIRED')");
                    table.CheckConstraint("ck_orders_terminal_timestamps", "(status = 'PENDING' AND confirmed_at IS NULL AND cancelled_at IS NULL) OR (status = 'CONFIRMED' AND confirmed_at IS NOT NULL AND cancelled_at IS NULL) OR (status = 'CANCELLED' AND confirmed_at IS NULL AND cancelled_at IS NOT NULL) OR (status = 'EXPIRED' AND confirmed_at IS NULL AND cancelled_at IS NULL)");
                    table.CheckConstraint("ck_orders_total_nonnegative", "total_amount >= 0");
                });

            migrationBuilder.CreateTable(
                name: "products",
                schema: "public",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sku = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    price = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_products", x => x.id);
                    table.CheckConstraint("ck_products_name_not_blank", "length(btrim(name)) > 0");
                    table.CheckConstraint("ck_products_price_nonnegative", "price >= 0");
                    table.CheckConstraint("ck_products_sku_canonical", "length(btrim(sku)) > 0 AND sku = upper(btrim(sku))");
                });

            migrationBuilder.CreateTable(
                name: "idempotency_requests",
                schema: "public",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    request_path = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    request_fingerprint = table.Column<byte[]>(type: "bytea", nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "PROCESSING"),
                    order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    response_status = table.Column<short>(type: "smallint", nullable: true),
                    response_body = table.Column<string>(type: "jsonb", nullable: true),
                    resource_location = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_idempotency_requests", x => x.id);
                    table.CheckConstraint("ck_idempotency_completion", "(state = 'PROCESSING' AND completed_at IS NULL AND response_status IS NULL AND response_body IS NULL AND order_id IS NULL AND resource_location IS NULL) OR (state = 'COMPLETED' AND completed_at IS NOT NULL AND response_status IS NOT NULL AND response_body IS NOT NULL)");
                    table.CheckConstraint("ck_idempotency_fingerprint_sha256", "octet_length(request_fingerprint) = 32");
                    table.CheckConstraint("ck_idempotency_http_status", "response_status IS NULL OR response_status BETWEEN 100 AND 599");
                    table.CheckConstraint("ck_idempotency_key_canonical", "length(btrim(idempotency_key)) > 0 AND idempotency_key = btrim(idempotency_key)");
                    table.CheckConstraint("ck_idempotency_path_canonical", "length(btrim(request_path)) > 0 AND request_path = btrim(request_path)");
                    table.CheckConstraint("ck_idempotency_scope_canonical", "length(btrim(scope)) > 0 AND scope = btrim(scope)");
                    table.CheckConstraint("ck_idempotency_state", "state IN ('PROCESSING', 'COMPLETED')");
                    table.ForeignKey(
                        name: "FK_idempotency_requests_orders_order_id",
                        column: x => x.order_id,
                        principalSchema: "public",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "inventories",
                schema: "public",
                columns: table => new
                {
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    available_quantity = table.Column<int>(type: "integer", nullable: false),
                    reserved_quantity = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inventories", x => x.product_id);
                    table.CheckConstraint("ck_inventories_available_nonnegative", "available_quantity >= 0");
                    table.CheckConstraint("ck_inventories_reserved_nonnegative", "reserved_quantity >= 0");
                    table.ForeignKey(
                        name: "FK_inventories_products_product_id",
                        column: x => x.product_id,
                        principalSchema: "public",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "order_items",
                schema: "public",
                columns: table => new
                {
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_order_items", x => new { x.order_id, x.product_id });
                    table.CheckConstraint("ck_order_items_quantity_positive", "quantity > 0");
                    table.CheckConstraint("ck_order_items_unit_price_nonnegative", "unit_price >= 0");
                    table.ForeignKey(
                        name: "FK_order_items_orders_order_id",
                        column: x => x.order_id,
                        principalSchema: "public",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_order_items_products_product_id",
                        column: x => x.product_id,
                        principalSchema: "public",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_idempotency_completed_created_at",
                schema: "public",
                table: "idempotency_requests",
                columns: new[] { "created_at", "id" },
                filter: "state = 'COMPLETED'");

            migrationBuilder.CreateIndex(
                name: "IX_idempotency_requests_order_id",
                schema: "public",
                table: "idempotency_requests",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "uq_idempotency_scope_key",
                schema: "public",
                table: "idempotency_requests",
                columns: new[] { "scope", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_order_items_product_id",
                schema: "public",
                table: "order_items",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_orders_pending_expiry",
                schema: "public",
                table: "orders",
                columns: new[] { "reservation_expired_at", "id" },
                filter: "status = 'PENDING'");

            migrationBuilder.CreateIndex(
                name: "uq_orders_order_number",
                schema: "public",
                table: "orders",
                column: "order_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_products_sku",
                schema: "public",
                table: "products",
                column: "sku",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "idempotency_requests",
                schema: "public");

            migrationBuilder.DropTable(
                name: "inventories",
                schema: "public");

            migrationBuilder.DropTable(
                name: "order_items",
                schema: "public");

            migrationBuilder.DropTable(
                name: "orders",
                schema: "public");

            migrationBuilder.DropTable(
                name: "products",
                schema: "public");
        }
    }
}
