import type { JobDto } from '@romd/admin-api-client';
import { IconDownload, IconRefresh, IconReplace, IconSparkles, IconUpload } from '@tabler/icons-react';
import type { QueryKey } from '@tanstack/react-query';
import { catalogSearchKeys } from '../../hooks/api/useCatalogSearch';
import { datKeys } from '../../hooks/api/useDats';
import { romKeys } from '../../hooks/api/useRoms';
import { titleDetailKeys } from '../../hooks/api/useTitleDetail';

interface JobTypeConfig {
  icon: typeof IconUpload;
  color: string;
  label: string;
  invalidates: (job: JobDto) => QueryKey[];
}

const registry: Record<string, JobTypeConfig> = {
  upload: {
    icon: IconUpload,
    color: 'blue',
    label: 'Upload',
    invalidates: () => [romKeys.all, datKeys.all],
  },
  replace_dat: {
    icon: IconReplace,
    color: 'orange',
    label: 'DAT Replace',
    invalidates: () => [datKeys.all],
  },
  enrichment: {
    icon: IconSparkles,
    color: 'violet',
    label: 'Enrichment',
    invalidates: (job) => {
      const titleId = (job as { titleId?: string }).titleId;
      const keys: QueryKey[] = [catalogSearchKeys.all];
      if (titleId) {
        keys.push(titleDetailKeys.detail(titleId));
      }
      return keys;
    },
  },
  bulk_enrichment: {
    icon: IconSparkles,
    color: 'violet',
    label: 'Bulk Enrichment',
    invalidates: () => [catalogSearchKeys.all],
  },
  'artwork-import': {
    icon: IconUpload,
    color: 'teal',
    label: 'Artwork import',
    invalidates: (job) => job.jobType === 'artwork-import' ? [titleDetailKeys.detail(job.titleId)] : [],
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
    invalidates: () => [],
  },
};

const fallback: JobTypeConfig = {
  icon: IconUpload,
  color: 'gray',
  label: 'Job',
  invalidates: () => [],
};

export function getJobTypeConfig(jobType: string | undefined): JobTypeConfig {
  return registry[jobType ?? ''] ?? fallback;
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
export function summarizeFailureReasons(errors: ReadonlyArray<{ reason?: string }>): string {
  const distinct = [...new Set(errors.map((error) => formatJobErrorReason(error.reason)))];
  return distinct.join(', ');
}

export const jobOutcomes = {
  queued: { label: 'Queued', color: 'blue' },
  running: { label: 'Running', color: 'blue' },
  completed: { label: 'Completed', color: 'teal' },
  partial: { label: 'Completed with errors', color: 'orange' },
  failed: { label: 'Failed', color: 'red' },
  cancelled: { label: 'Cancelled', color: 'gray' },
  deferred: { label: 'Deferred', color: 'gray' },
  unknown: { label: 'Unknown', color: 'gray' },
} as const;

export type JobOutcome = keyof typeof jobOutcomes;

export function getJobOutcome(job: JobDto): JobOutcome {
  if (!job.isTerminal) return job.phase === 'Pending' ? 'queued' : 'running';
  switch (job.phase) {
    case 'Cancelled': return 'cancelled';
    case 'Deferred': return 'deferred';
    case 'Failed': return 'failed';
    case 'CompletedWithErrors': return 'partial';
    case 'Completed': return job.hasErrors ? 'partial' : 'completed';
    default: return 'unknown';
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
 * has no name subject, so it derives scope from its file count.
 */
export function getJobTitle(job: JobDto): string {
  switch (job.jobType) {
    case 'artwork-import':
      return job.sourceFilename ? `Import artwork · ${job.sourceFilename}` : 'Import artwork';
    case 'upload':
      return job.sourceFilename ? `Import ${job.sourceFilename}` : 'Import files';
    case 'replace_dat':
      return job.sourceFilename ? `Replace DAT · ${job.sourceFilename}` : 'Replace DAT';
    case 'enrichment':
      return job.sourceFilename ? `Enrich ${job.sourceFilename}` : 'Enrich title';
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

export type { JobTypeConfig };
