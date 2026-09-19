import type { UploadAccepted } from '@romd/admin-api-client';
import { getTrackedCollectionStats, replaceDat, uploadDat, uploadRom } from '@romd/admin-api-client';
import { useMutation } from '@tanstack/react-query';
import { type UploadProgress, uploadWithProgress } from './uploadWithProgress';
import { useCreateJob } from './useCreateJob';

async function requireTrackedTitles(signal?: AbortSignal) {
  const response = await getTrackedCollectionStats({ signal });
  if (response.error || !response.data) throw new Error('Could not check tracked titles. Try again before uploading.');
  if (Number(response.data.trackedTitleCount) === 0)
    throw new Error('Track at least one title before importing tracked-title ROMs.');
}

interface UploadDatParams {
  file: File;
  systemKey?: string;
}

export function useUploadDat() {
  const { seedJob } = useCreateJob();

  return useMutation({
    mutationFn: async ({ file, systemKey }: UploadDatParams) => {
      const response = await uploadDat({
        body: { file },
        query: systemKey ? { systemKey } : undefined,
      });

      if (response.error) {
        throw new Error('Failed to upload DAT file');
      }

      return response.data as UploadAccepted;
    },
    onSuccess: (data) => {
      seedJob(data);
    },
  });
}

interface ReplaceDatParams {
  datId: string;
  file: File;
  systemKey?: string;
}

export function useReplaceDat() {
  const { seedJob } = useCreateJob();

  return useMutation({
    mutationFn: async ({ datId, file, systemKey }: ReplaceDatParams) => {
      const response = await replaceDat({
        path: { datId },
        body: { file },
        query: systemKey ? { systemKey } : undefined,
      });

      if (response.error) {
        throw new Error('Failed to replace DAT file');
      }

      return response.data as UploadAccepted;
    },
    onSuccess: (data) => {
      seedJob(data);
    },
  });
}

interface UploadGenericParams {
  requestId?: string;
  batchId?: string;
  file: File;
  systemKey?: string;
  allowUnidentified?: boolean;
  archiveOnly?: boolean;
  trackedOnly?: boolean;
  onProgress?: (progress: UploadProgress) => void;
  signal?: AbortSignal;
}

export function useUploadGeneric() {
  const { seedJob } = useCreateJob();

  return useMutation({
    // Sent via XHR (not the generated fetch client) so the caller gets upload progress.
    mutationFn: async ({ file, systemKey, allowUnidentified, archiveOnly = false, trackedOnly = false, requestId, batchId, onProgress, signal }: UploadGenericParams) => {
      if (trackedOnly) await requireTrackedTitles(signal);
      return uploadWithProgress<UploadAccepted>('/api/upload', file, {
        query: {
          requestId,
          batchId,
          systemKey,
          allowUnidentified: allowUnidentified ? 'true' : undefined,
          archiveOnly: archiveOnly ? 'true' : 'false',
          trackedOnly: trackedOnly ? 'true' : undefined,
        },
        onProgress,
        signal,
      });
    },
    onSuccess: (data) => {
      seedJob(data);
    },
  });
}

interface UploadRomParams {
  file: File;
  trackedOnly?: boolean;
}

export function useUploadRom() {
  const { seedJob } = useCreateJob();

  return useMutation({
    mutationFn: async ({ file, trackedOnly }: UploadRomParams) => {
      if (trackedOnly) await requireTrackedTitles();
      const response = await uploadRom({
        body: { file },
        query: { trackedOnly },
      });

      if (response.error) {
        throw new Error('Failed to upload ROM file');
      }

      return response.data as UploadAccepted;
    },
    onSuccess: (data) => {
      seedJob(data);
    },
  });
}
