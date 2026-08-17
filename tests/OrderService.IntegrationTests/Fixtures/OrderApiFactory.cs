using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OrderService.Api.Controllers;
using OrderService.Infrastructure.Services;

namespace OrderService.IntegrationTests.Fixtures;

public sealed class OrderApiFactory(string connectionString) : WebApplicationFactory<OrdersController>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:PostgreSQL", connectionString);
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.Sources.Clear();
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:PostgreSQL"] = connectionString,
                ["OrderService:ReservationDuration"] = "00:15:00",
                ["OrderService:MaxTransactionRetries"] = "3",
                ["OrderService:ExpiryBatchSize"] = "50",
                ["OrderService:ExpiryPollInterval"] = "01:00:00"
            });
        });
        builder.ConfigureServices(services =>
        {
            var expiryRegistrations = services
                .Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
                    && descriptor.ImplementationType == typeof(ExpiryBackgroundService))
                .ToArray();

            foreach (var registration in expiryRegistrations)
            {
                services.Remove(registration);
            }
        });
    }
}
