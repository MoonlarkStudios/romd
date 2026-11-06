using Romd.Domain.Source.Rom;

namespace Romd.Domain.Jobs;

/// <summary>Only stable selection scope; current kind and eligibility are resolved from facts by the worker.</summary>
public abstract class MediaReconciliationFilter
{
    private MediaReconciliationFilter() { }
    public sealed class AllSubjects : MediaReconciliationFilter;
    public sealed class RetainedRepresentations : MediaReconciliationFilter;
    public sealed class KindSubjects : MediaReconciliationFilter
    {
        public KindSubjects(string kind, int? platformId)
        {
            if (kind is not { Length: > 0 and <= 64 } || !kind.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-') ||
                platformId is <= 0)
                throw new ArgumentException("Selection requires a registered kind id and optional positive platform id.");
            Kind = kind;
            PlatformId = platformId;
        }
        public string Kind { get; }
        public int? PlatformId { get; }
    }
}

/// <summary>Finite captured high-watermarks; no source or ownership grant.</summary>
public sealed record MediaReconciliationBounds
{
    public MediaReconciliationBounds(int logicalRom, int originalLayout, int representation, int catalogCdLayout)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(logicalRom);
        ArgumentOutOfRangeException.ThrowIfNegative(originalLayout);
        ArgumentOutOfRangeException.ThrowIfNegative(representation);
        ArgumentOutOfRangeException.ThrowIfNegative(catalogCdLayout);
        LogicalRom = logicalRom;
        OriginalLayout = originalLayout;
        Representation = representation;
        CatalogCdLayout = catalogCdLayout;
    }
    public int LogicalRom { get; }
    public int OriginalLayout { get; }
    public int Representation { get; }
    public int CatalogCdLayout { get; }
    public long MaximumSubjects => (long)LogicalRom + OriginalLayout + Representation + CatalogCdLayout;
    public bool Contains(MediaReconciliationSubject subject) => subject.Id <= subject.Kind switch
    {
        MediaReconciliationSubjectKind.LogicalRom => LogicalRom,
        MediaReconciliationSubjectKind.OriginalLayout => OriginalLayout,
        MediaReconciliationSubjectKind.Representation => Representation,
        MediaReconciliationSubjectKind.CatalogCdLayout => CatalogCdLayout,
        _ => throw new ArgumentOutOfRangeException(nameof(subject))
    };
}

public abstract class MediaReconciliationRequest
{
    private MediaReconciliationRequest() { }
    public sealed class SubjectWork : MediaReconciliationRequest
    {
        public SubjectWork(MediaReconciliationSubject subject) { ArgumentNullException.ThrowIfNull(subject); Subject = subject; }
        public MediaReconciliationSubject Subject { get; }
    }
    public sealed class SelectionWork : MediaReconciliationRequest
    {
        public SelectionWork(MediaReconciliationFilter filter, MediaReconciliationBounds bounds, long limit, bool conversionRequested)
        {
            ArgumentNullException.ThrowIfNull(filter);
            ArgumentNullException.ThrowIfNull(bounds);
            if (limit is < 1 or > (4L * int.MaxValue))
                throw new ArgumentOutOfRangeException(nameof(limit));
            if (filter is MediaReconciliationFilter.RetainedRepresentations && conversionRequested)
                throw new ArgumentException("A retained-representation selection never requests encoding.");
            Filter = filter;
            Bounds = bounds;
            Limit = limit;
            ConversionRequested = conversionRequested;
        }
        public MediaReconciliationFilter Filter { get; }
        public MediaReconciliationBounds Bounds { get; }
        /// <summary>Bounds created/adopted work, allowing a skipped prefix to be walked without consuming the requested limit.</summary>
        public long Limit { get; }
        public bool ConversionRequested { get; }
    }
}

/// <summary>Durable counts for captured child jobs; later independent demand cannot change their outcomes.</summary>
public sealed record MediaReconciliationSelectionProgress(MediaReconciliationSubject? Cursor, long Selected, long Created,
    long Adopted, long Skipped, long Completed, long Failed, bool EnumerationComplete);
