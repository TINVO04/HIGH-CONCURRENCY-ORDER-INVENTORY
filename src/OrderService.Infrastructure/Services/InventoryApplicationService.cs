using Microsoft.EntityFrameworkCore;
using OrderService.Application;
using OrderService.Infrastructure.Persistence;

namespace OrderService.Infrastructure.Services;

public sealed class InventoryApplicationService(IDbContextFactory<OrderDbContext> contextFactory) : IInventoryService
{
    public async Task<InventoryResponse?> GetAsync(Guid productId, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Inventories.AsNoTracking().Where(x => x.ProductId == productId).Select(x => new InventoryResponse(x.ProductId, x.AvailableQuantity, x.ReservedQuantity)).SingleOrDefaultAsync(cancellationToken);
    }
}
