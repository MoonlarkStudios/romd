using Microsoft.EntityFrameworkCore;
using OpenIddict.EntityFrameworkCore;
using Romd.Consumer.Application.Libraries;
using Romd.Domain.Identity;
using Romd.Domain.Libraries;
using Romd.Infrastructure.Identity;
using Romd.Persistence.Identity;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Queries;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class LiveConsumerLibraryQueryTests : IAsyncDisposable
{
    private static readonly Guid UserId = Guid.Parse("a61f809e-bb71-416e-9823-cb5192806167");
    private readonly PostgreSqlTestDatabase _connection;
    private readonly DbContextOptions<RomdDbContext> _options;
    private readonly int _libraryId;

    public LiveConsumerLibraryQueryTests()
    {
        _connection = PostgreSqlTestDatabase.Create();
        _options = new DbContextOptionsBuilder<RomdDbContext>()
            .UseNpgsql(_connection.ConnectionString)
            .UseOpenIddict()
            .Options;

        using var context = CreateContext();
        var library = LibraryEntity.FromDomain(Library.CreateNew("Strict config", new LibraryConfiguration()));
        library.NeedsMaterialization = false;
        context.Libraries.Add(library);
        context.SaveChanges();
        _libraryId = library.Id;
        context.Users.Add(new RomdUser
        {
            Id = UserId,
            UserName = "strict-config-user",
            NormalizedUserName = "STRICT-CONFIG-USER",
            Email = "strict-config@example.test",
            NormalizedEmail = "STRICT-CONFIG@EXAMPLE.TEST",
            LibraryId = library.Id,
            CreatedAt = DateTimeOffset.UtcNow
        });
        context.SaveChanges();
    }

    [Fact]
    public async Task ReadAsync_ValidConfiguration_ExecutesProjectionInsideAuthoritativeRead()
    {
        await using var context = CreateContext();
        bool executed = false;

        var read = await new LiveConsumerLibraryQuery(context).ReadAsync(
            new ConsumerLibraryScope(UserId),
            (libraryId, _) =>
            {
                executed = true;
                libraryId.ShouldBe(_libraryId);
                return Task.FromResult<ConsumerLibraryProjectionResult<int>>(
                    new ConsumerLibraryProjectionResult<int>.Found(42));
            });

        executed.ShouldBeTrue();
        var found = read.ShouldBeOfType<ConsumerLibraryReadResult<int>.Found>();
        found.LibraryId.ShouldBe(_libraryId);
        found.Value.ShouldBe(42);
    }

    [Fact]
    public async Task ReadAsync_ProjectionItemNotFound_AttachesExactLiveLibraryId()
    {
        await using var context = CreateContext();

        var read = await new LiveConsumerLibraryQuery(context).ReadAsync<int>(
            new ConsumerLibraryScope(UserId),
            (_, _) => Task.FromResult<ConsumerLibraryProjectionResult<int>>(
                new ConsumerLibraryProjectionResult<int>.ItemNotFound()));

        var notFound = read.ShouldBeOfType<ConsumerLibraryReadResult<int>.ItemNotFound>();
        notFound.LibraryId.ShouldBe(_libraryId);
    }

    [Fact]
    public async Task ReadAsync_ProjectionInconsistent_DoesNotExposeLibraryId()
    {
        await using var context = CreateContext();

        var read = await new LiveConsumerLibraryQuery(context).ReadAsync<int>(
            new ConsumerLibraryScope(UserId),
            (_, _) => Task.FromResult<ConsumerLibraryProjectionResult<int>>(
                new ConsumerLibraryProjectionResult<int>.ProjectionInconsistent()));

        read.ShouldBeOfType<ConsumerLibraryReadResult<int>.ProjectionInconsistent>();
    }

    [Theory]
    [InlineData(LibraryConfigurationState.Valid, "{")]
    [InlineData(LibraryConfigurationState.Valid, "null")]
    [InlineData(LibraryConfigurationState.Valid, "{\"ContentRatingPolicy\":{\"MaxMinimumAge\":-1}}")]
    [InlineData(LibraryConfigurationState.Valid, "{\"AllowedPlatformIds\":null}")]
    [InlineData(LibraryConfigurationState.Invalid, "{}")]
    [InlineData(LibraryConfigurationState.RequiresMigration, "{}")]
    public async Task ReadAsync_InvalidPersistedConfiguration_DoesNotExecuteProjection(
        LibraryConfigurationState state,
        string configurationJson)
    {
        await SetConfigurationAsync(state.ToString(), configurationJson);

        await AssertNoAuthoritativeReadAsync();
    }

    [Fact]
    public async Task ReadAsync_UnknownPersistedConfigurationState_DoesNotExecuteProjection()
    {
        await SetConfigurationAsync("FutureState", "{}");

        await AssertNoAuthoritativeReadAsync();
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    private RomdDbContext CreateContext() => new(_options);

    private async Task SetConfigurationAsync(string state, string configurationJson)
    {
        await using var context = CreateContext();
        int changed = await context.Libraries
            .Where(library => library.Id == _libraryId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(library => library.ConfigurationState, state)
                .SetProperty(library => library.ConfigurationJson, configurationJson));
        changed.ShouldBe(1);
    }

    private async Task AssertNoAuthoritativeReadAsync()
    {
        await using var context = CreateContext();
        bool executed = false;

        var read = await new LiveConsumerLibraryQuery(context).ReadAsync(
            new ConsumerLibraryScope(UserId),
            (_, _) =>
            {
                executed = true;
                return Task.FromResult<ConsumerLibraryProjectionResult<int>>(
                    new ConsumerLibraryProjectionResult<int>.Found(42));
            });

        executed.ShouldBeFalse();
        read.ShouldBeOfType<ConsumerLibraryReadResult<int>.LibraryUnavailable>();
    }
}
