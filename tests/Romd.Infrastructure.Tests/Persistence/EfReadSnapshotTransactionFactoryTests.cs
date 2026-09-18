using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class EfReadSnapshotTransactionFactoryTests
{
    [Theory]
    [InlineData(EfReadSnapshotTransactionFactory.NpgsqlProviderName, IsolationLevel.RepeatableRead)]
    public void GetRequiredIsolationLevel_KnownProvider_RequestsVerifiedSnapshotIsolation(
        string providerName,
        IsolationLevel expected)
    {
        EfReadSnapshotTransactionFactory.GetRequiredIsolationLevel(providerName).ShouldBe(expected);
    }

    [Fact]
    public void GetRequiredIsolationLevel_UnknownProvider_FailsClosed()
    {
        Should.Throw<NotSupportedException>(() =>
                EfReadSnapshotTransactionFactory.GetRequiredIsolationLevel("Example.UnknownProvider"))
            .Message.ShouldContain("no verified repeatable read snapshot contract");
    }

    [Fact]
    public async Task BeginAsync_Npgsql_KeepsReadsOnTheOpeningSnapshotWhileAWriterCommits()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using (var seedContext = database.CreateContext())
        {
            seedContext.Platforms.Add(new PlatformEntity
            {
                Id = 1,
                Name = "Before",
                Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Before", BaseCompactLabel = "Before", CanonicalKey = "before", ShortName = "before",
                CreatedAt = DateTimeOffset.UtcNow
            });
            await seedContext.SaveChangesAsync();
        }

        await using var readContext = database.CreateContext();
        var factory = new EfReadSnapshotTransactionFactory(readContext);
        await using var snapshot = await factory.BeginAsync();

        readContext.Database.CurrentTransaction.ShouldNotBeNull();
        readContext.Database.CurrentTransaction.GetDbTransaction().IsolationLevel
            .ShouldBe(IsolationLevel.RepeatableRead);
        // The first statement fixes the snapshot; a committed writer must stay invisible to it.
        (await readContext.Platforms.AsNoTracking().SingleAsync()).Name.ShouldBe("Before");

        await using var writeContext = database.CreateContext();
        int changed = await writeContext.Platforms
            .Where(platform => platform.Id == 1)
            .ExecuteUpdateAsync(setters => setters.SetProperty(platform => platform.Name, "After"));
        changed.ShouldBe(1);

        (await readContext.Platforms.AsNoTracking().SingleAsync()).Name.ShouldBe("Before");
        await snapshot.CompleteAsync();
        (await writeContext.Platforms.AsNoTracking().SingleAsync()).Name.ShouldBe("After");
    }
}
