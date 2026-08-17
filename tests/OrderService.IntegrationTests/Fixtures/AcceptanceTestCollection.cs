using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace OrderService.IntegrationTests.Fixtures;

[SuppressMessage("Naming", "CA1711", Justification = "xUnit collection name marker used by acceptance tests.")]
public static class AcceptanceTestCollection
{
    public const string Name = "PostgreSQL acceptance database";
}

[CollectionDefinition(AcceptanceTestCollection.Name, DisableParallelization = true)]
public sealed class AcceptanceTests : ICollectionFixture<PostgreSqlFixture>
{
}
