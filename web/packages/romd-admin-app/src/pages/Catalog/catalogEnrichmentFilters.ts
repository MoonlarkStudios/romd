import type { TitleStatus } from '../../components/Title/enrichmentPresentation';

type EnrichmentFlag = Exclude<TitleStatus, 'Settled'>;

/** Catalog `enrichment` values in chip order. Several flags mean all of them; Settled means none. */
const enrichmentFlags: readonly EnrichmentFlag[] = [
  'NeedsReview',
  'Blocked',
  'Processing',
  'Automatic',
];
export const enrichmentFilters: readonly TitleStatus[] = [
  ...enrichmentFlags,
  'Settled',
];

export const lastAttemptStatuses = [
  'None',
  'Pending',
  'Completed',
  'Failed',
  'NotFound',
  'LowConfidence',
] as const;
export type LastAttemptStatus = (typeof lastAttemptStatuses)[number];

export const lastAttemptLabels: Record<LastAttemptStatus, string> = {
  None: 'Never attempted',
  Pending: 'Pending',
  Completed: 'Completed',
  Failed: 'Failed',
  NotFound: 'Not found',
  LowConfidence: 'Low confidence',
};

/** Known values once each, in chip order. Settled excludes every flag, so it wins over them. */
export function canonicalEnrichment(values: readonly string[]): TitleStatus[] {
  const known = enrichmentFilters.filter((value) => values.includes(value));
  return known.includes('Settled')
    ? [
        'Settled',
      ]
    : known;
}

export function parseLastAttempt(value: string | null | undefined): LastAttemptStatus | undefined {
  return lastAttemptStatuses.find((status) => status === value);
}

/** Bookmarks from before the repeatable parameter carried a single flag in `enrichmentStatus`. */
export function legacyEnrichmentFlag(value: string | null): EnrichmentFlag[] {
  return enrichmentFlags.filter((flag) => flag === value);
}

/** An absent last attempt means the title was never attempted; unknown values are shown as recorded. */
export function lastAttemptLabel(value = 'None'): string {
  const status = parseLastAttempt(value);
  return status ? lastAttemptLabels[status] : value;
}
