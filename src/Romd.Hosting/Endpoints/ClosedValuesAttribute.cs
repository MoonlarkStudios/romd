using System.ComponentModel.DataAnnotations;
using Romd.Admin.Application.Ingestion.Jobs;

namespace Romd.Host.Endpoints;

/// <summary>Declares the same closed string vocabulary for parameter validation and OpenAPI.</summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class ClosedValuesAttribute(params string[] values) : ValidationAttribute("The value is not valid.")
{
    public ClosedValuesAttribute(JobFilterKind kind) : this(JobFilters.Values(kind).ToArray()) { }

    public IReadOnlyList<string> Values { get; } = Array.AsReadOnly(values);

    public override bool IsValid(object? value) =>
        value is null || value is string text && Values.Contains(text, StringComparer.Ordinal);
}
