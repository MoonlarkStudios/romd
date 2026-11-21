import { listJobs } from '@romd/admin-api-client';
import { useInfiniteQuery, useQueryClient } from '@tanstack/react-query';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useSearchParams } from 'react-router';
import { useDraft, useDraftGuard } from '../../components/Administration/DraftGuard';
import { computeTransferStats } from '../../hooks/api/uploadWithProgress';
import { useUploadGeneric } from '../../hooks/api/useUpload';
import { bundleForUpload, uploadPlanKind } from './bundle';
import { classifyFile, type FileKind, summarizeKinds } from './classify';

export interface StagedFile {
  id: string;
  file: File;
  kind: FileKind;
}
export interface ImportUploadProgress {
  phase: 'preparing' | 'uploading' | 'accepting';
  loaded: number;
  total: number;
  bytesPerSec: number;
  etaSeconds: number;
  fileIndex: number;
  fileCount: number;
  filename?: string;
}
interface PendingPlan {
  batchId: string;
  requests: {
    file: File;
    id: string;
    stagedIds: string[];
  }[];
  systemKey?: string;
  allowUnidentified: boolean;
  archiveOnly: boolean;
  trackedOnly: boolean;
}

const MAX_STAGED_FILES = 1000;
const MAX_FILE_BYTES = 64 * 1024 ** 3;

export function importFileIssue(file: File): string | undefined {
  if (file.size === 0) return 'Empty file';
  if (file.size > MAX_FILE_BYTES) return 'Larger than 64 GiB';
  return undefined;
}

export function useImportSession() {
  const queryClient = useQueryClient();
  const [params, setParams] = useSearchParams();
  const { navigateWithinEditor } = useDraftGuard();
  const rawBatch = params.get('batch');
  const selectedJob = params.get('job');
  const jobId =
    selectedJob &&
    /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(selectedJob)
      ? selectedJob
      : undefined;
  const batchId =
    rawBatch && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(rawBatch)
      ? rawBatch
      : undefined;
  const [staged, setStaged] = useState<StagedFile[]>([]);
  const [defaultPlatformId, setDefaultPlatformId] = useState<string | null>(null);
  const [allowUnidentified, setAllowUnidentified] = useState(false);
  const [trackMatchedTitles, setTrackMatchedTitles] = useState(true);
  const [trackedOnly, setTrackedOnly] = useState(false);
  const [localJobs, setLocalJobs] = useState<
    {
      id: string;
      name?: string;
    }[]
  >([]);
  const [dismissed, setDismissed] = useState<string[]>([]);
  const [isStarting, setIsStarting] = useState(false);
  const [uploadProgress, setUploadProgress] = useState<ImportUploadProgress | null>(null);
  const [uploadError, setUploadError] = useState<string | null>(null);
  const planRef = useRef<PendingPlan | null>(null);
  const busy = useRef(false);
  const abortRef = useRef<AbortController | null>(null);
  const uploadGeneric = useUploadGeneric();
  const { summary, totalBytes, invalidCount, planKind } = useMemo(() => {
    const files = staged.map(({ file }) => file);
    return {
      summary: summarizeKinds(staged),
      totalBytes: files.reduce((total, file) => total + file.size, 0),
      invalidCount: files.filter(importFileIssue).length,
      planKind: uploadPlanKind(files),
    };
  }, [
    staged,
  ]);
  const hasDats = summary.dat > 0 || summary.archive > 0;

  useDraft(
    staged.length > 0 || isStarting,
    'Files waiting to transfer will be cleared. Accepted imports continue in Jobs.',
  );
  const stagingIssue =
    staged.length > MAX_STAGED_FILES
      ? 'Choose up to 1,000 files per import.'
      : invalidCount > 0
        ? 'Remove the marked files to start the import.'
        : undefined;
  const batch = useInfiniteQuery({
    queryKey: [
      'import-batch',
      batchId,
    ],
    enabled: !!batchId,
    initialPageParam: undefined as string | undefined,
    queryFn: async ({ signal, pageParam }) => {
      if (!batchId) throw new Error('Choose an import batch first.');
      const result = await listJobs({
        query: {
          correlationId: batchId,
          jobType: 'upload',
          archive: 'all',
          limit: 25,
          cursor: pageParam,
        },
        signal,
      });
      if (result.error || !result.data || (result.data.hasNextPage && !result.data.nextCursor))
        throw new Error('Could not load this import.');
      return result.data;
    },
    getNextPageParam: (page, _, __, cursors) =>
      page.hasNextPage && page.nextCursor && !cursors.includes(page.nextCursor)
        ? page.nextCursor
        : undefined,
    refetchInterval: (query) =>
      isStarting ||
      query.state.data?.pages.some((page) => page.items.some((job) => !job.isTerminal))
        ? 2000
        : false,
  });
  const batchJobs = batch.data?.pages.flatMap((page) => page.items) ?? [];
  const jobNames = new Map(
    localJobs.map((job) => [
      job.id,
      job.name,
    ]),
  );
  for (const job of batchJobs)
    if (job.jobType === 'upload') jobNames.set(job.id, job.sourceFilename);
  const activeJobIds = Array.from(
    new Set([
      ...localJobs.map((job) => job.id),
      ...(jobId
        ? [
            jobId,
          ]
        : []),
      ...batchJobs.map((job) => job.id),
    ]),
  ).filter((id) => !dismissed.includes(id));

  useEffect(() => () => abortRef.current?.abort(), []);

  const addFiles = useCallback(
    (files: File[]) => {
      if (busy.current || planRef.current) return;
      if (!staged.length) setTrackMatchedTitles(true);
      setStaged((current) => [
        ...current,
        ...files.map((file) => ({
          id: crypto.randomUUID(),
          file,
          kind: classifyFile(file),
        })),
      ]);
      setUploadError(null);
    },
    [
      staged.length,
    ],
  );
  const clearStaged = () => {
    if (busy.current) return;
    planRef.current = null;
    setStaged([]);
    setUploadError(null);
    setTrackMatchedTitles(true);
  };
  const removeFile = (id: string) => {
    if (!busy.current && !planRef.current)
      setStaged((current) => current.filter((entry) => entry.id !== id));
  };
  const removeInvalidFiles = () => {
    if (!busy.current && !planRef.current)
      setStaged((current) => current.filter(({ file }) => !importFileIssue(file)));
  };
  const reopenJob = (id: string, name?: string) => {
    setDismissed((current) => current.filter((value) => value !== id));
    if (!batchJobs.some((job) => job.id === id)) {
      setLocalJobs((current) =>
        current.some((job) => job.id === id)
          ? current
          : [
              {
                id,
                name,
              },
              ...current,
            ],
      );
    }
    navigateWithinEditor(() =>
      setParams(
        (current) => {
          const next = new URLSearchParams(current);
          next.set('job', id);
          return next;
        },
        {
          replace: true,
        },
      ),
    );
  };

  const startImport = async () => {
    if (!staged.length || busy.current) return;
    busy.current = true;
    setIsStarting(true);
    setUploadError(null);
    const controller = new AbortController();
    abortRef.current = controller;
    setUploadProgress({
      phase: 'preparing',
      loaded: 0,
      total: 0,
      bytesPerSec: 0,
      etaSeconds: Infinity,
      fileIndex: 0,
      fileCount: 0,
    });
    try {
      if (!planRef.current) {
        if (stagingIssue) throw new Error(stagingIssue);
        const bundle = await bundleForUpload(
          staged.map((entry) => entry.file),
          controller.signal,
        );
        controller.signal.throwIfAborted();
        const id = crypto.randomUUID();
        planRef.current = {
          batchId: id,
          systemKey: hasDats ? (defaultPlatformId ?? undefined) : undefined,
          allowUnidentified: !trackedOnly && allowUnidentified,
          archiveOnly: trackedOnly || !trackMatchedTitles,
          trackedOnly,
          requests: (bundle.kind === 'individual'
            ? bundle.files
            : [
                bundle.file,
              ]
          ).map((file, index) => ({
            file,
            id: crypto.randomUUID(),
            stagedIds:
              bundle.kind === 'individual'
                ? [
                    staged[index].id,
                  ]
                : staged.map((entry) => entry.id),
          })),
        };
        setLocalJobs([]);
        navigateWithinEditor(() =>
          setParams(
            (current) => {
              const next = new URLSearchParams(current);
              next.delete('job');
              next.set('batch', id);
              return next;
            },
            {
              replace: true,
            },
          ),
        );
      }
      const plan = planRef.current;
      const count = plan.requests.length;
      let index = 0;
      while (plan.requests.length) {
        const request = plan.requests[0];
        const start = performance.now();
        index++;
        const progress = {
          phase: 'uploading' as const,
          loaded: 0,
          total: request.file.size,
          bytesPerSec: 0,
          etaSeconds: Infinity,
          fileIndex: index,
          fileCount: count,
          filename: request.file.name,
        };
        setUploadProgress(progress);
        const result = await uploadGeneric.mutateAsync({
          file: request.file,
          systemKey: plan.systemKey,
          allowUnidentified: plan.allowUnidentified,
          archiveOnly: plan.archiveOnly,
          trackedOnly: plan.trackedOnly,
          requestId: request.id,
          batchId: plan.batchId,
          signal: controller.signal,
          onProgress: ({ loaded, total }) => {
            const stats = computeTransferStats(loaded, total, (performance.now() - start) / 1000);
            setUploadProgress({
              ...progress,
              ...stats,
              loaded,
              total,
              phase: loaded >= total ? 'accepting' : 'uploading',
            });
          },
        });
        setLocalJobs((current) =>
          current.some((job) => job.id === result.jobId)
            ? current
            : [
                {
                  id: result.jobId,
                  name: request.file.name,
                },
                ...current,
              ],
        );
        setStaged((current) => current.filter((entry) => !request.stagedIds.includes(entry.id)));
        plan.requests.shift();
      }
      planRef.current = null;
      setTrackMatchedTitles(true);
    } catch (error) {
      setUploadError(
        error instanceof DOMException && error.name === 'AbortError'
          ? 'Transfer stopped. Accepted imports continue in Jobs. You can retry the remaining files.'
          : `${error instanceof Error ? error.message : 'Upload failed.'} Accepted imports continue in Jobs. You can retry the remaining files.`,
      );
    } finally {
      busy.current = false;
      setIsStarting(false);
      setUploadProgress(null);
      abortRef.current = null;
      void queryClient.invalidateQueries({
        queryKey: [
          'import-batch',
        ],
      });
    }
  };

  return {
    staged,
    stagingIssue,
    totalBytes,
    invalidCount,
    planKind,
    hasDats,
    addFiles,
    removeFile,
    removeInvalidFiles,
    clearStaged,
    defaultPlatformId,
    setDefaultPlatformId,
    allowUnidentified,
    setAllowUnidentified,
    trackedOnly,
    setTrackedOnly,
    trackMatchedTitles,
    setTrackMatchedTitles,
    startImport,
    isStarting,
    uploadProgress,
    uploadError,
    retryPending: !!planRef.current,
    cancelUpload: () => abortRef.current?.abort(),
    activeJobIds,
    selectedJobId: jobId && activeJobIds.includes(jobId) ? jobId : activeJobIds[0],
    jobNames,
    hasMoreJobs: batch.hasNextPage,
    fetchingMoreJobs: batch.isFetchingNextPage,
    loadMoreJobs: () => void batch.fetchNextPage(),
    dismissJob: (id: string) =>
      setDismissed((current) => [
        ...current,
        id,
      ]),
    reopenJob,
    batchError: batch.isFetchNextPageError ? 'next-page' : batch.isError ? 'restore' : null,
    retryBatch: () => {
      if (batch.isFetchNextPageError) void batch.fetchNextPage();
      else void batch.refetch();
    },
  };
}
