using Microsoft.EntityFrameworkCore;
using OrderService.Domain;

namespace OrderService.Infrastructure.Persistence;

public static class DbInitializer
{
    public static async Task InitializeAsync(OrderDbContext db, CancellationToken cancellationToken = default)
    {
        await db.Database.MigrateAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var productId = SeedIds.ProductA;
        var product = await db.Products.SingleOrDefaultAsync(x => x.Sku == "PRODUCT-A", cancellationToken);
        if (product is null)
        {
            product = new Product(productId, "PRODUCT-A", "Product A", 10.00m, true, now);
            db.Products.Add(product);
            db.Inventories.Add(new Inventory(productId, 10, 0, now));
            await db.SaveChangesAsync(cancellationToken);
        }
        else if (!await db.Inventories.AnyAsync(x => x.ProductId == product.Id, cancellationToken))
        {
            db.Inventories.Add(new Inventory(product.Id, 10, 0, now));
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public static class SeedIds
    {
        public static readonly Guid ProductA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    }
}
