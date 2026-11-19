import type { DatReplacementPreview } from '@romd/admin-api-client';

export const reviewFilterLabels = {
  NeedsReview: 'Needs review',
  RemovedLinked: 'Removes a title link',
  LostMatches: 'ROMs losing a match',
  RemovedStored: 'Removed with stored ROMs',
  All: 'All changes',
  Removed: 'Removed',
  Changed: 'Changed',
  Sha1Changed: 'SHA-1 changed',
  OtherChecksumOnly: 'CRC/MD5 only',
  MetadataOnly: 'Metadata only',
  Added: 'Added',
  NewMatches: 'ROMs gaining a match',
} as const;

export type DatChangeFilter = keyof typeof reviewFilterLabels;

export type ReviewRailRow = {
  filter: DatChangeFilter;
  count: number;
  /** Review rows carry the warning tone only while they count something. */
  tone?: 'review' | 'gain';
  nested?: boolean;
};

export type ReviewRailGroup = {
  label: string;
  rows: ReviewRailRow[];
};

export function countLabel(count: number, singular: string, plural = `${singular}s`) {
  return `${count.toLocaleString()} ${count === 1 ? singular : plural}`;
}

export function totalChanges(preview: DatReplacementPreview) {
  return preview.entriesAdded + preview.entriesRemoved + preview.entriesChanged;
}

function firstImport(preview: DatReplacementPreview) {
  return !preview.activeSha256;
}

/** The rail is both the summary and the list filter, so its counts and the list never disagree in meaning. */
export function reviewRail(preview: DatReplacementPreview): ReviewRailGroup[] {
  const impact = preview.impact;
  if (firstImport(preview))
    return [
      {
        label: 'Changes',
        rows: [
          {
            filter: 'All',
            count: totalChanges(preview),
          },
          ...(impact
            ? [
                {
                  filter: 'NewMatches' as const,
                  count: impact.newRomMatches,
                  tone: 'gain' as const,
                },
              ]
            : []),
        ],
      },
    ];
  const all: ReviewRailRow[] = [
    {
      filter: 'All',
      count: totalChanges(preview),
    },
    {
      filter: 'Removed',
      count: preview.entriesRemoved,
    },
    {
      filter: 'Changed',
      count: preview.entriesChanged,
    },
  ];
  if (impact)
    all.push(
      {
        filter: 'Sha1Changed',
        count: impact.sha1ChangedEntries,
        nested: true,
      },
      {
        filter: 'OtherChecksumOnly',
        count: impact.otherChecksumOnlyEntries,
        nested: true,
      },
      {
        filter: 'MetadataOnly',
        count: impact.metadataOnlyEntries,
        nested: true,
      },
    );
  all.push({
    filter: 'Added',
    count: preview.entriesAdded,
  });
  if (impact)
    all.push({
      filter: 'NewMatches',
      count: impact.newRomMatches,
      tone: 'gain',
    });
  if (!impact)
    return [
      {
        label: 'Changes',
        rows: all,
      },
    ];
  return [
    {
      label: 'Catalog impact',
      rows: [
        {
          filter: 'NeedsReview',
          count: impact.needsReviewEntries,
          tone: 'review',
        },
        {
          filter: 'RemovedLinked',
          count: impact.removedWithTitleLinks,
          tone: 'review',
          nested: true,
        },
        {
          filter: 'LostMatches',
          count: impact.lostRomMatches,
          tone: 'review',
          nested: true,
        },
        {
          filter: 'RemovedStored',
          count: impact.removedWithStoredRoms,
          tone: 'review',
          nested: true,
        },
      ],
    },
    {
      label: 'Changes',
      rows: all,
    },
  ];
}

export function defaultReviewFilter(preview: DatReplacementPreview): DatChangeFilter {
  return preview.impact?.needsReviewEntries ? 'NeedsReview' : 'All';
}

export type ReviewVerdict = {
  tone: 'review' | 'clear' | 'unknown' | 'none';
  title: string;
  detail: string;
};

function gains(count: number) {
  return count ? ` ${countLabel(count, 'stored ROM gains', 'stored ROMs gain')} a match.` : '';
}

/** One sentence the admin can act on before reading any list. */
export function reviewVerdict(preview: DatReplacementPreview): ReviewVerdict {
  const impact = preview.impact;
  if (totalChanges(preview) === 0)
    return {
      tone: 'none',
      title: 'No supported entry changes',
      detail: 'Header metadata, formatting, or fields outside this comparison may differ.',
    };
  if (firstImport(preview))
    return {
      tone: 'clear',
      title: `Imports ${countLabel(preview.candidateEntries, 'catalog entry', 'catalog entries')}`,
      detail: `Nothing is installed for this source yet.${gains(impact?.newRomMatches ?? 0)}`,
    };
  if (!impact)
    return {
      tone: 'unknown',
      title: 'Catalog impact not calculated',
      detail:
        'Stored ROMs that match removed entries or old SHA-1 values may lose their match in this catalog.',
    };
  if (!impact.needsReviewEntries)
    return {
      tone: 'clear',
      title: 'Nothing needs review',
      detail: `No stored ROM matches or title links are lost.${gains(impact.newRomMatches)}`,
    };
  const effects = [
    impact.removedWithTitleLinks
      ? `removes ${countLabel(impact.removedWithTitleLinks, 'title link')}`
      : undefined,
    impact.lostRomMatches
      ? `${countLabel(impact.lostRomMatches, 'stored ROM loses its', 'stored ROMs lose their')} match`
      : undefined,
  ].filter((effect) => effect != null);
  const unaffected = impact.lostRomMatches ? '' : ' No stored ROM loses its match.';
  if (!effects.length)
    return {
      tone: 'review',
      title: countLabel(impact.needsReviewEntries, 'change needs review', 'changes need review'),
      detail: `No stored ROM loses its match. See the flagged entries for details.${gains(impact.newRomMatches)}`,
    };
  const sentence = `${effects.join('. ')}.`;
  return {
    tone: 'review',
    title: countLabel(impact.needsReviewEntries, 'change needs review', 'changes need review'),
    detail: `${sentence.charAt(0).toUpperCase()}${sentence.slice(1)}${unaffected}${gains(impact.newRomMatches)}`,
  };
}

/** The consequence that sits next to the decision buttons. */
export function decisionConsequence(preview: DatReplacementPreview) {
  const impact = preview.impact;
  if (firstImport(preview))
    return `Importing adds ${countLabel(preview.candidateEntries, 'catalog entry', 'catalog entries')} in a background job.${gains(impact?.newRomMatches ?? 0)}`;
  if (!impact) return 'Applying runs in a background job. Stored ROM files are never deleted.';
  const effects = [
    impact.removedWithTitleLinks
      ? `removes ${countLabel(impact.removedWithTitleLinks, 'title link')}`
      : undefined,
    impact.lostRomMatches
      ? `removes the catalog match for ${countLabel(impact.lostRomMatches, 'stored ROM')}`
      : undefined,
    impact.newRomMatches
      ? `adds a catalog match for ${countLabel(impact.newRomMatches, 'stored ROM')}`
      : undefined,
  ].filter((effect) => effect != null);
  const first = effects.length
    ? `Applying ${effects.join(' and ')}.`
    : impact.needsReviewEntries
      ? 'Review the flagged changes before applying.'
      : 'Applying changes no stored ROM matches or title links.';
  const unaffected =
    impact.removedWithTitleLinks && !impact.lostRomMatches ? ' No stored ROM loses its match.' : '';
  return `${first}${unaffected} Libraries refresh after the job.`;
}

/** A compact record of what was approved, shown while the job runs and after it finishes. */
export function reviewOutcome(preview: DatReplacementPreview) {
  const impact = preview.impact;
  return [
    impact?.removedWithTitleLinks
      ? countLabel(impact.removedWithTitleLinks, 'title link removed', 'title links removed')
      : undefined,
    impact?.lostRomMatches
      ? countLabel(
          impact.lostRomMatches,
          'stored ROM losing its match',
          'stored ROMs losing their match',
        )
      : undefined,
    preview.entriesRemoved ? `${preview.entriesRemoved.toLocaleString()} removed` : undefined,
    preview.entriesChanged ? `${preview.entriesChanged.toLocaleString()} changed` : undefined,
    preview.entriesAdded ? `${preview.entriesAdded.toLocaleString()} added` : undefined,
  ]
    .filter((part) => part != null)
    .join(' · ');
}

/** Where the reader is in a review. Absent fields mean the defaults: the verdict's filter and no entry. */
export type ReviewView = {
  filter?: DatChangeFilter;
  entry?: string;
  query?: string;
};

const filterSlugs: Record<DatChangeFilter, string> = {
  NeedsReview: 'needs-review',
  RemovedLinked: 'unlinks-title',
  LostMatches: 'losing-match',
  RemovedStored: 'removed-stored',
  All: 'all',
  Removed: 'removed',
  Changed: 'changed',
  Sha1Changed: 'sha1-changed',
  OtherChecksumOnly: 'crc-md5-only',
  MetadataOnly: 'metadata-only',
  Added: 'added',
  NewMatches: 'gaining-match',
};

const reviewParamNames = [
  'filter',
  'entry',
  'q',
] as const;

export function reviewViewFromParams(params: URLSearchParams): ReviewView {
  const slug = params.get('filter');
  const filter = (Object.keys(filterSlugs) as DatChangeFilter[]).find(
    (key) => filterSlugs[key] === slug,
  );
  return {
    filter,
    entry: params.get('entry') || undefined,
    query: params.get('q')?.trim() || undefined,
  };
}

/** Writes only the fields that differ from the defaults, so a fresh review has a bare URL. */
export function withReviewView(params: URLSearchParams, view: ReviewView) {
  const next = new URLSearchParams(params);
  for (const name of reviewParamNames) next.delete(name);
  if (view.filter) next.set('filter', filterSlugs[view.filter]);
  if (view.entry) next.set('entry', view.entry);
  if (view.query) next.set('q', view.query);
  return next;
}
