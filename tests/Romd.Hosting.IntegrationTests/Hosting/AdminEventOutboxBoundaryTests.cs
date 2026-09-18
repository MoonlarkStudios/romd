using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Romd.Admin.Application.Common.Realtime;
using Romd.Admin.Application.Dashboard;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Contracts.Management.Realtime;
using Romd.Host.Endpoints;
using Romd.Host.Hubs;
using Romd.Hosting;
using Romd.Hosting.IntegrationTests.RealtimeGuardFixtures;
using Romd.Hosting.Realtime;
using Romd.Infrastructure.Realtime;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Hosting;

/// <summary>
///     Migration guardrails for #85. Detached notifications are retired from
///     the migrated files, and Hosting owns relay helpers rather than application
///     notifier ports. Hosting has no remaining application-outbox references.
/// </summary>
public sealed class AdminEventOutboxBoundaryTests
{
    private static readonly Regex DetachedNotificationPattern = new(
        @"(?:\bTask\s*\.\s*Run\s*\(|_\s*=\s*[^;\r\n]*\b[A-Za-z_]\w*Async\s*\()",
        RegexOptions.CultureInvariant);

    private static readonly Regex AdminEventOutboxReferencePattern = new(
        @"\bIAdminEventOutbox\b",
        RegexOptions.CultureInvariant);

    private static readonly Regex InfrastructureOutboxReferencePattern = new(
        @"\bIAdminRealtimeOutbox\b",
        RegexOptions.CultureInvariant);

    private static readonly Regex RetiredNotifierReferencePattern = new(
        @"\b(?:IStatsNotifier|IJobNotifier)\b",
        RegexOptions.CultureInvariant);

    private static readonly IReadOnlyDictionary<string, string> AllowedDetachedNotificationSites =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private static readonly IReadOnlyList<string> RetiredNotificationFiles =
    [
        "src/Romd.Admin.Application/Source/Dat/Commands/DeleteDat/DeleteDat.cs",
        "src/Romd.Admin.Application/Source/Rom/Commands/BatchDelete/BatchDelete.cs",
        "src/Romd.Admin.Application/Source/Rom/Commands/PurgeUnidentified/PurgeUnidentified.cs",
        "src/Romd.Admin.Application/Source/Rom/Commands/DeleteRom/DeleteRom.cs",
        "src/Romd.Admin.Application/Titles/Commands/UploadTitleMedia/UploadTitleMedia.cs",
        "src/Romd.Admin.Application/Titles/Commands/DeleteTitleMedia/DeleteTitleMedia.cs",
        "src/Romd.Infrastructure/Jobs/Executors/ReplaceDatJobExecutor.cs",
        "src/Romd.Infrastructure/Jobs/Executors/UploadJobExecutor.cs",
        "src/Romd.Admin.Application/Source/Rom/Commands/IngestRom/IngestRom.cs"
    ];

    private static readonly IReadOnlyDictionary<string, string> AllowedHostingEnqueuePortReferences =
        new Dictionary<string, string>(StringComparer.Ordinal);

    [Fact]
    public void DetachedNotificationSites_HaveNoRemainingAllowlistOrOccurrences()
    {
        AllowedDetachedNotificationSites.Count.ShouldBe(0);

        var occurrences = GetSourceFiles("src/Romd.Admin.Application", "src/Romd.Infrastructure")
            .Select(file => new
            {
                Path = ToRepositoryRelativePath(file),
                Count = DetachedNotificationPattern.Matches(File.ReadAllText(file)).Count
            })
            .Where(site => site.Count > 0)
            .ToList();
        occurrences.Sum(site => site.Count).ShouldBe(0);

        var detected = occurrences
            .Select(site => site.Path)
            .Order(StringComparer.Ordinal)
            .ToList();
        var expected = AllowedDetachedNotificationSites.Keys
            .Order(StringComparer.Ordinal)
            .ToList();

        detected.ShouldBe(expected);
    }

    [Fact]
    public void RetiredNotificationFiles_DoNotReferenceNotifierPorts()
    {
        RetiredNotificationFiles.Count.ShouldBe(9);
        RetiredNotificationFiles.Distinct(StringComparer.Ordinal).Count().ShouldBe(9);

        var references = RetiredNotificationFiles
            .Select(path => new
            {
                Path = path,
                Contents = File.ReadAllText(Path.Combine(FindRepositoryRoot(), path))
            })
            .Where(file => RetiredNotifierReferencePattern.IsMatch(file.Contents))
            .Select(file => file.Path)
            .ToList();

        references.ShouldBeEmpty();
    }

    [Fact]
    public void HostingApplicationOutboxReferences_HaveNoRemainingAllowlistOrOccurrences()
    {
        AllowedHostingEnqueuePortReferences.Count.ShouldBe(0);

        var hostingFiles = GetSourceFiles("src/Romd.Hosting");
        var detectedEnqueueReferences = hostingFiles
            .Where(file => AdminEventOutboxReferencePattern.IsMatch(File.ReadAllText(file)))
            .Select(ToRepositoryRelativePath)
            .Order(StringComparer.Ordinal)
            .ToList();
        var expected = AllowedHostingEnqueuePortReferences.Keys
            .Order(StringComparer.Ordinal)
            .ToList();

        detectedEnqueueReferences.ShouldBe(expected);

        var infrastructureOutboxReferences = hostingFiles
            .Where(file => InfrastructureOutboxReferencePattern.IsMatch(File.ReadAllText(file)))
            .Select(ToRepositoryRelativePath)
            .Order(StringComparer.Ordinal)
            .ToList();
        infrastructureOutboxReferences.ShouldBeEmpty();
    }

    [Fact]
    public void LibraryEndpoints_HasNoApplicationOutboxParameter()
    {
        var methods = typeof(LibraryEndpoints)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(method => method.GetParameters()
                .Any(parameter => parameter.ParameterType == typeof(IAdminEventOutbox)))
            .Select(method => method.Name)
            .ToArray();

        methods.ShouldBeEmpty();
    }

    [Fact]
    public void HostingNotifierPorts_HaveNoSourceReferencesOrSignalRImplementations()
    {
        var references = GetSourceFiles("src/Romd.Hosting")
            .Where(file => RetiredNotifierReferencePattern.IsMatch(File.ReadAllText(file)))
            .Select(ToRepositoryRelativePath)
            .ToList();

        references.ShouldBeEmpty();
        typeof(IJobNotifier).IsAssignableFrom(typeof(SignalRJobNotifier)).ShouldBeFalse();
        typeof(IStatsNotifier).IsAssignableFrom(typeof(SignalRStatsNotifier)).ShouldBeFalse();
        GetDeclaredPublicMethodNames<SignalRJobNotifier>().ShouldBe(
            ["SendJobUpdatedAsync", "SendTitleEnrichedAsync"]);
        GetDeclaredPublicMethodNames<SignalRStatsNotifier>().ShouldBe(
            [
                "SendCoverageChangedAsync",
                "SendHealthChangedAsync",
                "SendLibraryUpdatedAsync",
                "SendStorageChangedAsync"
            ]);
        GetDeclaredPublicMethods<SignalRJobNotifier>()
            .ShouldAllBe(method => HasSchemaVersionParameter(method));
        GetDeclaredPublicMethods<SignalRStatsNotifier>()
            .ShouldAllBe(method => HasSchemaVersionParameter(method));
        typeof(SignalRJobNotifier).GetConstructors().Single().GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ShouldBe([typeof(IHubContext<JobHub>)]);
        typeof(SignalRStatsNotifier).GetConstructors().Single().GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ShouldBe([typeof(IHubContext<SystemHub>)]);
    }

    [Fact]
    public void AdminRealtimePublicContracts_ExposeNoRawIntegerIdentity()
    {
        var payloadViolations = FindRawIntegerIdentityPaths(
            typeof(AdminRealtimeLibraryUpdatedPayload).Assembly,
            typeof(AdminRealtimeLibraryUpdatedPayload).Namespace!);

        var notifierArgumentViolations = GetDeclaredPublicMethods<SignalRJobNotifier>()
            .Concat(GetDeclaredPublicMethods<SignalRStatsNotifier>())
            .SelectMany(method => method.GetParameters()
                .Where(parameter => parameter.Name is not null && IsIdentityProperty(parameter.Name))
                .Where(parameter => ContainsRawInteger(parameter.ParameterType))
                .Select(parameter => $"{method.DeclaringType?.FullName}.{method.Name}({parameter.Name})"))
            .ToList();

        payloadViolations.Concat(notifierArgumentViolations).ShouldBeEmpty(
            "admin SignalR contracts must encode persisted identity as opaque strings");
    }

    [Fact]
    public void RawIntegerIdentityGuard_DiscoversAllRealtimeRootsAndTraversesNestedCollections()
    {
        var violations = FindRawIntegerIdentityPaths(
            typeof(RawIdentityGuardMessage).Assembly,
            typeof(RawIdentityGuardMessage).Namespace!);

        violations.ShouldContain($"{typeof(RawIdentityGuardMessage).FullName}.LibraryId");
        violations.ShouldContain($"{typeof(RawIdentityGuardMessage).FullName}.LibraryIds");
        violations.ShouldContain($"{typeof(RawIdentityGuardMessage).FullName}.Nested.TitleIds");
        violations.ShouldNotContain(path => path.EndsWith(".ItemCount", StringComparison.Ordinal));
    }

    private static IReadOnlyList<string> FindRawIntegerIdentityPaths(
        Assembly assembly,
        string realtimeNamespace) =>
        assembly
            .GetExportedTypes()
            .Where(type => type.Namespace is not null
                           && (string.Equals(type.Namespace, realtimeNamespace, StringComparison.Ordinal)
                               || type.Namespace.StartsWith($"{realtimeNamespace}.", StringComparison.Ordinal)))
            .SelectMany(FindRawIntegerIdentityPaths)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

    [Fact]
    public void RealtimeRegistrations_KeepTransportSignalROnlyAndDeliveryInRelay()
    {
        var transportServices = new ServiceCollection();
        transportServices.AddRomdRealtimeTransport();

        transportServices.ShouldContain(descriptor => descriptor.ServiceType == typeof(IHubContext<>));
        transportServices.ShouldNotContain(descriptor => descriptor.ServiceType == typeof(IJobNotifier));
        transportServices.ShouldNotContain(descriptor => descriptor.ServiceType == typeof(IStatsNotifier));
        transportServices.ShouldNotContain(descriptor => descriptor.ServiceType == typeof(SignalRJobNotifier));
        transportServices.ShouldNotContain(descriptor => descriptor.ServiceType == typeof(SignalRStatsNotifier));
        transportServices.ShouldNotContain(descriptor => descriptor.ServiceType == typeof(IAdminRealtimeEventSink));
        transportServices.ShouldNotContain(descriptor =>
            descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationType == typeof(AdminRealtimeOutboxDispatcher));

        var relayServices = new ServiceCollection();
        relayServices.AddRomdAdminRealtimeOutboxRelay();

        AssertSingleScopedService<SignalRJobNotifier>(relayServices);
        AssertSingleScopedService<SignalRStatsNotifier>(relayServices);
        var sinkDescriptor = relayServices
            .Where(descriptor => descriptor.ServiceType == typeof(IAdminRealtimeEventSink))
            .ShouldHaveSingleItem();
        sinkDescriptor.Lifetime.ShouldBe(ServiceLifetime.Scoped);
        sinkDescriptor.ImplementationType.ShouldBe(typeof(SignalRAdminRealtimeEventSink));
        relayServices
            .Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
                                 && descriptor.ImplementationType == typeof(AdminRealtimeOutboxDispatcher))
            .ShouldHaveSingleItem()
            .Lifetime.ShouldBe(ServiceLifetime.Singleton);
        relayServices.ShouldNotContain(descriptor => descriptor.ServiceType == typeof(IJobNotifier));
        relayServices.ShouldNotContain(descriptor => descriptor.ServiceType == typeof(IStatsNotifier));
    }

    [Fact]
    public void ApplicationUnitOfWork_DoesNotExposeEfTrackerReset()
    {
        var references = GetSourceFiles("src")
            .Where(file => File.ReadAllText(file).Contains("ResetAfterRollback", StringComparison.Ordinal))
            .Select(ToRepositoryRelativePath)
            .ToList();

        references.ShouldBeEmpty();
    }

    [Fact]
    public void UploadExecutor_DelegatesEventOwnershipToAtomicIngestHandlers()
    {
        string executor = ReadSource("src/Romd.Infrastructure/Jobs/Executors/UploadJobExecutor.cs");
        string datProcessor = ReadSource("src/Romd.Infrastructure/Jobs/Processors/DatProcessor.cs");
        string romProcessor = ReadSource("src/Romd.Infrastructure/Jobs/Processors/RomProcessor.cs");
        string ingestDat = ReadSource(
            "src/Romd.Admin.Application/Source/Dat/Commands/IngestDat/IngestDat.cs");
        string ingestRom = ReadSource(
            "src/Romd.Admin.Application/Source/Rom/Commands/IngestRom/IngestRom.cs");

        executor.ShouldContain("_datProcessor.ProcessAsync(");
        executor.ShouldContain("_romProcessor.ProcessAsync(");
        AssertExecutorDoesNotOwnNotificationsOrCommits(executor);

        datProcessor.ShouldContain(
            "ICommandHandler<IngestDatCommand, DatIngestResult>");
        romProcessor.ShouldContain(
            "ICommandHandler<IngestRomCommand, RomIngestResult>");
        AssertChildHandlerOwnsAtomicEventCommit(ingestDat);
        AssertChildHandlerOwnsAtomicEventCommit(ingestRom);
    }

    [Fact]
    public void ReplaceDatExecutor_DelegatesEventOwnershipToAtomicIngestAndActivateHandlers()
    {
        string executor = ReadSource("src/Romd.Infrastructure/Jobs/Executors/ReplaceDatJobExecutor.cs");
        string ingestDat = ReadSource(
            "src/Romd.Admin.Application/Source/Dat/Commands/IngestDat/IngestDat.cs");
        string activateDatVersion = ReadSource(
            "src/Romd.Admin.Application/Source/Dat/Commands/ActivateDatVersion/ActivateDatVersion.cs");

        executor.ShouldContain(
            "ICommandHandler<IngestDatCommand, DatIngestResult>");
        executor.ShouldContain(
            "ICommandHandler<ActivateDatVersionCommand, DatFile>");
        AssertExecutorDoesNotOwnNotificationsOrCommits(executor);
        AssertChildHandlerOwnsAtomicEventCommit(ingestDat);
        AssertChildHandlerOwnsAtomicEventCommit(activateDatVersion);
    }

    private static void AssertExecutorDoesNotOwnNotificationsOrCommits(string contents)
    {
        RetiredNotifierReferencePattern.IsMatch(contents).ShouldBeFalse();
        contents.ShouldNotContain("IAdminEventOutbox");
        contents.ShouldNotContain("IUnitOfWork");
        contents.ShouldNotContain("EnqueueAsync(");
        contents.ShouldNotContain("SaveChangesAsync(");
        contents.ShouldNotContain("CommitAsync(");
    }

    private static void AssertChildHandlerOwnsAtomicEventCommit(string contents)
    {
        contents.ShouldContain("_outbox.EnqueueAsync(");
        contents.ShouldContain("transaction.CommitAsync(");
    }

    private static IReadOnlyList<string> GetSourceFiles(params string[] relativeRoots) =>
        relativeRoots
            .SelectMany(relativeRoot => Directory.GetFiles(
                Path.Combine(FindRepositoryRoot(), relativeRoot),
                "*.cs",
                SearchOption.AllDirectories))
            .Where(path => !ToRepositoryRelativePath(path).Contains("/bin/", StringComparison.Ordinal)
                           && !ToRepositoryRelativePath(path).Contains("/obj/", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();

    private static string ToRepositoryRelativePath(string path) =>
        Path.GetRelativePath(FindRepositoryRoot(), path).Replace('\\', '/');

    private static string ReadSource(string relativePath) =>
        File.ReadAllText(Path.Combine(FindRepositoryRoot(), relativePath));

    private static string[] GetDeclaredPublicMethodNames<T>() =>
        GetDeclaredPublicMethods<T>()
            .Select(method => method.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static MethodInfo[] GetDeclaredPublicMethods<T>() =>
        typeof(T).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

    private static bool HasSchemaVersionParameter(MethodInfo method) =>
        method.GetParameters().Any(parameter =>
            parameter.Name == "schemaVersion" && parameter.ParameterType == typeof(int));

    private static bool IsIntOrNullableInt(Type type) =>
        type == typeof(int) || Nullable.GetUnderlyingType(type) == typeof(int);

    private static IReadOnlyList<string> FindRawIntegerIdentityPaths(Type rootType)
    {
        var violations = new List<string>();
        Inspect(rootType, rootType.FullName ?? rootType.Name, new HashSet<Type>(), violations);
        return violations.Order(StringComparer.Ordinal).ToList();
    }

    private static void Inspect(
        Type type,
        string path,
        ISet<Type> ancestors,
        ICollection<string> violations)
    {
        if (!ancestors.Add(type))
        {
            return;
        }

        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            string propertyPath = $"{path}.{property.Name}";
            if (IsIdentityProperty(property.Name) && ContainsRawInteger(property.PropertyType))
            {
                violations.Add(propertyPath);
            }

            foreach (var nestedType in GetNestedContractTypes(property.PropertyType))
            {
                Inspect(nestedType, propertyPath, ancestors, violations);
            }
        }

        ancestors.Remove(type);
    }

    private static bool IsIdentityProperty(string name) =>
        name.EndsWith("Id", StringComparison.Ordinal) || name.EndsWith("Ids", StringComparison.Ordinal);

    private static bool ContainsRawInteger(Type type)
    {
        if (IsIntOrNullableInt(type))
        {
            return true;
        }

        if (type.IsArray)
        {
            return ContainsRawInteger(type.GetElementType()!);
        }

        return type.IsGenericType && type.GetGenericArguments().Any(ContainsRawInteger);
    }

    private static IEnumerable<Type> GetNestedContractTypes(Type type)
    {
        Type? nullableType = Nullable.GetUnderlyingType(type);
        if (nullableType is not null)
        {
            type = nullableType;
        }

        if (type.IsArray)
        {
            yield return type.GetElementType()!;
            yield break;
        }

        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments())
            {
                foreach (var nestedType in GetNestedContractTypes(argument))
                {
                    yield return nestedType;
                }
            }

            yield break;
        }

        if (!type.IsPrimitive
            && !type.IsEnum
            && type != typeof(string)
            && type != typeof(decimal)
            && type != typeof(DateTime)
            && type != typeof(DateTimeOffset)
            && type != typeof(TimeSpan)
            && type != typeof(Guid))
        {
            yield return type;
        }
    }

    private static void AssertSingleScopedService<T>(IServiceCollection services)
        where T : class =>
        services
            .Where(descriptor => descriptor.ServiceType == typeof(T))
            .ShouldHaveSingleItem()
            .Lifetime.ShouldBe(ServiceLifetime.Scoped);

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
