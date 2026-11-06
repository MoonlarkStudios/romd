using System.Text;
using System.Text.Json;
using Romd.Application.Common.ReferenceCatalog;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.ReferenceData;

public sealed class ReferenceCanonicalJsonTests
{
    [Fact]
    public void SharedVectors_CanonicalBytesAndHashesMatch_AndInvalidValuesFail()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ReferenceCatalogCanonicalization.json")));
        foreach (var vector in fixture.RootElement.GetProperty("vectors").EnumerateArray())
        {
            var input = vector.GetProperty("input");
            Encoding.UTF8.GetString(ReferenceCatalogCanonicalJson.Encode(input)).ShouldBe(vector.GetProperty("canonical").GetString());
            ReferenceCatalogCanonicalJson.Hash(input).ShouldBe(vector.GetProperty("sha256").GetString());
        }
        foreach (var vector in fixture.RootElement.GetProperty("rejections").EnumerateArray())
        {
            // JsonElement may itself reject an escaped lone surrogate when GetString materializes it.
            var failure = Record.Exception(() => ReferenceCatalogCanonicalJson.Hash(vector.GetProperty("input")));
            failure.ShouldNotBeNull();
            (failure is JsonException or InvalidOperationException).ShouldBeTrue();
        }
    }
    [Fact]
    public void DuplicateMembers_AreRejectedRatherThanAmbiguouslyHashed()
    {
        using var json = JsonDocument.Parse("{\"a\":1,\"a\":2}");
        Should.Throw<JsonException>(() => ReferenceCatalogCanonicalJson.Hash(json.RootElement));
    }
}
