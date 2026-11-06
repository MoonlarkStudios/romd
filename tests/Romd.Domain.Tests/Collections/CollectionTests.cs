using Romd.Domain.Collections;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Collections;

public class CollectionTests
{
    private static readonly DateTimeOffset CreatedAt = new DateTimeOffset(2026, 11, 4, 12, 34, 56, TimeSpan.Zero).AddTicks(1234567);

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void WriteDetails_OverlongValue_RejectsBeforeChangingExistingDetails(bool nameTooLong, bool update)
    {
        var collection = Collection.CreateNew("Original", CreatedAt, "Original description", 12, 23);
        string name = nameTooLong ? new string('n', 201) : "Replacement";
        string? description = nameTooLong ? "Replacement description" : new string('d', 2001);

        var error = Should.Throw<ArgumentException>(() =>
        {
            if (update)
                collection.UpdateDetails(name, description, 34, 45);
            else
                Collection.CreateNew(name, CreatedAt, description);
        });

        error.ParamName.ShouldBe(nameTooLong ? "name" : "description");
        collection.Name.ShouldBe("Original");
        collection.Description.ShouldBe("Original description");
        collection.CoverMediaId.ShouldBe(12);
        collection.PlatformId.ShouldBe(23);
    }

    [Fact]
    public void WriteDetails_AtLimitsAfterTrimming_PreservesNormalizedValues()
    {
        var name = new string('n', Collection.MaxNameLength);
        var description = new string('d', Collection.MaxDescriptionLength);

        var collection = Collection.CreateNew($" {name} ", CreatedAt, $" {description} ");
        collection.UpdateDetails($" {name} ", $" {description} ", null, null);

        collection.Name.ShouldBe(name);
        collection.Description.ShouldBe(description);
    }

    [Fact]
    public void Items_ThroughMutableInterface_RejectsExternalChangesAndKeepsLiveView()
    {
        var collection = Collection.CreateNew("Favorites", CreatedAt);
        var items = collection.Items;
        collection.AddItem(101, CreatedAt);

        Should.Throw<NotSupportedException>(() => ((ICollection<CollectionItem>)items).Clear());

        collection.Items.ShouldBeSameAs(items);
        collection.ItemCount.ShouldBe(1);
        items.ShouldHaveSingleItem().TitleId.ShouldBe(101);
    }

    [Fact]
    public void CreateNew_WithSurroundingWhitespace_TrimsDisplayDetails()
    {
        // Act
        var collection = Collection.CreateNew(
            "  Couch Favorites  ",
            CreatedAt,
            "  Games for local multiplayer nights.  ");

        // Assert
        collection.Name.ShouldBe("Couch Favorites");
        collection.Description.ShouldBe("Games for local multiplayer nights.");
    }

    [Fact]
    public void AddItem_WithNewTitles_AppendsSequentialSortOrders()
    {
        // Arrange
        var collection = Collection.CreateNew("Favorites", CreatedAt);

        // Act
        var firstAdded = collection.AddItem(101, CreatedAt);
        var secondAdded = collection.AddItem(202, CreatedAt);
        var thirdAdded = collection.AddItem(303, CreatedAt);

        // Assert
        firstAdded.ShouldNotBeNull().TitleId.ShouldBe(101);
        secondAdded.ShouldNotBeNull().SortOrder.ShouldBe(1);
        thirdAdded.ShouldBeSameAs(collection.Items.Last());
        collection.ItemCount.ShouldBe(3);
        collection.Items.Select(item => item.TitleId).ShouldBe([101, 202, 303]);
        collection.Items.Select(item => item.SortOrder).ShouldBe([0, 1, 2]);
    }

    [Fact]
    public void AddItem_WithDuplicateTitle_ReturnsNullAndPreservesOriginalItem()
    {
        // Arrange
        var collection = Collection.CreateNew("Favorites", CreatedAt);
        collection.AddItem(101, CreatedAt, "Original note");

        // Act
        var added = collection.AddItem(101, CreatedAt.AddDays(1), new string('x', 501));

        // Assert
        added.ShouldBeNull();
        collection.ItemCount.ShouldBe(1);
        collection.Items.Single().Note.ShouldBe("Original note");
        collection.Items.Single().AddedAt.EqualsExact(CreatedAt).ShouldBeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(330)]
    [InlineData(-420)]
    public void CreateNew_ExplicitTimestamps_PreserveExactPrecisionAndOffset(int offsetMinutes)
    {
        var timestamp = new DateTimeOffset(2026, 11, 4, 12, 34, 56, TimeSpan.FromMinutes(offsetMinutes)).AddTicks(1234567);

        var collection = Collection.CreateNew("Favorites", timestamp);
        var item = CollectionItem.CreateNew(42, 7, 3, timestamp);
        var added = collection.AddItem(7, timestamp);

        collection.CreatedAt.EqualsExact(timestamp).ShouldBeTrue();
        item.AddedAt.EqualsExact(timestamp).ShouldBeTrue();
        added.ShouldNotBeNull().AddedAt.EqualsExact(timestamp).ShouldBeTrue();
    }

    [Fact]
    public void AddItem_DetailsAndOrderingChange_PreservesEachItemsAcceptedTimestamp()
    {
        var collection = Collection.CreateNew("Favorites", CreatedAt);
        var first = collection.AddItem(101, CreatedAt.AddDays(1))!;
        var second = collection.AddItem(202, CreatedAt.AddDays(2))!;

        collection.ReorderItems([202, 101]).ShouldBeTrue();
        second.UpdateNote("Updated note");
        collection.RemoveItem(101).ShouldBeTrue();

        first.AddedAt.EqualsExact(CreatedAt.AddDays(1)).ShouldBeTrue();
        second.AddedAt.EqualsExact(CreatedAt.AddDays(2)).ShouldBeTrue();
        collection.Items.ShouldHaveSingleItem().ShouldBeSameAs(second);
        second.SortOrder.ShouldBe(0);
        collection.CreatedAt.EqualsExact(CreatedAt).ShouldBeTrue();
    }

    [Fact]
    public void AddItem_OverlongNote_RejectsWithoutChangingItemsOrNextSortOrder()
    {
        var collection = Collection.CreateNew("Favorites", CreatedAt);
        var first = collection.AddItem(101, CreatedAt)!;

        Should.Throw<ArgumentException>(() => collection.AddItem(202, CreatedAt.AddDays(1), new string('x', 501)));
        var next = collection.AddItem(303, CreatedAt.AddDays(2));

        collection.Items.Select(item => item.TitleId).ShouldBe([101, 303]);
        first.AddedAt.EqualsExact(CreatedAt).ShouldBeTrue();
        next.ShouldNotBeNull().SortOrder.ShouldBe(1);
        next.AddedAt.EqualsExact(CreatedAt.AddDays(2)).ShouldBeTrue();
    }

    [Fact]
    public void RemoveItem_WithMiddleTitle_CompactsRemainingSortOrders()
    {
        // Arrange
        var collection = Collection.CreateNew("Favorites", CreatedAt);
        collection.AddItem(101, CreatedAt);
        collection.AddItem(202, CreatedAt);
        collection.AddItem(303, CreatedAt);

        // Act
        var removed = collection.RemoveItem(202);

        // Assert
        removed.ShouldBeTrue();
        collection.Items.OrderBy(item => item.SortOrder).Select(item => item.TitleId)
            .ShouldBe([101, 303]);
        collection.Items.OrderBy(item => item.SortOrder).Select(item => item.SortOrder)
            .ShouldBe([0, 1]);
    }

    [Fact]
    public void ReorderItems_WithExactTitleSet_UpdatesSortOrders()
    {
        // Arrange
        var collection = Collection.CreateNew("Favorites", CreatedAt);
        collection.AddItem(101, CreatedAt);
        collection.AddItem(202, CreatedAt);
        collection.AddItem(303, CreatedAt);

        // Act
        var reordered = collection.ReorderItems([303, 101, 202]);

        // Assert
        reordered.ShouldBeTrue();
        collection.Items.OrderBy(item => item.SortOrder).Select(item => item.TitleId)
            .ShouldBe([303, 101, 202]);
    }

    [Fact]
    public void ReorderItems_WithDifferentTitleSet_ReturnsFalseWithoutMutation()
    {
        // Arrange
        var collection = Collection.CreateNew("Favorites", CreatedAt);
        collection.AddItem(101, CreatedAt);
        collection.AddItem(202, CreatedAt);
        collection.AddItem(303, CreatedAt);
        var originalOrder = collection.Items
            .ToDictionary(item => item.TitleId, item => item.SortOrder);

        // Act
        var reordered = collection.ReorderItems([303, 101, 404]);

        // Assert
        reordered.ShouldBeFalse();
        collection.Items.ToDictionary(item => item.TitleId, item => item.SortOrder)
            .ShouldBe(originalOrder);
    }

    [Fact]
    public void CreateNew_WithOverlongNote_Throws()
    {
        // Arrange
        var overlongNote = new string('x', 501);

        // Act
        var exception = Should.Throw<ArgumentException>(
            () => CollectionItem.CreateNew(1, 101, 0, CreatedAt, overlongNote));

        // Assert
        exception.ParamName.ShouldBe("note");
    }

    [Fact]
    public void UpdateNote_WithOverlongNote_ThrowsAndPreservesExistingNote()
    {
        // Arrange
        var maximumLengthNote = new string('x', 500);
        var item = CollectionItem.CreateNew(1, 101, 0, CreatedAt, maximumLengthNote);

        // Act
        var exception = Should.Throw<ArgumentException>(
            () => item.UpdateNote(new string('y', 501)));

        // Assert
        exception.ParamName.ShouldBe("note");
        item.Note.ShouldBe(maximumLengthNote);
    }
}
