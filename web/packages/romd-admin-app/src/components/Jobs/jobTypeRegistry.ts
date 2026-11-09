import type { Job } from '@romd/admin-api-client';
import {
  IconDownload,
  IconRefresh,
  IconReplace,
  IconSparkles,
  IconUpload,
} from '@tabler/icons-react';
import type { QueryKey } from '@tanstack/react-query';
import { enrichmentWorkspaceKeys } from '../../hooks/api/enrichmentWorkspaceCache';
import {
  acquisitionKeys,
  providerMatchKeys,
  titleEnrichmentKeys,
  titleSurfaceKeys,
} from '../../hooks/api/titleCache';
import { catalogSearchKeys } from '../../hooks/api/useCatalogSearch';
import { datKeys } from '../../hooks/api/useDats';
import { mediaReconciliationKeys } from '../../hooks/api/useMediaReconciliation';
import { romKeys } from '../../hooks/api/useRoms';
import { storageStatsKeys } from '../../hooks/api/useStorageStats';
import { titleDetailKeys } from '../../hooks/api/useTitleDetail';

interface JobTypeConfig {
  icon: typeof IconUpload;
  color: string;
  label: string;
  invalidates: (job: Job) => QueryKey[];
}

const registry: Record<string, JobTypeConfig> = {
  'media-reconciliation': {
    icon: IconRefresh,
    color: 'teal',
    label: 'Media reconciliation',
    invalidates: () => [
      romKeys.all,
      storageStatsKeys.all,
      titleDetailKeys.all,
      mediaReconciliationKeys.policies,
      [
        'originalDiscLayouts',
      ],
    ],
  },
  upload: {
    icon: IconUpload,
    color: 'blue',
    label: 'Upload',
    invalidates: () => [
      romKeys.all,
      datKeys.all,
      [
        'libraries',
      ],
    ],
  },
  replace_dat: {
    icon: IconReplace,
    color: 'orange',
    label: 'DAT Replace',
    invalidates: () => [
      datKeys.all,
    ],
  },
  enrichment: {
    icon: IconSparkles,
    color: 'violet',
    label: 'Title enrichment',
    invalidates: (job) => {
      const titleId = (
        job as {
          titleId?: string;
        }
      ).titleId;
      return [
        ...(titleId
          ? titleSurfaceKeys(titleId)
          : [
              catalogSearchKeys.all,
            ]),
        enrichmentWorkspaceKeys.all,
      ];
    },
  },
  bulk_enrichment: {
    icon: IconSparkles,
    color: 'violet',
    label: 'Bulk enrichment (legacy)',
    invalidates: () => [
      catalogSearchKeys.all,
      [
        'enrichment-workspace',
      ],
    ],
  },
  'artwork-completion': {
    icon: IconSparkles,
    color: 'teal',
    label: 'Enrichment run',
    invalidates: () => [
      catalogSearchKeys.all,
      titleDetailKeys.all,
      titleEnrichmentKeys.all,
      providerMatchKeys.all,
      [
        'artwork',
      ],
      acquisitionKeys.titles,
      enrichmentWorkspaceKeys.all,
    ],
  },
  'metadata-rematerialization': {
    icon: IconRefresh,
    color: 'teal',
    label: 'Metadata update',
    invalidates: (job) => [
      job.systemKey
        ? [
            'metadata-policy',
            job.systemKey,
          ]
        : [
            'metadata-policy',
          ],
      catalogSearchKeys.all,
      titleDetailKeys.all,
      titleEnrichmentKeys.all,
      enrichmentWorkspaceKeys.all,
    ],
  },
  'artwork-import': {
    icon: IconUpload,
    color: 'teal',
    label: 'Artwork import',
    invalidates: (job) =>
      job.jobType === 'artwork-import'
        ? [
            ...titleSurfaceKeys(job.titleId),
            enrichmentWorkspaceKeys.all,
          ]
        : [],
  },
  export: {
    icon: IconDownload,
    color: 'green',
    label: 'Export',
    invalidates: () => [],
  },
  materialization: {
    icon: IconRefresh,
    color: 'teal',
    label: 'Materialization',
    invalidates: () => [
      [
        'libraries',
      ],
    ],
  },
};

const fallback: JobTypeConfig = {
  icon: IconUpload,
  color: 'gray',
  label: 'Job',
  invalidates: () => [],
};

export function isJobType(value: string): value is NonNullable<Job['jobType']> {
  return Object.hasOwn(registry, value);
}

export function getJobTypeConfig(jobType: string | undefined): JobTypeConfig {
  return registry[jobType ?? ''] ?? fallback;
}

/** Job type filter choices, labelled exactly as job rows are. */
export const jobTypeOptions = Object.entries(registry).map(([value, config]) => ({
  value,
  label: config.label,
}));

export function visibleJobTypeOptions(isAdmin: boolean) {
  return jobTypeOptions.filter((option) => isAdmin || option.value !== 'media-reconciliation');
}

// Maps the backend JobErrorReason enum names to human-readable labels.
const JOB_ERROR_REASON_LABELS: Record<string, string> = {
  ProviderError: 'provider error',
  ProcessingError: 'processing error',
  Unknown: 'error',
};

export function formatJobErrorReason(reason: string | undefined): string {
  return JOB_ERROR_REASON_LABELS[reason ?? 'Unknown'] ?? 'error';
}

/** Distinct failure-reason labels across a job's errors, e.g. "provider error". */
export function summarizeFailureReasons(
  errors: ReadonlyArray<{
    reason?: string;
  }>,
): string {
  const distinct = [
    ...new Set(errors.map((error) => formatJobErrorReason(error.reason))),
  ];
  return distinct.join(', ');
}

export const jobOutcomes = {
  queued: {
    label: 'Queued',
    color: 'blue',
  },
  running: {
    label: 'Running',
    color: 'blue',
  },
  completed: {
    label: 'Completed',
    color: 'teal',
  },
  partial: {
    label: 'Completed with errors',
    color: 'orange',
  },
  failed: {
    label: 'Failed',
    color: 'red',
  },
  cancelled: {
    label: 'Cancelled',
    color: 'gray',
  },
  deferred: {
    label: 'Deferred',
    color: 'gray',
  },
  unknown: {
    label: 'Unknown',
    color: 'gray',
  },
  superseded: {
    label: 'Superseded',
    color: 'gray',
  },
} as const;

export type JobOutcome = keyof typeof jobOutcomes;

export function isJobOutcome(value: string): value is JobOutcome {
  return Object.hasOwn(jobOutcomes, value);
}

export function getJobOutcome(job: Job): JobOutcome {
  if (!job.isTerminal && job.nextAttemptAt && job.jobType === 'media-reconciliation')
    return 'deferred';
  if (!job.isTerminal) return job.phase === 'Pending' ? 'queued' : 'running';
  switch (job.phase) {
    case 'Cancelled':
      return 'cancelled';
    case 'Deferred':
      return 'deferred';
    case 'Failed':
      return 'failed';
    case 'Superseded':
      return 'superseded';
    case 'CompletedWithErrors':
      return 'partial';
    case 'Completed':
      return job.hasErrors ? 'partial' : 'completed';
    default:
      return 'unknown';
  }
}

// Jobs created before the factories captured a real subject stored these literal
// labels in sourceFilename. Treat them as "no name" so legacy rows degrade to the
// generic title instead of reading "Materialize Library Materialization".
const LEGACY_MATERIALIZATION_SUBJECT = 'Library Materialization';
const LEGACY_BULK_ENRICHMENT_SUBJECT = 'Bulk Enrichment';

function jobSubject(sourceFilename: string | undefined, legacyLabel: string): string | null {
  if (!sourceFilename || sourceFilename === legacyLabel) return null;
  return sourceFilename;
}

/**
 * A job's display title is always verb + scope. `sourceFilename` carries the
 * subject: upload filename, enrichment title name, replace_dat DAT filename,
 * materialization library name, and bulk-enrichment platform short name. Export
 * has no name subject, so it derives scope from its file count; bulk convergence
 * takes its scope from `systemKey`, which is absent for all-media runs.
 */
export function getJobTitle(job: Job): string {
  switch (job.jobType) {
    case 'media-reconciliation':
      return job.selection
        ? `Reconcile media${job.selection.systemKey ? ` · ${job.selection.systemKey}` : ''}`
        : `Reconcile ${job.sourceFilename || 'media'}`;
    case 'artwork-import':
      return job.sourceFilename ? `Import artwork · ${job.sourceFilename}` : 'Import artwork';
    case 'upload':
      return job.sourceFilename ? `Import ${job.sourceFilename}` : 'Import files';
    case 'replace_dat':
      return job.sourceFilename ? `Replace DAT · ${job.sourceFilename}` : 'Replace DAT';
    case 'enrichment':
      return job.sourceFilename ? `Enrich ${job.sourceFilename}` : 'Enrich title';
    case 'metadata-rematerialization':
      return job.sourceFilename ? `Update metadata · ${job.sourceFilename}` : 'Update metadata';
    case 'artwork-completion': {
      // Persisted v1 events can omit mode; unknown history keeps the neutral run label.
      const count = Number(job.total ?? 0);
      if (job.mode === 'Enrich')
        return count > 0
          ? `Enrich ${count.toLocaleString()} ${count === 1 ? 'title' : 'titles'}`
          : 'Enrich titles';
      if (job.mode === 'Artwork')
        return count > 0
          ? `Fill artwork · ${count.toLocaleString()} ${count === 1 ? 'title' : 'titles'}`
          : 'Fill artwork';
      return count > 0
        ? `Enrichment run · ${count.toLocaleString()} ${count === 1 ? 'title' : 'titles'}`
        : 'Enrichment run';
    }
    case 'bulk_enrichment': {
      const count = Number(job.totalTitles ?? 0);
      const base = count > 0 ? `Enrich ${count.toLocaleString()} titles` : 'Enrich titles';
      const platform = jobSubject(job.sourceFilename, LEGACY_BULK_ENRICHMENT_SUBJECT);
      return platform ? `${base} · ${platform}` : base;
    }
    case 'export': {
      const files = Number(job.totalFiles ?? 0);
      return files > 0 ? `Export ${files.toLocaleString()} files` : 'Export';
    }
    case 'materialization': {
      const library = jobSubject(job.sourceFilename, LEGACY_MATERIALIZATION_SUBJECT);
      return library ? `Materialize ${library}` : 'Materialize library';
    }
    default:
      return job.sourceFilename ?? 'Job';
  }
}

export function mediaReconciliationOutcome(outcome: string): string {
  return (
    (
      {
        Completed: 'Completed',
        CompletedWithErrors: 'Completed with errors',
        Failed: 'Failed',
        Cancelled: 'Cancelled',
        Superseded: 'Superseded',
      } as Record<string, string>
    )[outcome] ?? outcome
  );
}

export function mediaReconciliationSummary(job: Job): string | null {
  if (job.jobType !== 'media-reconciliation') return null;
  if (job.selection) {
    const selected = job.selection;
    return `${selected.completed} / ${selected.created + selected.adopted} finished · ${selected.failed} failed · ${selected.skipped} skipped`;
  }
  return job.outcome ? mediaReconciliationOutcome(job.outcome) : null;
}
