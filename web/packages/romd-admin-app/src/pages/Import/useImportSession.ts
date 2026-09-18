import { getImportBatch } from '@romd/admin-api-client';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useSearchParams } from 'react-router';
import { computeTransferStats } from '../../hooks/api/uploadWithProgress';
import { useUploadGeneric } from '../../hooks/api/useUpload';
import { bundleForUpload } from './bundle';
import { classifyFile, type FileKind, summarizeKinds } from './classify';

export interface StagedFile { id: string; file: File; kind: FileKind; }
export interface ImportUploadProgress {
  phase: 'preparing' | 'uploading' | 'accepting';
  loaded: number; total: number; bytesPerSec: number; etaSeconds: number;
  fileIndex: number; fileCount: number; filename?: string;
}
interface PendingPlan {
  batchId: string;
  requests: { file: File; id: string; stagedIds: string[] }[];
  systemKey?: string;
  allowUnidentified: boolean;
  archiveOnly: boolean;
}

export function useImportSession() {
  const queryClient = useQueryClient();
  const [params, setParams] = useSearchParams();
  const rawBatch = params.get('batch');
  const selectedJob = params.get('job');
  const jobId = selectedJob && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(selectedJob) ? selectedJob : undefined;
  const batchId = rawBatch && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(rawBatch) ? rawBatch : undefined;
  const [staged, setStaged] = useState<StagedFile[]>([]);
  const [defaultPlatformId, setDefaultPlatformId] = useState<string | null>(null);
  const [allowUnidentified, setAllowUnidentified] = useState(false);
  const [trackMatchedTitles, setTrackMatchedTitles] = useState(true);
  const [localIds, setLocalIds] = useState<string[]>([]);
  const [dismissed, setDismissed] = useState<string[]>([]);
  const [isStarting, setIsStarting] = useState(false);
  const [uploadProgress, setUploadProgress] = useState<ImportUploadProgress | null>(null);
  const [uploadError, setUploadError] = useState<string | null>(null);
  const planRef = useRef<PendingPlan | null>(null);
  const busy = useRef(false);
  const abortRef = useRef<AbortController | null>(null);
  const uploadGeneric = useUploadGeneric();
  const summary = useMemo(() => summarizeKinds(staged), [staged]);
  const hasDats = summary.dat > 0 || summary.archive > 0;

  const batch = useQuery({
    queryKey: ['import-batch', batchId], enabled: !!batchId,
    queryFn: async ({ signal }) => {
      const result = await getImportBatch({ path: { batchId: batchId! }, signal });
      if (result.error || !result.data) throw new Error('Could not load this import.');
      return result.data;
    },
    refetchInterval: (query) => isStarting || query.state.data?.some((job) => !job.isTerminal) ? 2000 : false,
  });
  const activeJobIds = Array.from(new Set([...localIds, ...(jobId ? [jobId] : []), ...(batch.data ?? []).map((job) => job.id)]))
    .filter((id) => !dismissed.includes(id));

  useEffect(() => {
    const preventLeave = (event: BeforeUnloadEvent) => { event.preventDefault(); };
    if (isStarting) window.addEventListener('beforeunload', preventLeave);
    return () => window.removeEventListener('beforeunload', preventLeave);
  }, [isStarting]);
  useEffect(() => () => abortRef.current?.abort(), []);

  const addFiles = useCallback((files: File[]) => {
    if (busy.current || planRef.current) return;
    if (!staged.length) setTrackMatchedTitles(true);
    setStaged((current) => [...current, ...files.map((file) => ({ id: crypto.randomUUID(), file, kind: classifyFile(file) }))]);
    setUploadError(null);
  }, [staged.length]);
  const clearStaged = () => {
    if (busy.current) return;
    planRef.current = null;
    setStaged([]); setUploadError(null); setTrackMatchedTitles(true);
  };
  const removeFile = (id: string) => {
    if (!busy.current && !planRef.current) setStaged((current) => current.filter((entry) => entry.id !== id));
  };
  const reopenJob = (id: string) => {
    setDismissed((current) => current.filter((value) => value !== id));
    setLocalIds((current) => [...new Set([id, ...current])]);
    setParams((current) => { const next = new URLSearchParams(current); next.set('job', id); return next; }, { replace: true });
  };

  const startImport = async () => {
    if (!staged.length || busy.current) return;
    busy.current = true;
    setIsStarting(true); setUploadError(null);
    const controller = new AbortController(); abortRef.current = controller;
    setUploadProgress({ phase: 'preparing', loaded: 0, total: 0, bytesPerSec: 0, etaSeconds: Infinity, fileIndex: 0, fileCount: 0 });
    try {
      if (!planRef.current) {
        if (staged.length > 1000 || staged.some(({ file }) => file.size === 0 || file.size > 10 * 1024 ** 3))
          throw new Error('Choose up to 1,000 non-empty files, each no larger than 10 GB.');
        const bundle = await bundleForUpload(staged.map((entry) => entry.file), controller.signal);
        controller.signal.throwIfAborted();
        const id = crypto.randomUUID();
        planRef.current = {
          batchId: id, systemKey: hasDats ? defaultPlatformId ?? undefined : undefined,
          allowUnidentified, archiveOnly: !trackMatchedTitles,
          requests: (bundle.kind === 'individual' ? bundle.files : [bundle.file]).map((file, index) => ({
            file, id: crypto.randomUUID(), stagedIds: bundle.kind === 'individual' ? [staged[index].id] : staged.map((entry) => entry.id),
          })),
        };
        setParams((current) => { const next = new URLSearchParams(current); next.set('batch', id); return next; }, { replace: true });
      }
      const plan = planRef.current;
      const count = plan.requests.length;
      let index = 0;
      while (plan.requests.length) {
        const request = plan.requests[0];
        const start = performance.now(); index++;
        const progress = { phase: 'uploading' as const, loaded: 0, total: request.file.size, bytesPerSec: 0, etaSeconds: Infinity, fileIndex: index, fileCount: count, filename: request.file.name };
        setUploadProgress(progress);
        const result = await uploadGeneric.mutateAsync({
          file: request.file, systemKey: plan.systemKey, allowUnidentified: plan.allowUnidentified,
          archiveOnly: plan.archiveOnly, requestId: request.id, batchId: plan.batchId, signal: controller.signal,
          onProgress: ({ loaded, total }) => {
            const stats = computeTransferStats(loaded, total, (performance.now() - start) / 1000);
            setUploadProgress({ ...progress, ...stats, loaded, total, phase: loaded >= total ? 'accepting' : 'uploading' });
          },
        });
        setLocalIds((current) => [...new Set([result.jobId, ...current])]);
        setStaged((current) => current.filter((entry) => !request.stagedIds.includes(entry.id)));
        plan.requests.shift();
      }
      planRef.current = null; setTrackMatchedTitles(true);
    } catch (error) {
      setUploadError(error instanceof DOMException && error.name === 'AbortError'
        ? 'Transfer stopped. Accepted imports continue processing. Retry restarts the remaining transfer.'
        : `${error instanceof Error ? error.message : 'Upload failed.'} Accepted imports are preserved. Retry uses the same request identity.`);
    } finally {
      busy.current = false; setIsStarting(false); setUploadProgress(null); abortRef.current = null;
      void queryClient.invalidateQueries({ queryKey: ['import-batch'] });
    }
  };

  return {
    staged, summary, hasDats, addFiles, removeFile, clearStaged,
    defaultPlatformId, setDefaultPlatformId, allowUnidentified, setAllowUnidentified,
    trackMatchedTitles, setTrackMatchedTitles, startImport, isStarting, uploadProgress, uploadError,
    retryPending: !!planRef.current, cancelUpload: () => abortRef.current?.abort(),
    activeJobIds, dismissJob: (id: string) => setDismissed((current) => [...current, id]), reopenJob,
    batchError: batch.isError, retryBatch: () => void batch.refetch(),
  };
}
