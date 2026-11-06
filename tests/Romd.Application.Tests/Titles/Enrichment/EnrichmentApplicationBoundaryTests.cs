using System.Reflection;
using Romd.Admin.Application.Titles.Enrichment;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Titles.Enrichment;

/// <summary>
///     Enrichment policy lives in the admin application layer. Provider transport, downloads, storage and
///     persistence stay behind its ports, and workflows receive every port explicitly.
/// </summary>
public sealed class EnrichmentApplicationBoundaryTests
{
    private static readonly string[] ForbiddenReferencePrefixes =
    [
        "Romd.Infrastructure", "Romd.Persistence", "Romd.Hosting", "Romd.Admin.Host", "Romd.Worker.Host",
        "Romd.Consumer.Host", "Microsoft.EntityFrameworkCore", "Npgsql", "Hangfire", "Microsoft.AspNetCore",
        "Microsoft.Extensions.Http", "System.Net.Http"
    ];

    private static readonly Type[] Workflows =
    [
        typeof(EnrichmentOrchestrator), typeof(EnrichmentContextFactory), typeof(TitleMetadataRematerializer),
        typeof(RematerializationService)
    ];

    [Fact]
    public void AdminApplicationAssembly_DoesNotReferenceAdaptersPersistenceOrTransport()
    {
        var references = typeof(EnrichmentOrchestrator).Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        references.Where(reference => ForbiddenReferencePrefixes.Any(prefix =>
                reference.StartsWith(prefix, StringComparison.Ordinal)))
            .ShouldBeEmpty();
    }

    [Fact]
    public void EnrichmentWorkflows_DependOnlyOnRequiredAbstractions()
    {
        foreach (var workflow in Workflows)
        {
            var constructor = workflow.GetConstructors().ShouldHaveSingleItem($"{workflow.Name} has one composition path.");
            foreach (var parameter in constructor.GetParameters())
            {
                parameter.IsOptional.ShouldBeFalse(
                    $"{workflow.Name}.{parameter.Name} must be composed explicitly, not tolerated as missing.");
                IsAbstraction(parameter.ParameterType).ShouldBeTrue(
                    $"{workflow.Name}.{parameter.Name} depends on concrete {parameter.ParameterType}.");
            }
        }
    }

    [Fact]
    public void EnrichmentOrchestrator_StagesMediaOnlyThroughItsPort()
    {
        var fields = typeof(EnrichmentOrchestrator)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Select(field => field.FieldType)
            .ToArray();

        fields.ShouldContain(typeof(IEnrichmentMediaStager));
        fields.ShouldContain(typeof(IMetadataRematerializer));
        fields.ShouldNotContain(type => type.Namespace != null && type.Namespace.StartsWith("System.Net", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(typeof(TimeProvider), true)]
    [InlineData(typeof(object), false)]
    [InlineData(typeof(List<IMetadataProvider>), false)]
    [InlineData(typeof(IEnumerable<object>), false)]
    [InlineData(typeof(IEnumerable<IMetadataProvider>), true)]
    public void IsAbstraction_ClockAndPortContracts_RejectsConcreteDependencies(Type dependency, bool expected)
    {
        IsAbstraction(dependency).ShouldBe(expected);
    }

    private static bool IsAbstraction(Type type) =>
        (type.IsInterface || type.IsClass && type.IsAbstract) && (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(IEnumerable<>)
                                                 || type.GetGenericArguments()[0].IsInterface);
}
