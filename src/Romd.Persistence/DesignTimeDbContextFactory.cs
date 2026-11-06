using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using OpenIddict.EntityFrameworkCore;

namespace Romd.Persistence;

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<RomdDbContext>
{
    // Migration scaffolding never opens this connection; the provider only needs a well-formed string.
    private const string DesignTimeConnectionString = "Host=localhost;Database=romd;Username=romd";

    public RomdDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<RomdDbContext>();
        PostgreSqlConfiguration.Configure(optionsBuilder, DesignTimeConnectionString);
        optionsBuilder.UseOpenIddict();
        return new RomdDbContext(optionsBuilder.Options);
    }
}
