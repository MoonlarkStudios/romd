using Romd.Domain.Libraries;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Libraries;

public sealed class LibraryBehavioralTimeTests
{
    private static readonly DateTimeOffset Created = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(5.5)).AddTicks(1234567);
    private static readonly DateTimeOffset Changed = Created.AddDays(2).ToOffset(TimeSpan.FromHours(-3.5));

    [Fact]
    public void CreateNew_ExplicitInstant_RetainsExactPrecisionAndOffset()
    {
        var library = Library.CreateNew("Library", new(), Created);

        AssertExact(library.CreatedAt, Created);
        library.UpdatedAt.ShouldBeNull();
        library.MaterializationGeneration.ShouldBe(0);
        library.MaterializationRevision.ShouldNotBe(Guid.Empty);
    }

    [Theory]
    [InlineData("configuration")]
    [InlineData("invalid")]
    [InlineData("default")]
    [InlineData("clear-default")]
    [InlineData("invalidate")]
    public void Mutation_ExplicitInstant_UpdatesTimestampAndRevisionWithoutChangingCreation(string mutation)
    {
        var library = Library.CreateNew("Library", new(), Created);
        if (mutation == "clear-default")
            library.MarkAsDefault(Created);
        Guid previousRevision = library.MaterializationRevision;

        switch (mutation)
        {
            case "configuration":
                library.UpdateConfiguration("Updated", new(), Changed);
                break;
            case "invalid":
                library.MarkConfigurationInvalid("Missing reference", Changed);
                break;
            case "default":
                library.MarkAsDefault(Changed);
                break;
            case "clear-default":
                library.UnmarkAsDefault(Changed);
                break;
            case "invalidate":
                library.FlagForRematerialization(Changed);
                break;
        }

        AssertExact(library.CreatedAt, Created);
        AssertExact(library.UpdatedAt.ShouldNotBeNull(), Changed);
        library.MaterializationRevision.ShouldNotBe(previousRevision);
        library.MaterializationGeneration.ShouldBe(0);
    }

    [Fact]
    public void MarkMaterialized_ExplicitInstant_RecordsPublicationWithoutChangingEditRevisionOrTime()
    {
        var library = Library.CreateNew("Library", new(), Created);
        library.MarkAsDefault(Created);
        Guid revision = library.MaterializationRevision;

        library.MarkMaterialized(12, Changed);

        AssertExact(library.LastMaterializedAt.ShouldNotBeNull(), Changed);
        AssertExact(library.UpdatedAt.ShouldNotBeNull(), Created);
        library.MaterializationRevision.ShouldBe(revision);
        library.ItemCount.ShouldBe(12);
        library.NeedsMaterialization.ShouldBeFalse();
        library.NeedsRefresh.ShouldBeFalse();
    }

    [Theory]
    [InlineData("name")]
    [InlineData("configuration")]
    [InlineData("invalid-reason")]
    public void Mutation_RejectedInput_PreservesTimestampRevisionAndPublishedState(string invalid)
    {
        var library = Library.CreateNew("Library", new(), Created);
        library.MarkAsDefault(Created);
        library.MarkMaterialized(12, Created);
        Guid revision = library.MaterializationRevision;

        Should.Throw<ArgumentException>(() =>
        {
            if (invalid == "name")
                library.UpdateConfiguration(" ", new(), Changed);
            else if (invalid == "configuration")
                library.UpdateConfiguration("Changed", new() { AllowedPlatformIds = [0] }, Changed);
            else
                library.MarkConfigurationInvalid(" ", Changed);
        });

        library.Name.ShouldBe("Library");
        library.ConfigurationState.ShouldBe(LibraryConfigurationState.Valid);
        library.IsDefault.ShouldBeTrue();
        library.NeedsMaterialization.ShouldBeFalse();
        library.ItemCount.ShouldBe(12);
        library.MaterializationRevision.ShouldBe(revision);
        AssertExact(library.UpdatedAt.ShouldNotBeNull(), Created);
        AssertExact(library.LastMaterializedAt.ShouldNotBeNull(), Created);
    }

    private static void AssertExact(DateTimeOffset actual, DateTimeOffset expected)
    {
        actual.Ticks.ShouldBe(expected.Ticks);
        actual.Offset.ShouldBe(expected.Offset);
    }
}
