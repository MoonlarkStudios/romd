namespace Romd.Persistence;

/// <summary>
///     Immutable entities that record who created them and when.
///     The interceptor stamps these on EntityState.Added only.
/// </summary>
public interface ICreatableEntity
{
    Guid CreatedByUserId { get; set; }
    DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
///     Mutable entities that also track the last modifier.
///     The interceptor stamps these on both Added and Modified.
/// </summary>
public interface IEditableEntity : ICreatableEntity
{
    DateTimeOffset? UpdatedAt { get; set; }
    Guid? UpdatedByUserId { get; set; }
}
