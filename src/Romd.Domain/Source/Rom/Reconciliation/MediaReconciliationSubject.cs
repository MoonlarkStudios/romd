namespace Romd.Domain.Source.Rom;

public enum MediaReconciliationSubjectKind { LogicalRom = 1, OriginalLayout = 2, Representation = 3, CatalogCdLayout = 4 }

/// <summary>A durable reconciliation scope, independent of any current recipe, decoder or live output owner.</summary>
public abstract class MediaReconciliationSubject : IEquatable<MediaReconciliationSubject>, IComparable<MediaReconciliationSubject>
{
    private MediaReconciliationSubject(int id)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
        Id = id;
    }
    public int Id { get; }
    public abstract MediaReconciliationSubjectKind Kind { get; }
    public sealed class LogicalRom(int id) : MediaReconciliationSubject(id)
    { public override MediaReconciliationSubjectKind Kind => MediaReconciliationSubjectKind.LogicalRom; }
    public sealed class OriginalLayout(int id) : MediaReconciliationSubject(id)
    { public override MediaReconciliationSubjectKind Kind => MediaReconciliationSubjectKind.OriginalLayout; }
    public sealed class Representation(int id) : MediaReconciliationSubject(id)
    { public override MediaReconciliationSubjectKind Kind => MediaReconciliationSubjectKind.Representation; }
    public sealed class CatalogCdLayout(int catalogFileId) : MediaReconciliationSubject(catalogFileId)
    { public override MediaReconciliationSubjectKind Kind => MediaReconciliationSubjectKind.CatalogCdLayout; }
    public bool Equals(MediaReconciliationSubject? other) => other is not null && Kind == other.Kind && Id == other.Id;
    public sealed override bool Equals(object? other) => other is MediaReconciliationSubject subject && Equals(subject);
    public sealed override int GetHashCode() => HashCode.Combine(Kind, Id);
    public int CompareTo(MediaReconciliationSubject? other) => other is null ? 1 : Kind != other.Kind
        ? Kind.CompareTo(other.Kind) : Id.CompareTo(other.Id);
}
