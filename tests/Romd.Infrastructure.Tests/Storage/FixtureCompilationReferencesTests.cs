using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class FixtureCompilationReferencesTests
{
    [Fact]
    public void Dispose_AfterCompilation_ReleasesNativeMetadataWithoutWaitingForCollection()
    {
        using var references = new FixtureCompilationReferences([typeof(object).Assembly.Location]);
        var reference = (PortableExecutableReference)references.References.ShouldHaveSingleItem();
        var metadata = reference.GetMetadata().ShouldBeOfType<AssemblyMetadata>();
        var module = metadata.GetModules().ShouldHaveSingleItem();
        module.GetMetadataReader().MetadataLength.ShouldBeGreaterThan(0);

        var compilation = CSharpCompilation.Create("CompletedFixture", references: references.References,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var emitted = new MemoryStream();
        compilation.Emit(emitted).Success.ShouldBeTrue();

        references.Dispose();

        Should.Throw<ObjectDisposedException>(() => module.GetMetadataReader());
        // Using declarations can dispose a second time after an explicit early release.
        references.Dispose();
    }
}
