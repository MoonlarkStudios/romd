using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Romd.Contracts.Management.Models;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Hosting;

/// <summary>
///     Audits every exported management enum against the admin host's configured JSON boundary.
///     This keeps strict parsing centralized as the contract assembly gains enum types.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class AdminEnumJsonContractTests(IntegrationTestFixture fixture)
{
    [Fact]
    public void AdminJsonOptions_AllManagementEnumsRequireExactDeclaredNames()
    {
        var options = fixture.Services
            .GetRequiredService<IOptions<JsonOptions>>()
            .Value
            .SerializerOptions;
        var enumTypes = typeof(PlatformAlias)
            .Assembly
            .GetExportedTypes()
            .Where(type => type.IsEnum)
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToList();

        enumTypes.ShouldNotBeEmpty();
        foreach (Type enumType in enumTypes)
        {
            string[] names = Enum.GetNames(enumType);
            names.ShouldNotBeEmpty();
            string firstName = names[0];

            foreach (string name in names)
            {
                object value = Enum.Parse(enumType, name);
                JsonSerializer.Serialize(value, enumType, options).ShouldBe($"\"{name}\"");
                JsonSerializer.Deserialize($"\"{name}\"", enumType, options).ShouldBe(value);
            }

            Should.Throw<JsonException>(() =>
                JsonSerializer.Deserialize($"\"{firstName.ToLowerInvariant()}\"", enumType, options));
            Should.Throw<JsonException>(() =>
                JsonSerializer.Deserialize("0", enumType, options));
            Should.Throw<JsonException>(() =>
                JsonSerializer.Deserialize("\"NotADeclaredName\"", enumType, options));

            if (names.Length > 1)
            {
                Should.Throw<JsonException>(() =>
                    JsonSerializer.Deserialize(
                        $"\"{firstName}, {names[1]}\"",
                        enumType,
                        options));
            }
        }
    }
}
