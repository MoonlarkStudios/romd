using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Romd.Infrastructure.Tests.Storage;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Architecture;

public sealed class DomainTimeArchitectureTests(DomainTimeArchitectureTests.CompilerReferences references)
    : IClassFixture<DomainTimeArchitectureTests.CompilerReferences>
{
    private const string ImplicitUsings = """
        global using System;
        global using System.Collections.Generic;
        global using System.IO;
        global using System.Linq;
        global using System.Net.Http;
        global using System.Threading;
        global using System.Threading.Tasks;
        """;

    [Fact]
    public void DomainSource_BehavioralTime_UsesFactsInsteadOfClocks()
    {
        var root = FindRepositoryRoot();
        var sources = Directory.EnumerateFiles(Path.Combine(root, "src/Romd.Domain"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment is "bin" or "obj"))
            .Order(StringComparer.Ordinal)
            .Select(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: Path.GetRelativePath(root, path)))
            .ToArray();
        sources.ShouldNotBeEmpty();

        // The normal build validates generated reference data. This analysis binds the maintained
        // source together so aliases and user-defined types resolve across files without running generators.
        var compilation = Compile(sources);

        FindClockReferences(compilation).ShouldBeEmpty(
            "Pass timestamp facts into Domain; clock services and ambient time belong in orchestration.");
    }

    [Theory]
    [InlineData("System.DateTime.Now", "System.DateTime.Now")]
    [InlineData("System.DateTime.UtcNow", "System.DateTime.UtcNow")]
    [InlineData("System.DateTime.Today", "System.DateTime.Today")]
    [InlineData("System.DateTimeOffset.Now", "System.DateTimeOffset.Now")]
    [InlineData("global::System.DateTimeOffset.UtcNow", "System.DateTimeOffset.UtcNow")]
    public void FindClockReferences_AmbientFrameworkProperty_ReportsItsSymbol(string expression, string expected)
    {
        var compilation = CompileSynthetic($"public sealed class Policy {{ public object At => {expression}; }}");

        FindClockReferences(compilation).ShouldHaveSingleItem().Member.ShouldBe(expected);
    }

    [Theory]
    [InlineData("using Clock = System.DateTimeOffset;", "Clock.UtcNow", "System.DateTimeOffset.UtcNow")]
    [InlineData("using static System.DateTime;", "Today", "System.DateTime.Today")]
    [InlineData("global using static System.DateTimeOffset;", "Now", "System.DateTimeOffset.Now")]
    public void FindClockReferences_ImportedClockProperty_ResolvesAliasesAndStaticImports(string import, string expression, string expected)
    {
        var compilation = CompileSynthetic($"{import}\npublic sealed class Policy {{ public object At => {expression}; }}");

        FindClockReferences(compilation).ShouldHaveSingleItem().Member.ShouldBe(expected);
    }

    [Theory]
    [InlineData("public sealed class Policy(System.TimeProvider clock);", 1)]
    [InlineData("public sealed class Policy { private System.TimeProvider _clock = null!; }", 1)]
    [InlineData("using Clock = System.TimeProvider; public sealed class Policy(Clock clock);", 2)]
    [InlineData("using static System.TimeProvider; public sealed class Policy;", 1)]
    [InlineData("public sealed class Policy { public object Clock() { var clock = System.TimeProvider.System; return clock; } }", 2)]
    [InlineData("class Clock : System.TimeProvider {} sealed class Policy(Clock clock);", 2)]
    [InlineData("public sealed class Policy<T>(T clock) where T : System.TimeProvider;", 3)]
    [InlineData("public sealed class Policy(System.TimeProvider[] clocks);", 1)]
    [InlineData("using Clocks = System.Collections.Generic.List<System.TimeProvider>; public sealed class Policy(Clocks clocks);", 3)]
    public void FindClockReferences_ClockServiceType_RejectsInjectionRetentionAliasesAndDerivedTypes(string source, int count)
    {
        var compilation = CompileSynthetic(source);

        var observed = FindClockReferences(compilation);

        observed.Count.ShouldBe(count);
        observed.ShouldAllBe(reference => reference.Member == "System.TimeProvider");
    }

    [Fact]
    public void FindClockReferences_GlobalAliasInAnotherFile_ResolvesTheReferencedProperty()
    {
        var compilation = CompileSynthetic("global using Clock = System.DateTimeOffset;",
            "public sealed class Policy { public object At => Clock.UtcNow; }");

        var observed = FindClockReferences(compilation).ShouldHaveSingleItem();

        observed.Path.ShouldBe("probe-1.cs");
        observed.Member.ShouldBe("System.DateTimeOffset.UtcNow");
    }

    [Fact]
    public void FindClockReferences_ExplicitFactsCommentsLiteralsAndUnrelatedTypes_AreAllowed()
    {
        var compilation = CompileSynthetic("""
            namespace Example;
            public sealed class TimeProvider;
            public static class DateTime
            {
                public static int Now => 1;
                public static int UtcNow => 2;
                public static int Today => 3;
            }
            public static class DateTimeOffset { public static int UtcNow => 4; }
            """, """
            namespace Example;
            public sealed class Policy(TimeProvider provider)
            {
                // System.DateTime.UtcNow and System.TimeProvider are text, not dependencies.
                public string Label => "System.DateTimeOffset.Now; TimeProvider.System";
                public string Name => nameof(global::System.DateTime.UtcNow);
                public int Local => DateTime.Now + DateTime.UtcNow + DateTime.Today + DateTimeOffset.UtcNow;
                public bool Before(global::System.DateTimeOffset at, global::System.DateTimeOffset deadline) => at < deadline;
            }
            """);

        FindClockReferences(compilation).ShouldBeEmpty();
    }

    private CSharpCompilation Compile(IEnumerable<SyntaxTree> sources) => CSharpCompilation.Create("DomainTimeGuard",
        sources.Prepend(CSharpSyntaxTree.ParseText(ImplicitUsings, path: "implicit-usings.cs")), references.References,
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

    private CSharpCompilation CompileSynthetic(params string[] sources)
    {
        var compilation = Compile(sources.Select((source, index) => CSharpSyntaxTree.ParseText(source, path: $"probe-{index}.cs")));
        compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ShouldBeEmpty();
        return compilation;
    }

    private static IReadOnlyList<ClockReference> FindClockReferences(CSharpCompilation compilation)
    {
        var framework = compilation.GetSpecialType(SpecialType.System_Object).ContainingAssembly;
        var dateTime = framework.GetTypeByMetadataName("System.DateTime").ShouldNotBeNull();
        var dateTimeOffset = framework.GetTypeByMetadataName("System.DateTimeOffset").ShouldNotBeNull();
        var timeProvider = framework.GetTypeByMetadataName("System.TimeProvider").ShouldNotBeNull();
        var observed = new List<ClockReference>();
        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var name in tree.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>())
            {
                var symbol = model.GetSymbolInfo(name).Symbol;
                var type = symbol as ITypeSymbol;
                if (type is null && name.Identifier.ValueText == "var")
                    type = model.GetTypeInfo(name).Type;
                string? member = null;
                if (type is not null && ContainsClockService(type, timeProvider, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default)))
                    member = "System.TimeProvider";
                else if (symbol is IPropertySymbol { IsStatic: true } property &&
                    property.Name is "Now" or "UtcNow" or "Today" &&
                    (SymbolEqualityComparer.Default.Equals(property.ContainingType, dateTime) ||
                     SymbolEqualityComparer.Default.Equals(property.ContainingType, dateTimeOffset)) &&
                    !name.Ancestors().OfType<InvocationExpressionSyntax>().Any(invocation => model.GetOperation(invocation) is INameOfOperation))
                    member = $"{property.ContainingType.ToDisplayString()}.{property.Name}";
                if (member is null)
                    continue;
                var position = tree.GetLineSpan(name.Span).StartLinePosition;
                observed.Add(new(tree.FilePath.Replace('\\', '/'), position.Line + 1, position.Character + 1, member));
            }
        }
        return observed;
    }

    private static bool ContainsClockService(ITypeSymbol type, INamedTypeSymbol timeProvider, HashSet<ITypeSymbol> visited)
    {
        if (!visited.Add(type))
            return false;
        return SymbolEqualityComparer.Default.Equals(type, timeProvider) || type switch
        {
            IArrayTypeSymbol array => ContainsClockService(array.ElementType, timeProvider, visited),
            ITypeParameterSymbol parameter => parameter.ConstraintTypes.Any(constraint => ContainsClockService(constraint, timeProvider, visited)),
            INamedTypeSymbol named => named.TypeArguments.Any(argument => ContainsClockService(argument, timeProvider, visited)) ||
                named.BaseType is { } parent && ContainsClockService(parent, timeProvider, visited),
            _ => false
        };
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Romd.sln")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Could not find repository root.");
    }

    private sealed record ClockReference(string Path, int Line, int Column, string Member)
    {
        public override string ToString() => $"{Path}:{Line}:{Column}: {Member}";
    }

    public sealed class CompilerReferences : IDisposable
    {
        private readonly FixtureCompilationReferences _owned;
        internal ImmutableArray<MetadataReference> References => _owned.References;

        public CompilerReferences()
        {
            var frameworkDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location);
            var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Where(path => Path.GetDirectoryName(path) == frameworkDirectory);
            _owned = new FixtureCompilationReferences(paths);
        }

        public void Dispose() => _owned.Dispose();
    }
}
