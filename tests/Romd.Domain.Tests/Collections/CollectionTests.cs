using Romd.Domain.Collections;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Collections;

public class CollectionTests
{
    [Fact]
    public void CreateNew_WithSurroundingWhitespace_TrimsDisplayDetails()
    {
        // Act
        var collection = Collection.CreateNew(
            "  Couch Favorites  ",
            "  Games for local multiplayer nights.  ");

        // Assert
        collection.Name.ShouldBe("Couch Favorites");
        collection.Description.ShouldBe("Games for local multiplayer nights.");
    }

    [Fact]
    public void AddItem_WithNewTitles_AppendsSequentialSortOrders()
    {
        // Arrange
        var collection = Collection.CreateNew("Favorites");

        // Act
        var firstAdded = collection.AddItem(101);
        var secondAdded = collection.AddItem(202);
        var thirdAdded = collection.AddItem(303);

        // Assert
        firstAdded.ShouldBeTrue();
        secondAdded.ShouldBeTrue();
        thirdAdded.ShouldBeTrue();
        collection.ItemCount.ShouldBe(3);
        collection.Items.Select(item => item.TitleId).ShouldBe([101, 202, 303]);
        collection.Items.Select(item => item.SortOrder).ShouldBe([0, 1, 2]);
    }

    [Fact]
    public void AddItem_WithDuplicateTitle_ReturnsFalseAndPreservesOriginalItem()
    {
        // Arrange
        var collection = Collection.CreateNew("Favorites");
        collection.AddItem(101, "Original note");

        // Act
        var added = collection.AddItem(101, "Replacement note");

        // Assert
        added.ShouldBeFalse();
        collection.ItemCount.ShouldBe(1);
        collection.Items.Single().Note.ShouldBe("Original note");
    }

    [Fact]
    public void RemoveItem_WithMiddleTitle_CompactsRemainingSortOrders()
    {
        // Arrange
        var collection = Collection.CreateNew("Favorites");
        collection.AddItem(101);
        collection.AddItem(202);
        collection.AddItem(303);

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
        var collection = Collection.CreateNew("Favorites");
        collection.AddItem(101);
        collection.AddItem(202);
        collection.AddItem(303);

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
        var collection = Collection.CreateNew("Favorites");
        collection.AddItem(101);
        collection.AddItem(202);
        collection.AddItem(303);
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
            () => CollectionItem.CreateNew(1, 101, 0, overlongNote));

        // Assert
        exception.ParamName.ShouldBe("note");
    }

    [Fact]
    public void UpdateNote_WithOverlongNote_ThrowsAndPreservesExistingNote()
    {
        // Arrange
        var maximumLengthNote = new string('x', 500);
        var item = CollectionItem.CreateNew(1, 101, 0, maximumLengthNote);

        // Act
        var exception = Should.Throw<ArgumentException>(
            () => item.UpdateNote(new string('y', 501)));

        // Assert
        exception.ParamName.ShouldBe("note");
        item.Note.ShouldBe(maximumLengthNote);
    }
}
