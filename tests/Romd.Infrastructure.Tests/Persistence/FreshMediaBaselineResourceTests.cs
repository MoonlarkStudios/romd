using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Romd.Persistence.Migrations;
using Romd.Persistence.MediaFacts;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

/// <summary>The released fresh baseline must keep executing the exact DDL it originally installed.</summary>
public sealed class FreshMediaBaselineResourceTests
{
    private static readonly (string Name, string Digest)[] Pins =
    [
        ("Schema", "718a910763c0bb590f28f9a0df2a0bb5539f0f8b33eab4430a757488bb2a5bfd"),
        ("ContainerFacts", "1f87141bd658a391ddda52d61ae74b9370ff8c51c4a81276f0ea09dccbf4a259"),
        ("InspectionFacts", "d226571adb84be5bb759157a75a2fc60abd35dded51debd733886771e5009095"),
        ("CreationFacts", "3faa6c1c42cc7d0ed411dbac501595c6e67ac79e1cb2a941b2f1d075840368a6"),
        ("CdObservationFacts", "d62a4787f7a7e53e326e4c5480b18c9919fba01d7440e0fb78eca04fe005a835"),
        ("OwnershipFacts", "bfe1666bd295af63184d0877ccafd9e3964d4bc5952b1a39c03394eae1d47a8c"),
        ("CatalogCdLayoutFacts", "3c1478d6c5e7013c61efdff83b7864603fb7f20feab057a62a4647b2e3da5a93"),
        ("SourcePinFacts", "fd19cfe802f9036fb985a0e5b417cd8021a29688f7e62536f9b75a9ca5e8b490"),
        ("ReconciliationSchema", "b715996b260c9af6d4f3b6c230d94e8bf68d0f5804e76711dd358a9b2097f454"),
        ("ReconciliationGuards", "38c16de3f22c4c4b79e8612ec739cd556aca3bfd990e23836bf6b3a467fd9389"),
        ("BaselineSupport", "af78acbd70e6f3c3e622fd370691900126b12625142adb72aa025a4396af6ae2"),
    ];

    public static IEnumerable<object[]> Resources() => Pins.Select(pin => new object[] { pin.Name, pin.Digest });

    [Fact]
    public void BaselineSql_ExecutedResourceInventory_HasNoMissingOrStalePins()
    {
        var sql = new FreshMediaBaseline().UpOperations.OfType<SqlOperation>().Select(operation => operation.Sql).ToArray();
        var assembly = typeof(MediaFactSchema).Assembly;
        const string prefix = "Romd.MediaFacts.";
        var used = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(prefix, StringComparison.Ordinal) && name.EndsWith(".sql", StringComparison.Ordinal))
            .Where(name =>
            {
                using var stream = assembly.GetManifestResourceStream(name)!;
                using var reader = new StreamReader(stream);
                string resource = reader.ReadToEnd();
                return sql.Any(statement => statement.Contains(resource, StringComparison.Ordinal));
            })
            .Select(name => name[prefix.Length..^4]).Order(StringComparer.Ordinal).ToArray();
        used.ShouldBe(Pins.Select(pin => pin.Name).Order(StringComparer.Ordinal).ToArray(),
            "Pin every resource the historical baseline executes. New migrations may add their own resources without changing this baseline.");
        used.ShouldNotContain("RuntimeFactGrants");
    }

    [Theory]
    [MemberData(nameof(Resources))]
    public void BaselineSql_EmbeddedResource_RemainsImmutable(string resource, string expectedSha256)
    {
        using var stream = typeof(MediaFactSchema).Assembly.GetManifestResourceStream($"Romd.MediaFacts.{resource}.sql");
        stream.ShouldNotBeNull();
        Convert.ToHexStringLower(SHA256.HashData(stream)).ShouldBe(expectedSha256,
            "20261031050000_FreshMediaBaseline executes these bytes. Schema changes require a new migration; do not refresh the baseline hashes.");
    }
}
