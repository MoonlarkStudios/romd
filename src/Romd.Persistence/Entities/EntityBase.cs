namespace Romd.Persistence.Entities;

/// <summary>
///     Base for integer-keyed entities.
/// </summary>
public abstract class EntityBase
{
    public int Id { get; set; }
}

/// <summary>
///     Base for GUID-keyed entities. All GUID entities track creation time.
/// </summary>
public abstract class GuidEntityBase
{
    public Guid Id { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
