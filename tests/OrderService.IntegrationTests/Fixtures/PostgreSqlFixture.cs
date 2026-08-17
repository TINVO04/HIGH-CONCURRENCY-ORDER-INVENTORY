using Microsoft.EntityFrameworkCore;
using Npgsql;
using OrderService.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace OrderService.IntegrationTests.Fixtures;

public sealed class PostgreSqlFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    public string? ConnectionString { get; private set; }
    public bool IsAvailable => !string.IsNullOrWhiteSpace(ConnectionString);

    public async ValueTask InitializeAsync()
    {
        ConnectionString = Environment.GetEnvironmentVariable("TEST_DATABASE_CONNECTION_STRING") ?? Environment.GetEnvironmentVariable("ConnectionStrings__PostgreSQL");
        if (!string.IsNullOrWhiteSpace(ConnectionString)) return;
        if (!string.Equals(Environment.GetEnvironmentVariable("TESTCONTAINERS_ENABLED"), "true", StringComparison.OrdinalIgnoreCase)) return;
        _container = new PostgreSqlBuilder().WithImage("postgres:17-alpine").Build();
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null) await _container.DisposeAsync();
    }

    public async Task ApplyMigrationsAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAvailable) return;
        var options = new DbContextOptionsBuilder<OrderDbContext>().UseNpgsql(ConnectionString).Options;
        await using var db = new OrderDbContext(options);
        await db.Database.MigrateAsync(cancellationToken);
    }

    public async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAvailable) throw new InvalidOperationException("PostgreSQL is unavailable. Set TEST_DATABASE_CONNECTION_STRING or TESTCONTAINERS_ENABLED=true.");
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
