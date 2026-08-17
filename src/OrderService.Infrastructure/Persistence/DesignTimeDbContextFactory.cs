using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OrderService.Infrastructure.Persistence;

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<OrderDbContext>
{
    public OrderDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<OrderDbContext>();
        builder.UseNpgsql("Host=localhost;Port=5432;Database=orderservice;Username=postgres;Password=postgres");
        return new OrderDbContext(builder.Options);
    }
}
