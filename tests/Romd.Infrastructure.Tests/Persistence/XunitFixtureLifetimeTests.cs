using System.Reflection;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class XunitFixtureLifetimeTests
{
    [Fact]
    public void TestClasses_WithAsyncCleanup_UseAFrameworkSupportedLifetime()
    {
        var unsupported = typeof(XunitFixtureLifetimeTests).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && !type.IsInterface)
            .Where(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Any(method => method.GetCustomAttributesData()
                    .Any(attribute => typeof(FactAttribute).IsAssignableFrom(attribute.AttributeType))))
            .Where(type => typeof(IAsyncDisposable).IsAssignableFrom(type)
                || type.GetMethod(nameof(IAsyncDisposable.DisposeAsync), Type.EmptyTypes) is not null)
            .Where(type => !typeof(IAsyncLifetime).IsAssignableFrom(type)
                && !typeof(IDisposable).IsAssignableFrom(type))
            .Select(type => type.FullName)
            .Order()
            .ToArray();

        unsupported.ShouldBeEmpty("xUnit v2 ignores standalone IAsyncDisposable; test-class cleanup must use IAsyncLifetime or IDisposable.");
    }
}
