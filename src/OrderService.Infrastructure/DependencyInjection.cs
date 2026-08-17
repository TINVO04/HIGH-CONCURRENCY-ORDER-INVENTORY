using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OrderService.Application;
using OrderService.Infrastructure.Persistence;
using OrderService.Infrastructure.Services;

namespace OrderService.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("PostgreSQL")
            ?? configuration["ConnectionStrings:PostgreSQL"]
            ?? throw new InvalidOperationException("ConnectionStrings:PostgreSQL is required.");
        services.AddDbContextFactory<OrderDbContext>(options => options.UseNpgsql(connectionString, npgsql => npgsql.MigrationsAssembly(typeof(OrderDbContext).Assembly.FullName)));
        services.AddScoped<IOrderService, OrderApplicationService>();
        services.AddScoped<IInventoryService, InventoryApplicationService>();
        services.AddScoped<IExpiredReservationProcessor, ExpiredReservationProcessor>();
        services.AddHostedService<ExpiryBackgroundService>();
        return services;
    }
}
