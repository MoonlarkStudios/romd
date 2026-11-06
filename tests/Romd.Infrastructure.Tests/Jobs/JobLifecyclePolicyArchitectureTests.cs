using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Domain.Jobs;
using Romd.Infrastructure.Import;
using Romd.Infrastructure.Jobs;
using Romd.Infrastructure.Jobs.Lifecycle;
using Romd.Infrastructure.Tests.Hosting;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Jobs;

/// <summary>Keeps job-family lifecycle behaviour in DI-registered policies and out of the shared runner.</summary>
public sealed class JobLifecyclePolicyArchitectureTests
{
    private static readonly Type[] LifecycleCapabilities =
    [
        typeof(IJobRetryBudgetPolicy<>), typeof(IJobCancellationPolicy<>),
        typeof(IJobTerminationPolicy<>), typeof(IJobCompletionCleanup<>)
    ];

    [Fact]
    public void JobRunnerSource_NamesNoConcreteJobFamilyOrFamilyService()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "Romd.Infrastructure", "Jobs", "JobRunner.cs"));
        var forbidden = typeof(Job).Assembly.GetTypes()
            .Where(type => type is { IsAbstract: false, IsClass: true } && type.IsSubclassOf(typeof(Job)))
            .Select(type => type.Name)
            .Concat([
                nameof(IMediaReconciliationExecution), nameof(IMediaReconciliationSelections),
                nameof(ImportSourceCleanup), "Romd.Admin.Application.Source", "Romd.Infrastructure.Import"
            ])
            .ToArray();
        forbidden.Length.ShouldBeGreaterThan(10);

        var named = forbidden.Where(name => Regex.IsMatch(source, $@"\b{Regex.Escape(name)}\b")).ToArray();

        named.ShouldBeEmpty("JobRunner must reach family behaviour only through lifecycle capabilities.");
    }

    [Fact]
    public void JobRunnerConstructors_TakeNoFamilySpecificDependency()
    {
        var parameterTypes = typeof(JobRunner<>).GetConstructors()
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        parameterTypes.ShouldNotContain(type => type.Namespace != null &&
            (type.Namespace.StartsWith("Romd.Admin.Application.Source", StringComparison.Ordinal) ||
             type.Namespace.StartsWith("Romd.Infrastructure.Import", StringComparison.Ordinal) ||
             type.Namespace.StartsWith("Romd.Infrastructure.Jobs.Lifecycle", StringComparison.Ordinal)));
    }

    [Fact]
    public void WorkerRegistration_RegistersDefaultsAndEachFamilyPolicy()
    {
        var services = RomdBoundaryTestServices.CreateWorkerStyleServices();

        foreach (var capability in LifecycleCapabilities)
            services.ShouldContain(descriptor => descriptor.ServiceType == capability &&
                descriptor.ImplementationType == typeof(DefaultJobLifecyclePolicy<>));

        ShouldRegister<IJobRetryBudgetPolicy<UploadJob>>(services);
        ShouldRegister<IJobCompletionCleanup<UploadJob>>(services, typeof(MoveImportCompletionCleanup));
        foreach (var (job, policy) in new[]
                 {
                     (typeof(MediaReconciliationJob), typeof(MediaReconciliationLifecyclePolicy))
                 })
        {
            services.Count(descriptor => descriptor.ServiceType == typeof(IJobRetryBudgetPolicy<>).MakeGenericType(job)).ShouldBe(1);
            services.Count(descriptor => descriptor.ServiceType == typeof(IJobCancellationPolicy<>).MakeGenericType(job)).ShouldBe(1);
            services.Count(descriptor => descriptor.ServiceType == typeof(IJobTerminationPolicy<>).MakeGenericType(job)).ShouldBe(1);
            services.Count(descriptor => descriptor.ServiceType == policy).ShouldBe(1);
        }
    }

    [Fact]
    public void WorkerRegistration_ResolvesFamilyPoliciesThroughLifecycleCapabilities()
    {
        var services = new ServiceCollection();
        services.AddSingleton(NSubstitute.Substitute.For<IMediaReconciliationExecution>());
        services.AddSingleton(NSubstitute.Substitute.For<IMediaReconciliationSelections>());
        services.AddRomdJobLifecyclePolicies();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var resolve = scope.ServiceProvider;

        var mediaLifecycle = resolve.GetRequiredService<IJobTerminationPolicy<MediaReconciliationJob>>();
        mediaLifecycle.ShouldBeOfType<MediaReconciliationLifecyclePolicy>();
        resolve.GetRequiredService<IJobCancellationPolicy<MediaReconciliationJob>>().ShouldBeSameAs(mediaLifecycle);
        var mediaBudget = resolve.GetRequiredService<IJobRetryBudgetPolicy<MediaReconciliationJob>>();
        var subject = MediaReconciliationJob.Create(Guid.NewGuid(), new MediaReconciliationRequest.SubjectWork(new Romd.Domain.Source.Rom.MediaReconciliationSubject.LogicalRom(1)), "subject", null, null, DateTimeOffset.UtcNow);
        var selection = MediaReconciliationJob.Create(Guid.NewGuid(), new MediaReconciliationRequest.SelectionWork(new MediaReconciliationFilter.AllSubjects(), new(1, 0, 0, 0), 1, false), "selection", null, null, DateTimeOffset.UtcNow);
        mediaBudget.BudgetFor(subject)!.MaximumDeferralsWithoutProgress.ShouldBe(JobContext.MaxUploadDeferrals);
        mediaBudget.BudgetFor(selection)!.MaximumDeferralsWithoutProgress.ShouldBeNull();
        mediaBudget.BudgetFor(selection)!.MaximumAge.ShouldBe(TimeSpan.FromDays(1));
        resolve.GetRequiredService<IJobRetryBudgetPolicy<UploadJob>>().BudgetFor(UploadJob.Create("fixture.zip", createdAt: DateTimeOffset.UtcNow)).ShouldBe(
            new JobRetryBudget("Upload", TimeSpan.FromDays(1), JobContext.MaxUploadDeferrals));
        resolve.GetRequiredService<IJobRetryBudgetPolicy<ExportJob>>().BudgetFor(ExportJob.CreateAllCatalog(createdAt: DateTimeOffset.UtcNow)).ShouldBeNull();
        resolve.GetRequiredService<IJobTerminationPolicy<ExportJob>>().ShouldBeOfType<DefaultJobLifecyclePolicy<ExportJob>>();
    }

    [Fact]
    public void ApiHostRegistrations_ComposeNoJobLifecyclePolicy()
    {
        foreach (var services in new[]
                 {
                     RomdBoundaryTestServices.CreateAdminApiStyleServices(),
                     RomdBoundaryTestServices.CreateConsumerStyleServices()
                 })
        {
            services.ShouldNotContain(descriptor => IsLifecycleService(descriptor.ServiceType));
            services.ShouldNotContain(descriptor => descriptor.ServiceType.Namespace == typeof(DefaultJobLifecyclePolicy<>).Namespace);
        }
    }

    private static bool IsLifecycleService(Type type) =>
        LifecycleCapabilities.Contains(type.IsGenericType ? type.GetGenericTypeDefinition() : type);

    private static void ShouldRegister<TService>(IServiceCollection services, Type? implementation = null)
    {
        var descriptor = services.Single(candidate => candidate.ServiceType == typeof(TService));
        if (implementation is not null)
            descriptor.ImplementationType.ShouldBe(implementation);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Romd.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find repository root.");
    }
}
