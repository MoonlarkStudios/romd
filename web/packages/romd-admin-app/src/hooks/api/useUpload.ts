import type { UploadAccepted } from '@romd/admin-api-client';
import { replaceDat, uploadDat, uploadRom } from '@romd/admin-api-client';
import { useMutation } from '@tanstack/react-query';
import { type UploadProgress, uploadWithProgress } from './uploadWithProgress';
import { useCreateJob } from './useCreateJob';

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
  onProgress?: (progress: UploadProgress) => void;
  signal?: AbortSignal;
}

export function useUploadGeneric() {
  const { seedJob } = useCreateJob();

  return useMutation({
    // Sent via XHR (not the generated fetch client) so the caller gets upload progress.
    mutationFn: ({ file, systemKey, allowUnidentified, archiveOnly = false, requestId, batchId, onProgress, signal }: UploadGenericParams) =>
      uploadWithProgress<UploadAccepted>('/api/upload', file, {
        query: {
          requestId,
          batchId,
          systemKey,
          allowUnidentified: allowUnidentified ? 'true' : undefined,
          archiveOnly: archiveOnly ? 'true' : 'false',
        },
        onProgress,
        signal,
      }),
    onSuccess: (data) => {
      seedJob(data);
    },
  });
}

interface UploadRomParams {
  file: File;
}

export function useUploadRom() {
  const { seedJob } = useCreateJob();

  return useMutation({
    mutationFn: async ({ file }: UploadRomParams) => {
      const response = await uploadRom({
        body: { file },
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
