using System.Reflection;
using ErrorOr;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Application.Common.Pagination;
using Romd.Domain.Libraries;
using Romd.Consumer.Application.Browse;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Hosting;

/// <summary>Public error factories must not accept database identities that can leak into error text.</summary>
public sealed class OpaqueErrorIdentityGuardrailTests
{
    [Fact]
    public void ErrorFactories_DoNotAcceptRawNumericIdentityParameters()
    {
        Assembly[] assemblies = [typeof(Library).Assembly, typeof(IJobRepository).Assembly, typeof(IConsumerBrowseRepository).Assembly, typeof(KeysetCursor).Assembly];
        string[] violations = assemblies.Distinct()
            .SelectMany(assembly => assembly.GetExportedTypes())
            .Where(type => type.Name.EndsWith("Errors", StringComparison.Ordinal))
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(method => method.ReturnType == typeof(Error))
            .SelectMany(method => method.GetParameters()
                .Where(parameter => parameter.Name?.EndsWith("Id", StringComparison.OrdinalIgnoreCase) is true
                                    || parameter.Name?.EndsWith("Ids", StringComparison.OrdinalIgnoreCase) is true)
                .Where(parameter => IsNumericIdentity(parameter.ParameterType))
                .Select(parameter => $"{method.DeclaringType!.FullName}.{method.Name}({parameter.Name})"))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        violations.ShouldBeEmpty("Public error details use opaque identity or context-free descriptions; internal numeric IDs stay internal.");
    }

    private static bool IsNumericIdentity(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type.IsArray)
            return IsNumericIdentity(type.GetElementType()!);
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            return IsNumericIdentity(type.GenericTypeArguments[0]);
        if (type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong)
            || type == typeof(short) || type == typeof(ushort) || type == typeof(byte) || type == typeof(sbyte))
            return true;
        return type.GetInterfaces()
            .Where(contract => contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            .Any(contract => IsNumericIdentity(contract.GenericTypeArguments[0]));
    }
}
