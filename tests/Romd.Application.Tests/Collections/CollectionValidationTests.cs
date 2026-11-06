using ErrorOr;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Collections;
using Romd.Admin.Application.Collections.Commands.AddTitle;
using Romd.Admin.Application.Collections.Commands.CreateCollection;
using Romd.Admin.Application.Collections.Commands.UpdateCollection;
using Romd.Admin.Application.Collections.Commands.UpdateItemNote;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Titles;
using Romd.Application.Common.Validation;
using Romd.Domain.Collections;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Collections;

public sealed class CollectionValidationTests
{
    private readonly ICollectionRepository _collections = Substitute.For<ICollectionRepository>();
    private readonly ICollectionCoverMediaReader _covers = Substitute.For<ICollectionCoverMediaReader>();
    private readonly IUnitOfWork _unit = Substitute.For<IUnitOfWork>();
    private readonly ITransaction _transaction = Substitute.For<ITransaction>();
    private readonly RecordingClock _clock = new();

    public CollectionValidationTests()
    {
        _unit.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(_transaction);
    }

    [Theory]
    [InlineData(false, "name", "Collections.NameTooLong")]
    [InlineData(true, "name", "Collections.NameTooLong")]
    [InlineData(false, "description", "Collections.DescriptionTooLong")]
    [InlineData(true, "description", "Collections.DescriptionTooLong")]
    [InlineData(false, "empty", "Collections.EmptyName")]
    [InlineData(true, "empty", "Collections.EmptyName")]
    public async Task WriteDetails_InvalidText_ReturnsFieldErrorWithoutStartingMutation(bool update, string field, string code)
    {
        string name = field switch { "name" => new string('n', 201), "empty" => " ", _ => "Valid" };
        string? description = field == "description" ? new string('d', 2001) : null;

        var error = await WriteDetailsAsync(update, name, description);

        error.Code.ShouldBe(code);
        FieldErrors.FieldOf(error).ShouldBe(field == "empty" ? "name" : field);
        _collections.ReceivedCalls().ShouldBeEmpty();
        _covers.ReceivedCalls().ShouldBeEmpty();
        _unit.ReceivedCalls().ShouldBeEmpty();
        _clock.ReadCount.ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteDetails_UnknownCover_ReturnsFieldErrorWithoutSavingOrCommitting(bool update)
    {
        _collections.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(Collection.CreateNew("Original", DateTimeOffset.UnixEpoch));
        _covers.LockStagedAsync(91, Arg.Any<CancellationToken>()).Returns(false);

        var error = await WriteDetailsAsync(update, "Valid", null, 91);

        error.Code.ShouldBe("Collections.CoverMediaNotFound");
        FieldErrors.FieldOf(error).ShouldBe("coverMediaId");
        await _collections.DidNotReceive().AddStagedAsync(Arg.Any<Collection>(), Arg.Any<CancellationToken>());
        await _collections.DidNotReceive().UpdateDetailsStagedAsync(Arg.Any<Collection>(), Arg.Any<CancellationToken>());
        await _transaction.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteNote_OverlongNote_ReturnsFieldErrorWithoutStartingMutation(bool update)
    {
        string note = new('n', 501);
        Error error;
        if (update)
        {
            error = (await new UpdateCollectionItemNoteCommandHandler(TestSystemCatalog.Create(), _collections,
                _unit, NullLogger<UpdateCollectionItemNoteCommandHandler>.Instance)
                .HandleAsync(new(7, 23, note))).FirstError;
        }
        else
        {
            error = (await new AddTitleToCollectionCommandHandler(TestSystemCatalog.Create(), _collections,
                Substitute.For<ITitleReader>(), _unit, _clock, NullLogger<AddTitleToCollectionCommandHandler>.Instance)
                .HandleAsync(new(7, 23, note))).FirstError;
        }

        error.Code.ShouldBe("Collections.NoteTooLong");
        FieldErrors.FieldOf(error).ShouldBe("note");
        _collections.ReceivedCalls().ShouldBeEmpty();
        _unit.ReceivedCalls().ShouldBeEmpty();
        _clock.ReadCount.ShouldBe(0);
    }

    [Fact]
    public async Task AddTitle_OverlongNoteForExistingTitle_RejectsBeforeReadingStateOrTime()
    {
        var existing = CollectionItem.Rehydrate(1, 7, 23, 0, "Original note", DateTimeOffset.UnixEpoch);
        var collection = Collection.Rehydrate(7, "Original", null, null, null, false, 0,
            DateTimeOffset.UnixEpoch, [existing]);
        _collections.LockForItemMutationStagedAsync(7, Arg.Any<CancellationToken>()).Returns(collection);
        var titles = Substitute.For<ITitleReader>();
        var handler = new AddTitleToCollectionCommandHandler(TestSystemCatalog.Create(), _collections, titles,
            _unit, _clock, NullLogger<AddTitleToCollectionCommandHandler>.Instance);

        var result = await handler.HandleAsync(new(7, 23, new string('n', 501)));

        result.FirstError.Code.ShouldBe("Collections.NoteTooLong");
        FieldErrors.FieldOf(result.FirstError).ShouldBe("note");
        collection.Items.ShouldHaveSingleItem().ShouldBeSameAs(existing);
        existing.Note.ShouldBe("Original note");
        existing.AddedAt.ShouldBe(DateTimeOffset.UnixEpoch);
        _collections.ReceivedCalls().ShouldBeEmpty();
        titles.ReceivedCalls().ShouldBeEmpty();
        _unit.ReceivedCalls().ShouldBeEmpty();
        _clock.ReadCount.ShouldBe(0);
    }

    private async Task<Error> WriteDetailsAsync(bool update, string name, string? description, int? mediaId = null)
    {
        if (update)
            return (await new UpdateCollectionCommandHandler(TestSystemCatalog.Create(), _collections, _covers,
                _unit, NullLogger<UpdateCollectionCommandHandler>.Instance)
                .HandleAsync(new(7, name, description, mediaId, null))).FirstError;

        return (await new CreateCollectionCommandHandler(TestSystemCatalog.Create(), _collections, _covers,
            _unit, _clock, NullLogger<CreateCollectionCommandHandler>.Instance)
            .HandleAsync(new(name, description, mediaId, null))).FirstError;
    }

    private sealed class RecordingClock : TimeProvider
    {
        public int ReadCount { get; private set; }

        public override DateTimeOffset GetUtcNow()
        {
            ReadCount++;
            return DateTimeOffset.UnixEpoch;
        }
    }
}
