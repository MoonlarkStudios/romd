using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

/// <summary>Scale fixtures put heavy load on the assembly's shared PostgreSQL test server. Running them alone keeps
/// them from starving each other and every other database test, which pushed single queries past their command
/// timeout and left the suite without a completed test for minutes.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PostgreSqlScaleWorkloads
{
    public const string Name = "PostgreSQL scale workloads";
}
