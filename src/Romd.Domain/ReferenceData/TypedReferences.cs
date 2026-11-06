namespace Romd.Domain.ReferenceData;

public sealed record ReferenceMetadata(ReferenceOwnership Ownership, int? BuiltInVersion = null);
public sealed record SystemDefinition(string Name, string CompactLabel, string? Description = null,
    string? AssetHash = null, bool Monochrome = false, bool Retired = false);
public sealed record CompanyDefinition(string Name, string? Description = null, bool Retired = false);
public enum SystemOverrideField { Name, CompactLabel, Description, Icon, Monochrome }
public enum CompanyOverrideField { Name, Description }
public sealed record SystemOverrides(string? Name = null, string? CompactLabel = null,
    string? Description = null, string? Icon = null, bool? Monochrome = null,
    bool HasDescription = false, bool HasIcon = false)
{
    public SystemDefinition Apply(SystemDefinition source) => source with
    {
        Name = Name ?? source.Name,
        CompactLabel = CompactLabel ?? source.CompactLabel,
        Description = HasDescription ? Description : source.Description,
        AssetHash = HasIcon ? Icon : source.AssetHash,
        Monochrome = Monochrome ?? source.Monochrome
    };
    public SystemOverrides Merge(SystemOverrides patch) => new(patch.Name ?? Name, patch.CompactLabel ?? CompactLabel,
        patch.HasDescription ? patch.Description : Description, patch.HasIcon ? patch.Icon : Icon,
        patch.Monochrome ?? Monochrome, patch.HasDescription || HasDescription, patch.HasIcon || HasIcon);
    public SystemOverrides Reset(SystemOverrideField? field) => field switch
    {
        null => new(),
        SystemOverrideField.Name => this with { Name = null },
        SystemOverrideField.CompactLabel => this with { CompactLabel = null },
        SystemOverrideField.Description => this with { HasDescription = false, Description = null },
        SystemOverrideField.Icon => this with { HasIcon = false, Icon = null },
        SystemOverrideField.Monochrome => this with { Monochrome = null },
        _ => throw new ArgumentOutOfRangeException(nameof(field))
    };
}
public sealed record CompanyOverrides(string? Name = null, string? Description = null, bool HasDescription = false)
{
    public CompanyDefinition Apply(CompanyDefinition source) => source with
    {
        Name = Name ?? source.Name,
        Description = HasDescription ? Description : source.Description
    };
    public CompanyOverrides Merge(CompanyOverrides patch) => new(patch.Name ?? Name,
        patch.HasDescription ? patch.Description : Description, patch.HasDescription || HasDescription);
    public CompanyOverrides Reset(CompanyOverrideField? field) => field switch
    {
        null => new(),
        CompanyOverrideField.Name => this with { Name = null },
        CompanyOverrideField.Description => this with { HasDescription = false, Description = null },
        _ => throw new ArgumentOutOfRangeException(nameof(field))
    };
}
public sealed record SystemReference(int Id, string Key, ReferenceMetadata Metadata, SystemDefinition Definition,
    SystemOverrides Overrides, IReadOnlyList<string> ManufacturerKeys)
{
    public SystemDefinition Effective => Overrides.Apply(Definition);
}
public sealed record CompanyReference(string Key, ReferenceMetadata Metadata, CompanyDefinition Definition, CompanyOverrides Overrides)
{
    public CompanyDefinition Effective => Overrides.Apply(Definition);
}
