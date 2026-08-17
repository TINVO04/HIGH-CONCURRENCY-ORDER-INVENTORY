using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OrderService.Application;
using OrderService.Domain;
using OrderService.Infrastructure;
using OrderService.Infrastructure.Persistence;

namespace OrderService.Api;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddProblemDetails();
        builder.Services.AddControllers();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(options => options.SwaggerDoc("v1", new() { Title = "Limited Stock Order Service", Version = "v1" }));
        builder.Services.AddHealthChecks().AddCheck<PostgreSqlHealthCheck>("postgresql");
        builder.Services.AddOptions<OrderServiceOptions>().BindConfiguration(OrderServiceOptions.SectionName).Validate(x => x.ReservationDuration > TimeSpan.Zero && x.MaxTransactionRetries is >= 1 and <= 3 && x.ExpiryBatchSize is >= 1 and <= 100, "OrderService options are invalid.").ValidateOnStart();
        builder.Services.AddSingleton<IClock, SystemClock>();
        builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<OrderServiceOptions>>().Value);
        builder.Services.AddInfrastructure(builder.Configuration);
        builder.Services.AddSingleton<PostgreSqlHealthCheck>();

        var app = builder.Build();
        app.UseMiddleware<CorrelationMiddleware>();
        app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
        {
            var feature = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
            var exception = feature?.Error;
            var traceId = context.TraceIdentifier;
            var (status, code, message) = exception switch
            {
                DomainException domain => domain.Code switch
                {
                    "PRODUCT_NOT_FOUND" or "PRODUCT_INACTIVE" or "ORDER_NOT_FOUND" => (404, domain.Code, domain.Message),
                    "OUT_OF_STOCK" or "IDEMPOTENCY_KEY_CONFLICT" or "ORDER_EXPIRED" or "ORDER_STATE_CONFLICT" => (409, domain.Code, domain.Message),
                    _ => (500, domain.Code, domain.Message)
                },
                _ => (500, "INTERNAL_ERROR", "An unexpected error occurred.")
            };
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new ErrorResponse(code, message, traceId, []));
        }));
        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }
        app.MapControllers();
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
        app.MapHealthChecks("/health/ready");

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
            await DbInitializer.InitializeAsync(db);
        }
        await app.RunAsync();
    }
}

public sealed class PostgreSqlHealthCheck(IDbContextFactory<OrderDbContext> contextFactory) : Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck
{
    public async Task<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult> CheckHealthAsync(Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Database.CanConnectAsync(cancellationToken)
            ? Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy()
            : Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Unhealthy();
    }
}

public sealed class CorrelationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers.TryGetValue("X-Correlation-ID", out var supplied) && !string.IsNullOrWhiteSpace(supplied) ? supplied.ToString() : context.TraceIdentifier;
        context.TraceIdentifier = correlationId;
        context.Response.Headers["X-Correlation-ID"] = correlationId;
        await next(context);
    }
}
