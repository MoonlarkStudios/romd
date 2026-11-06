using System.Data.Common;
using Romd.Application.Common.ReferenceCatalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Romd.Contracts.Common.ReferenceCatalog;
using Romd.Persistence;
using Romd.Persistence.ReferenceData;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.ReferenceData;

public sealed class SystemReadConsistencyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Read_ConcurrentAtomicEdit_ReturnsOneCommittedVersion(bool list)
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var writer = database.CreateContext();
        var commands = new TypedReferenceTestCommands(writer);
        (await commands.CreateCompany(new("local-nintendo", "Nintendo"))).IsError.ShouldBeFalse();
        (await commands.CreateCompany(new("local-sega", "Sega"))).IsError.ShouldBeFalse();
        var before = (await commands.CreateSystem(new("local-test", "Before", "TEST", ManufacturerKeys: ["local-nintendo"]))).Value;
        var gate = new BetweenQueries();
        var options = new DbContextOptionsBuilder<RomdDbContext>().UseNpgsql(database.ConnectionString).AddInterceptors(gate).Options;
        await using var readerDb = new RomdDbContext(options);
        var reader = new SystemReferenceReader(readerDb);
        var get = list ? null : reader.GetAsync("local-test", default);
        var all = list ? reader.ListAsync(ReferencePageRequest.Create("test").Value, default) : null;
        try
        {
            await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var after = await commands.UpdateSystem("local-test", new SystemPatchDto { Name = "After", ManufacturerKeys = ["local-sega"] }, before.Revision);
            after.IsError.ShouldBeFalse();
            gate.Resume.TrySetResult();
            if (list)
                System.Text.Json.JsonSerializer.Serialize((await all!).Items.Single()).ShouldBe(System.Text.Json.JsonSerializer.Serialize(before.Resource));
            else
            {
                var result = (await get!)!;
                result.Resource.Name.ShouldBe("Before");
                result.Resource.Manufacturers.Single().Name.ShouldBe("Nintendo");
                result.Revision.ShouldBe(before.Revision);
                result.Revision.ShouldNotBe(after.Value.Revision);
            }
            (await reader.GetAsync("local-test", default))!.Revision.ShouldBe(after.Value.Revision);
            readerDb.Database.CurrentTransaction.ShouldBeNull();
        }
        finally { gate.Resume.TrySetResult(); }
    }

    // Pause before the relationship query, after the platform SELECT has established
    // PostgreSQL's snapshot. No timing assumptions or sleeps are involved.
    private sealed class BetweenQueries : DbCommandInterceptor
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int paused;
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("\"SystemCompanies\"", StringComparison.Ordinal) && Interlocked.Exchange(ref paused, 1) == 0)
            {
                Reached.TrySetResult();
                await Resume.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            }
            return result;
        }
    }
}
