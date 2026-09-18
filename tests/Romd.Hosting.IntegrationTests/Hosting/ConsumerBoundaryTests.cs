using System.Reflection;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Xml.Linq;
using Romd.Consumer.Application.Access;
using Romd.Consumer.Application.Activity;
using Romd.Consumer.Application.Browse;
using Romd.Consumer.Application.Collections;
using Romd.Consumer.Application.Delivery;
using Romd.Consumer.Application.Libraries;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Hosting;

public sealed class ConsumerBoundaryTests
{
    private static readonly IReadOnlyDictionary<string, string[]> ExpectedProjectReferences =
        new Dictionary<string, string[]>
        {
            ["src/Romd.Admin.Host/Romd.Admin.Host.csproj"] =
            [
                "Romd.Hosting"
            ],
            ["src/Romd.Application.Common/Romd.Application.Common.csproj"] =
            [
                "Romd.Contracts.Common",
                "Romd.Domain"
            ],
            ["src/Romd.Admin.Application/Romd.Admin.Application.csproj"] =
            [
                "Romd.Application.Common",
                "Romd.Contracts.Common",
                "Romd.Contracts.Management",
                "Romd.Domain",
                "Romd.Storage"
            ],
            ["src/Romd.Consumer.Host/Romd.Consumer.Host.csproj"] =
            [
                "Romd.Hosting"
            ],
            ["src/Romd.Consumer.Application/Romd.Consumer.Application.csproj"] =
            [
                "Romd.Application.Common",
                "Romd.Contracts.Common",
                "Romd.Contracts.Consumer",
                "Romd.Domain"
            ],
            ["src/Romd.Contracts.Common/Romd.Contracts.Common.csproj"] = [],
            ["src/Romd.Contracts.Consumer/Romd.Contracts.Consumer.csproj"] =
            [
                "Romd.Contracts.Common"
            ],
            ["src/Romd.Contracts.Management/Romd.Contracts.Management.csproj"] =
            [
                "Romd.Contracts.Common"
            ],
            ["src/Romd.Domain/Romd.Domain.csproj"] = ["Romd.ReferenceData.Generators"],
            ["src/Romd.Worker.Host/Romd.Worker.Host.csproj"] =
            [
                "Romd.Hosting"
            ],
            ["src/Romd.Hosting/Romd.Hosting.csproj"] =
            [
                "Romd.Admin.Application",
                "Romd.Application.Common",
                "Romd.Consumer.Application",
                "Romd.Contracts.Common",
                "Romd.Contracts.Consumer",
                "Romd.Contracts.Management",
                "Romd.Infrastructure"
            ],
            ["src/Romd.Infrastructure/Romd.Infrastructure.csproj"] =
            [
                "Romd.Admin.Application",
                "Romd.Application.Common",
                "Romd.Consumer.Application",
                "Romd.Dat.Parsing",
                "Romd.Persistence",
                "Romd.Storage"
            ],
            ["src/Romd.Persistence/Romd.Persistence.csproj"] =
            [
                "Romd.Admin.Application",
                "Romd.Application.Common",
                "Romd.Consumer.Application"
            ],
            ["src/Romd.Storage/Romd.Storage.csproj"] =
            [
                "Romd.Domain"
            ]
        };

    private static readonly string[] ConsumerSourceRoots =
    [
        "src/Romd.Consumer.Application",
        "src/Romd.Consumer.Host"
    ];

    private static readonly string[] ConsumerHostFiles =
    [
        "src/Romd.Hosting/Endpoints/ArtworkDeliveryEndpoints.cs",
        "src/Romd.Hosting/Hosting/RomdConsumerEndpointRouteExtensions.cs",
        "src/Romd.Hosting/Endpoints/ConsumerAccessEndpoints.cs",
        "src/Romd.Hosting/Endpoints/ConsumerAccountEndpoints.cs",
        "src/Romd.Hosting/Endpoints/ConsumerPlayActivityEndpoints.cs",
        "src/Romd.Hosting/Endpoints/ConsumerBrowseEndpoints.cs",
        "src/Romd.Hosting/Endpoints/ConsumerCollectionEndpoints.cs",
        "src/Romd.Hosting/Endpoints/ConsumerDeliveryEndpoints.cs",
        "src/Romd.Hosting/Endpoints/ConsumerIdentityEndpoints.cs",
        "src/Romd.Hosting/Endpoints/ConsumerPlaybackEndpoints.cs",
        "src/Romd.Hosting/Endpoints/ConsumerServerEndpoints.cs",
        "src/Romd.Hosting/Endpoints/ConsumerStorageEndpoints.cs"
    ];

    private static readonly string[] ConsumerRouteMappingFiles =
    [
        "src/Romd.Consumer.Host/Program.cs",
        "src/Romd.Hosting/Hosting/RomdConsumerEndpointRouteExtensions.cs"
    ];

    private static readonly string[] ConsumerContentLaneFiles =
    [
        "src/Romd.Hosting/Endpoints/ArtworkDeliveryEndpoints.cs",
        "src/Romd.Infrastructure/Storage/ArtworkDeliveryService.cs",
        "src/Romd.Hosting/Endpoints/ConsumerStorageEndpoints.cs",
        "src/Romd.Infrastructure/Storage/ConsumerContentArtifactResolver.cs"
    ];

    private static readonly string[] ForbiddenConsumerNamespacePrefixes =
    [
        "Microsoft.AspNetCore.Identity",
        "Romd.Contracts.Management",
        "Romd.Admin.Application",
        "Romd.Infrastructure.Catalog",
        "Romd.Infrastructure.Dats",
        "Romd.Infrastructure.Enrichment",
        "Romd.Infrastructure.Identity",
        "Romd.Infrastructure.Jobs",
        "Romd.Infrastructure.Libraries",
        "Romd.Persistence",
        "Romd.Infrastructure.Upload"
    ];

    private static readonly string[] ForbiddenConsumerReferenceTokens =
    [
        "{fileId}",
        "RomdDbContext",
        "UserManager<",
        "SignInManager<",
        "RoleManager<",
        "IFileStorageService",
        "IFileRepository",
        "IFileContentReadService",
        "IFileContentReadRepository",
        "IUnitOfWork",
        "IUserAdministration"
    ];

    private static readonly string[] ForbiddenConsumerRawStorageRouteTokens =
    [
        "{fileId}",
        "{storageId}",
        "{storageKey}",
        "{sha256}",
        "{hash}",
        "{casKey}",
        "{blobId}"
    ];

    private static readonly string[] ForbiddenConsumerContentLaneReferenceTokens =
    [
        "RomdDbContext",
        "UserManager<",
        "SignInManager<",
        "RoleManager<",
        "IFileStorageService",
        "IFileRepository",
        "IFileContentReadService",
        "IFileContentReadRepository",
        "IUnitOfWork",
        "IUserAdministration",
        "Romd.Contracts.Management",
        "Romd.Admin.Application",
        "Hangfire",
        "IExport",
        "IJobExecutor",
        "UploadJob",
        "ReplaceDatJob",
        "EnrichmentJob",
        "MaterializationJob",
        "ExportScheduler",
        "UploadJobCreator",
        "ReplaceDatJobCreator",
        "LibraryMaterializationReconciler"
    ];

    private static readonly string[] ForbiddenCommonReferenceTokens =
    [
        "IUnitOfWork",
        "ITransaction",
        "IFileContentReadService",
        "IFileContentReadRepository"
    ];

    [Fact]
    public void ProjectReferences_MatchDocumentedPhaseOnePointSevenBoundary()
    {
        var actualReferences = ExpectedProjectReferences.Keys
            .ToDictionary(
                projectPath => projectPath,
                projectPath => GetProjectReferences(projectPath),
                StringComparer.Ordinal);

        actualReferences
            .Keys
            .Order(StringComparer.Ordinal)
            .ShouldBe(ExpectedProjectReferences.Keys.Order(StringComparer.Ordinal));
        foreach ((string projectPath, string[] expectedReferences) in ExpectedProjectReferences)
        {
            actualReferences[projectPath].ShouldBe(expectedReferences);
        }
    }

    [Fact]
    public void ReferenceGenerator_IsCompilationOnly_NotARuntimeDomainDependency()
    {
        var project = XDocument.Load(FindRepositoryFile("src/Romd.Domain/Romd.Domain.csproj"));
        var analyzer = project.Descendants("ProjectReference").Single();
        analyzer.Attribute("OutputItemType")?.Value.ShouldBe("Analyzer");
        analyzer.Attribute("ReferenceOutputAssembly")?.Value.ShouldBe("false");
    }

    [Fact]
    public void DeployableHostAndApiGenerationTopology_HasExactlyThreeHostsAndNoLegacyPath()
    {
        string sourceRoot = FindRepositoryDirectory("src");
        string[] hostProjects = Directory
            .EnumerateDirectories(sourceRoot, "*.Host", SearchOption.TopDirectoryOnly)
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly))
            .Select(ToRepositoryRelativePath)
            .Order(StringComparer.Ordinal)
            .ToArray();

        hostProjects.ShouldBe(
        [
            "src/Romd.Admin.Host/Romd.Admin.Host.csproj",
            "src/Romd.Consumer.Host/Romd.Consumer.Host.csproj",
            "src/Romd.Worker.Host/Romd.Worker.Host.csproj"
        ]);

        using var packageDocument = JsonDocument.Parse(File.ReadAllText(FindRepositoryFile("web/package.json")));
        string[] scripts = packageDocument.RootElement
            .GetProperty("scripts")
            .EnumerateObject()
            .Select(script => script.Name)
            .ToArray();

        scripts.ShouldNotContain("api:update:legacy");
    }

    [Fact]
    public void ConsumerApplicationAndHostCode_DoesNotReferenceManagementOrWorkerNamespaces()
    {
        var violations = GetConsumerSourceFiles()
            .SelectMany(file => ForbiddenConsumerNamespacePrefixes
                .Where(prefix => ContainsNamespaceReference(file, prefix))
                .Select(prefix => $"{ToRepositoryRelativePath(file)} references {prefix}"))
            .Order(StringComparer.Ordinal)
            .ToList();

        violations.ShouldBeEmpty();
    }

    [Fact]
    public void ConsumerApplicationAndHostCode_DoesNotInjectBroadPersistenceIdentityOrStoragePrimitives()
    {
        var violations = GetConsumerSourceFiles()
            .SelectMany(file => ForbiddenConsumerReferenceTokens
                .Where(token => File.ReadAllText(file).Contains(token, StringComparison.Ordinal))
                .Select(token => $"{ToRepositoryRelativePath(file)} references {token}"))
            .Order(StringComparer.Ordinal)
            .ToList();

        violations.ShouldBeEmpty();
    }

    [Fact]
    public void ConsumerContentDeliveryLane_DoesNotReferenceBroadStoragePersistenceIdentityAdminOrWorkerPrimitives()
    {
        var violations = ConsumerContentLaneFiles
            .Select(FindRepositoryFile)
            .SelectMany(file => ForbiddenConsumerContentLaneReferenceTokens
                .Where(token => File.ReadAllText(file).Contains(token, StringComparison.Ordinal))
                .Select(token => $"{ToRepositoryRelativePath(file)} references {token}"))
            .Order(StringComparer.Ordinal)
            .ToList();

        violations.ShouldBeEmpty();
    }

    [Fact]
    public void ConsumerRoutes_DoNotExposeRawStorageIdentifiers()
    {
        var violations = ConsumerHostFiles
            .Select(FindRepositoryFile)
            .SelectMany(file => ForbiddenConsumerRawStorageRouteTokens
                .Where(token => File.ReadAllText(file).Contains(token, StringComparison.OrdinalIgnoreCase))
                .Select(token => $"{ToRepositoryRelativePath(file)} exposes {token}"))
            .Order(StringComparer.Ordinal)
            .ToList();

        violations.ShouldBeEmpty();
    }

    [Fact]
    public void ConsumerHostFileGuardrail_CoversEveryMappedConsumerEndpointFile()
    {
        var coveredEndpointFiles = ConsumerHostFiles
            .Where(file => file.StartsWith("src/Romd.Hosting/Endpoints/", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        coveredEndpointFiles.ShouldBe(GetMappedConsumerEndpointFiles());
    }

    [Fact]
    public void ConsumerHostTestStartupGate_RemainsAvailableInReleaseBuilds()
    {
        string program = File.ReadAllText(FindRepositoryFile("src/Romd.Consumer.Host/Program.cs"));
        string helper = File.ReadAllText(FindRepositoryFile(
            "src/Romd.Consumer.Host/Testing/ConsumerHostTestStartupGate.cs"));

        program.ShouldContain("using Romd.Consumer.Host.Testing;");
        program.ShouldContain("await ConsumerHostTestStartupGate.WaitIfRequestedAsync(builder.Environment);");
        helper.ShouldNotContain("#if DEBUG");
        helper.ShouldContain("ROMD_CONSUMER_TEST_STARTUP_GATE");
        helper.ShouldContain("ROMD_CONSUMER_TEST_STARTUP_READY");
        helper.ShouldContain("environment.IsEnvironment(\"Testing\")");
    }

    [Fact]
    public void ConsumerLibraryScopedApplicationBoundary_DoesNotReadTokenLibraryOrAcceptRawLibraryAuthority()
    {
        var tokenLibraryReads = GetConsumerSourceFiles()
            .Where(file => File.ReadAllText(file).Contains("currentUser.LibraryId", StringComparison.Ordinal))
            .Select(ToRepositoryRelativePath)
            .Order(StringComparer.Ordinal)
            .ToList();
        tokenLibraryReads.ShouldBeEmpty();

        Type[] repositoryTypes =
        [
            typeof(IConsumerLibraryContextRepository),
            typeof(IConsumerReleaseAccessRepository),
            typeof(IConsumerBrowseRepository),
            typeof(IConsumerCollectionReadRepository),
            typeof(IConsumerBiosRepository),
            typeof(IConsumerReleaseManifestRepository),
            typeof(IPlayActivityRepository)
        ];
        var rawLibraryAuthorityParameters = repositoryTypes
            .SelectMany(type => type.GetMethods().SelectMany(method => method.GetParameters()
                .Where(parameter =>
                    parameter.ParameterType == typeof(int) &&
                    parameter.Name?.Contains("library", StringComparison.OrdinalIgnoreCase) == true)
                .Select(parameter => $"{type.Name}.{method.Name}({parameter.Name})")))
            .Order(StringComparer.Ordinal)
            .ToList();

        rawLibraryAuthorityParameters.ShouldBeEmpty();
    }

    [Fact]
    public void ConsumerLibraryRepositoryBoundary_UsesOnlyExplicitNonNullResultHierarchies()
    {
        Type[] repositoryTypes =
        [
            typeof(IConsumerLibraryContextRepository),
            typeof(IConsumerReleaseAccessRepository),
            typeof(IConsumerBrowseRepository),
            typeof(IConsumerCollectionReadRepository),
            typeof(IConsumerBiosRepository),
            typeof(IConsumerReleaseManifestRepository)
        ];
        var invalidReturns = repositoryTypes
            .SelectMany(type => type.GetMethods().Select(method => (Type: type, Method: method)))
            .Where(item =>
            {
                Type returnType = item.Method.ReturnType;
                return !returnType.IsGenericType ||
                       returnType.GetGenericTypeDefinition() != typeof(Task<>) ||
                       !returnType.GenericTypeArguments[0].IsGenericType ||
                       returnType.GenericTypeArguments[0].GetGenericTypeDefinition() !=
                       typeof(ConsumerLibraryReadResult<>);
            })
            .Select(item => $"{item.Type.Name}.{item.Method.Name}: {item.Method.ReturnType}")
            .Order(StringComparer.Ordinal)
            .ToList();
        invalidReturns.ShouldBeEmpty();

        AssertClosedResultHierarchy(
            typeof(ConsumerLibraryReadResult<>),
            [
                typeof(ConsumerLibraryReadResult<>.Found),
                typeof(ConsumerLibraryReadResult<>.ItemNotFound),
                typeof(ConsumerLibraryReadResult<>.LibraryUnavailable),
                typeof(ConsumerLibraryReadResult<>.ProjectionInconsistent)
            ]);
        AssertClosedResultHierarchy(
            typeof(ConsumerLibraryProjectionResult<>),
            [
                typeof(ConsumerLibraryProjectionResult<>.Found),
                typeof(ConsumerLibraryProjectionResult<>.ItemNotFound),
                typeof(ConsumerLibraryProjectionResult<>.ProjectionInconsistent)
            ]);

        string[] resultSources =
        [
            "src/Romd.Consumer.Application/Libraries/ConsumerLibraryScope.cs",
            "src/Romd.Persistence/Queries/LiveConsumerLibraryQuery.cs"
        ];
        resultSources.Select(FindRepositoryFile)
            .Select(File.ReadAllText)
            .Sum(source => Regex.Matches(source, @"where T : notnull").Count)
            .ShouldBe(3);

        string[] relevantInfrastructureFiles =
        [
            "src/Romd.Persistence/Queries/LiveConsumerLibraryQuery.cs",
            "src/Romd.Persistence/Repositories/LibraryRepository.cs",
            "src/Romd.Persistence/Repositories/ConsumerBrowseRepository.cs",
            "src/Romd.Persistence/Repositories/CollectionRepository.cs",
            "src/Romd.Persistence/Repositories/ConsumerBiosRepository.cs",
            "src/Romd.Persistence/Repositories/ConsumerReleaseManifestRepository.cs",
            "src/Romd.Persistence/Repositories/ConsumerReleaseAccessRepository.cs"
        ];
        var forbiddenPatterns = new[]
        {
            @"ConsumerLibraryRead\s*<",
            @"ConsumerLibraryReadResult\s*<[^>]*\?>",
            @"read\?\.Value",
            @"\{\s*Value:\s*null\s*\}",
            @"Value:\s*null"
        };
        var violations = GetConsumerSourceFiles()
            .Concat(relevantInfrastructureFiles.Select(FindRepositoryFile))
            .Distinct(StringComparer.Ordinal)
            .SelectMany(file => forbiddenPatterns
                .Where(pattern => Regex.IsMatch(File.ReadAllText(file), pattern, RegexOptions.CultureInvariant))
                .Select(pattern => $"{ToRepositoryRelativePath(file)} matches {pattern}"))
            .Order(StringComparer.Ordinal)
            .ToList();
        violations.ShouldBeEmpty();

        typeof(ConsumerReleaseManifest).GetProperty("LibraryId").ShouldBeNull();
    }

    private static void AssertClosedResultHierarchy(Type baseType, Type[] expectedConcreteVariants)
    {
        MethodInfo? seal = baseType.GetMethod(
            "SealConcreteVariants",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        seal.ShouldNotBeNull();
        seal.IsAbstract.ShouldBeTrue();
        seal.IsFamilyAndAssembly.ShouldBeTrue();

        var actualConcreteVariants = baseType.Assembly.GetTypes()
            .Where(type => !type.IsAbstract && IsDerivedFromOpenGeneric(type, baseType))
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();
        actualConcreteVariants.ShouldBe(expectedConcreteVariants
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray());

        foreach (Type variant in actualConcreteVariants)
        {
            variant.IsSealed.ShouldBeTrue();
            variant.DeclaringType.ShouldBe(baseType);
        }
    }

    private static bool IsDerivedFromOpenGeneric(Type type, Type openGenericBase)
    {
        for (Type? current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == openGenericBase)
            {
                return true;
            }
        }

        return false;
    }

    [Fact]
    public void CommonApplication_DoesNotContainAdminOrConsumerDeliveryPrimitives()
    {
        var violations = Directory
            .GetFiles(FindRepositoryDirectory("src/Romd.Application.Common"), "*.cs", SearchOption.AllDirectories)
            .Where(IsRepositorySourceFile)
            .SelectMany(file => ForbiddenCommonReferenceTokens
                .Where(token => File.ReadAllText(file).Contains(token, StringComparison.Ordinal))
                .Select(token => $"{ToRepositoryRelativePath(file)} references {token}"))
            .Order(StringComparer.Ordinal)
            .ToList();

        violations.ShouldBeEmpty();
    }

    private static string[] GetProjectReferences(string projectPath)
    {
        XDocument project = XDocument.Load(FindRepositoryFile(projectPath));

        return project
            .Descendants("ProjectReference")
            .Select(reference => reference.Attribute("Include")?.Value)
            .Select(reference => GetRequiredReference(reference, projectPath))
            .Select(GetProjectName)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<string> GetConsumerSourceFiles()
    {
        var rootFiles = ConsumerSourceRoots
            .Select(FindRepositoryDirectory)
            .SelectMany(directory => Directory.GetFiles(directory, "*.cs", SearchOption.AllDirectories))
            .Where(IsRepositorySourceFile);
        var hostFiles = ConsumerHostFiles.Select(FindRepositoryFile);

        return rootFiles
            .Concat(hostFiles)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private static string[] GetMappedConsumerEndpointFiles() =>
        ConsumerRouteMappingFiles
            .Select(FindRepositoryFile)
            .Select(File.ReadAllText)
            .SelectMany(content => Regex.Matches(content, @"\bMap(?:Consumer|Artwork)[A-Za-z0-9]+Endpoints\s*\(")
                .Select(match => match.Value.TrimEnd('(', ' ')))
            .Distinct(StringComparer.Ordinal)
            .Select(methodName => $"src/Romd.Hosting/Endpoints/{methodName["Map".Length..]}.cs")
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static bool ContainsNamespaceReference(string file, string namespacePrefix)
    {
        string content = File.ReadAllText(file);
        string[] referenceTokens =
        [
            $"using {namespacePrefix};",
            $"using {namespacePrefix}.",
            $"= {namespacePrefix}.",
            $"typeof({namespacePrefix}.",
            $"new {namespacePrefix}.",
            $"{namespacePrefix}."
        ];

        return referenceTokens.Any(token => content.Contains(token, StringComparison.Ordinal));
    }

    private static string GetRequiredReference(string? reference, string projectPath)
    {
        reference.ShouldNotBeNull($"Project reference in {projectPath} is missing an Include attribute.");

        return reference;
    }

    private static string GetProjectName(string reference) =>
        Path.GetFileNameWithoutExtension(reference.Replace('\\', '/'));

    private static string FindRepositoryFile(string relativePath)
    {
        string path = Path.Combine(FindRepositoryRoot(), relativePath);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Could not find repository file '{relativePath}'.");
        }

        return path;
    }

    private static string FindRepositoryDirectory(string relativePath)
    {
        string path = Path.Combine(FindRepositoryRoot(), relativePath);

        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"Could not find repository directory '{relativePath}'.");
        }

        return path;
    }

    private static string ToRepositoryRelativePath(string path) =>
        Path.GetRelativePath(FindRepositoryRoot(), path).Replace('\\', '/');

    private static bool IsRepositorySourceFile(string path)
    {
        string relativePath = ToRepositoryRelativePath(path);

        return !relativePath.Contains("/bin/", StringComparison.Ordinal)
               && !relativePath.Contains("/obj/", StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Romd.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find repository root.");
    }
}
