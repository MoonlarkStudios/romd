using Shouldly;
using Xunit;

namespace Romd.Dat.Parsing.Tests;

public sealed class ArchitectureTests
{
    [Fact]
    public void AssemblyReferences_DoNotDependOnRomdApplicationDomainInfrastructureOrHosting()
    {
        string[] references = typeof(DatParser).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        references.ShouldNotContain(reference =>
            reference.StartsWith("Romd.", StringComparison.Ordinal));
    }

    [Fact]
    public void NeutralModels_DoNotExposePersistenceIdentifiers()
    {
        var modelTypes = typeof(DatParser).Assembly.ExportedTypes
            .Where(type => type.Namespace == "Romd.Dat.Parsing.Models")
            .ToArray();

        string[] prohibitedPropertyNames = ["Id", "DatFileId", "DatGameId"];
        modelTypes.ShouldNotContain(type => type.GetProperties().Any(property =>
            prohibitedPropertyNames.Contains(property.Name, StringComparer.Ordinal)));
    }
}
