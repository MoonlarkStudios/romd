using System.Reflection;
using Romd.Contracts.Management.Models;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Contracts;

/// <summary>
///     Guardrail for the "Opaque Public Identity" policy
///     (docs/decisions/admin-api-contract-policy.md): management contracts never expose
///     integer identity. Public IDs are Sqid-encoded strings; internal <c>int</c> IDs stay
///     internal. This test fails the build if anyone reintroduces an <c>int</c>-typed
///     identity property on an exported management contract type.
/// </summary>
public class ManagementContractsTests
{
    /// <summary>
    ///     Reviewed name collisions where a management raw int intentionally shares its name
    ///     with an enum-typed domain property. Empty today. Every future entry needs a concrete
    ///     explanation of why the public value is numeric rather than a policy choice.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ReviewedRawIntEnumNameCollisions =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    ///     Reviewed exceptions to the int-identity scan. Empty by design: every Id-suffixed
    ///     property on a management contract is an identity and must be a Sqid string.
    ///     Non-identity domain values (for example <c>MinimumAge</c>) do not carry the Id
    ///     suffix and are not matched by the scan. Any future entry needs an explicit
    ///     justification comment explaining why the property is not an identity.
    /// </summary>
    private static readonly HashSet<string> AllowedIntIdentityProperties = new(StringComparer.Ordinal);

    [Fact]
    public void ManagementContracts_OpaquePublicIdentity_NoIntTypedIdentityProperties()
    {
        var violations = GetManagementContractTypes()
            .SelectMany(type => type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => IsIdentityPropertyName(property.Name))
                .Where(property => IsIntOrNullableInt(property.PropertyType))
                .Select(property => $"{type.FullName}.{property.Name}"))
            .Where(qualifiedName => !AllowedIntIdentityProperties.Contains(qualifiedName))
            .OrderBy(qualifiedName => qualifiedName, StringComparer.Ordinal)
            .ToList();

        violations.ShouldBeEmpty(
            "Management contracts must not expose int identity; encode public IDs with IdCoder " +
            "(docs/decisions/admin-api-contract-policy.md, \"Opaque Public Identity\")");
    }

    [Fact]
    public void ManagementContracts_NamedStringEnumPolicy_NoUnreviewedRawIntProperties()
    {
        var domainEnumPropertyNames = GetDomainEnumPropertyNames();
        var rawIntPolicyProperties = GetManagementContractTypes()
            .SelectMany(type => type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => IsRawIntPolicyProperty(property, domainEnumPropertyNames))
                .Select(property => $"{type.FullName}.{property.Name}"))
            .OrderBy(qualifiedName => qualifiedName, StringComparer.Ordinal)
            .ToList();

        var violations = rawIntPolicyProperties
            .Where(qualifiedName => !ReviewedRawIntEnumNameCollisions.ContainsKey(qualifiedName))
            .ToList();

        violations.ShouldBeEmpty(
            "A management raw int whose property name corresponds to a domain enum is an " +
            "untyped policy choice; add a mirrored named contract enum instead");
        ReviewedRawIntEnumNameCollisions.Keys
            .Except(rawIntPolicyProperties, StringComparer.Ordinal)
            .ShouldBeEmpty("Remove stale raw-int enum-name exceptions when a contract becomes typed");
        ReviewedRawIntEnumNameCollisions.Values.ShouldAllBe(justification =>
            !string.IsNullOrWhiteSpace(justification));
    }

    [Fact]
    public void ManagementContracts_NamedStringEnumPolicy_StructuralScanDistinguishesPolicyFromNumber()
    {
        var syntheticDomainEnumNames = typeof(SyntheticDomainContract)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => ContainsEnum(property.PropertyType))
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        PropertyInfo policyProperty = typeof(SyntheticPublicContract)
            .GetProperty(nameof(SyntheticPublicContract.AlternateDecision))!;
        PropertyInfo countProperty = typeof(SyntheticPublicContract)
            .GetProperty(nameof(SyntheticPublicContract.ItemCount))!;

        IsRawIntPolicyProperty(policyProperty, syntheticDomainEnumNames).ShouldBeTrue();
        IsRawIntPolicyProperty(countProperty, syntheticDomainEnumNames).ShouldBeFalse();
    }

    private static IReadOnlyList<Type> GetManagementContractTypes() =>
        typeof(PlatformAlias)
            .Assembly
            .GetExportedTypes()
            .Where(type => type.Namespace?.StartsWith("Romd.Contracts.Management", StringComparison.Ordinal) is true)
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToList();

    private static bool IsIdentityPropertyName(string propertyName) =>
        propertyName.EndsWith("Id", StringComparison.Ordinal);

    private static bool IsIntOrNullableInt(Type propertyType) =>
        propertyType == typeof(int) || Nullable.GetUnderlyingType(propertyType) == typeof(int);

    private static IReadOnlySet<string> GetDomainEnumPropertyNames() =>
        typeof(Romd.Domain.Libraries.Library)
            .Assembly
            .GetExportedTypes()
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Where(property => ContainsEnum(property.PropertyType))
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

    private static bool IsRawIntPolicyProperty(
        PropertyInfo property,
        IReadOnlySet<string> domainEnumPropertyNames) =>
        ContainsRawInt(property.PropertyType) && domainEnumPropertyNames.Contains(property.Name);

    private static bool ContainsRawInt(Type type)
    {
        Type inspectedType = Nullable.GetUnderlyingType(type) ?? type;
        if (inspectedType == typeof(int))
            return true;

        if (inspectedType.IsArray)
            return ContainsRawInt(inspectedType.GetElementType()!);

        return inspectedType.IsGenericType && inspectedType
            .GetGenericArguments()
            .Any(ContainsRawInt);
    }

    private static bool ContainsEnum(Type type)
    {
        Type inspectedType = Nullable.GetUnderlyingType(type) ?? type;
        if (inspectedType.IsEnum)
            return true;

        if (inspectedType.IsArray)
            return ContainsEnum(inspectedType.GetElementType()!);

        return inspectedType.IsGenericType && inspectedType
            .GetGenericArguments()
            .Any(ContainsEnum);
    }

    private enum SyntheticDecision
    {
        First
    }

    private sealed record SyntheticDomainContract(SyntheticDecision AlternateDecision, int ItemCount);
    private sealed record SyntheticPublicContract(int AlternateDecision, int ItemCount);
}
