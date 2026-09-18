using System.Text.Json;
using Romd.Domain.Catalog.Ratings;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Catalog;

public sealed class PresentationCatalogCompatibilityTests
{
    [Fact]
    public void PresentationCatalog_Ratings_UseCanonicalDomainCodesAndBoardOrder()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "reference-data", "ratings.json")));
        var catalog = json.RootElement;
        foreach (var entry in catalog.GetProperty("ratings").EnumerateArray())
        {
            var board = Enum.Parse<RatingBoard>(entry.GetProperty("board").GetString()!);
            var code = entry.GetProperty("code").GetString()!;
            var category = RatingBoardCatalog.TryResolve(board, code);
            category.ShouldNotBeNull($"Unknown presentation rating: {board}:{code}");
            category.Code.ShouldBe(code, "Presentation must not introduce another normalization table");
        }
    }
}
