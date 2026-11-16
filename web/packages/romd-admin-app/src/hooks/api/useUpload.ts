import type { JobReference } from '@romd/admin-api-client';
import {
  getTrackedCollectionStats,
  importFromPath,
  listImportFolders,
  uploadDat,
  uploadRom,
} from '@romd/admin-api-client';
import { useMutation, useQuery } from '@tanstack/react-query';
import { apiError } from '../../components/Administration/apiError';
import { type UploadProgress, uploadWithProgress } from './uploadWithProgress';
import { useCreateJob } from './useCreateJob';

async function requireTrackedTitles(signal?: AbortSignal) {
  const response = await getTrackedCollectionStats({
    signal,
  });
  if (response.error || !response.data)
    throw new Error('Could not check tracked titles. Try again before uploading.');
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
        body: {
          file,
        },
        query: systemKey
          ? {
              systemKey,
            }
          : undefined,
      });

      if (response.error) {
        throw new Error('Failed to upload DAT file');
      }

      return response.data as JobReference;
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
    mutationFn: async ({
      file,
      systemKey,
      allowUnidentified,
      archiveOnly = false,
      trackedOnly = false,
      requestId,
      batchId,
      onProgress,
      signal,
    }: UploadGenericParams) => {
      if (trackedOnly) await requireTrackedTitles(signal);
      return uploadWithProgress<JobReference>('/api/upload', file, {
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
        body: {
          file,
        },
        query: {
          trackedOnly,
        },
      });

      if (response.error) {
        throw new Error('Failed to upload ROM file');
      }

      return response.data as JobReference;
    },
    onSuccess: (data) => {
      seedJob(data);
    },
  });
}

/**
 * One level of the server folders a path import may read: the allowed roots without a path,
 * otherwise the real subfolders of the given folder. The server refuses anything outside them.
 */
export function useImportFolders(path: string | null, enabled: boolean) {
  return useQuery({
    queryKey: [
      'import-folders',
      path,
    ],
    enabled,
    staleTime: 30_000,
    queryFn: async ({ signal }) => {
      const response = await listImportFolders({
        query: path
          ? {
              path,
            }
          : undefined,
        signal,
      });
      if (response.error || !response.data)
        throw new Error(apiError(response.error, 'Could not list server folders.'));
      return response.data;
    },
  });
}

interface ImportServerFolderParams {
  path: string;
  allowUnidentified: boolean;
  archiveOnly: boolean;
  trackedOnly: boolean;
}

/** Copies a server folder into a new import job. The source folder is never moved or changed. */
export function useImportServerFolder() {
  const { seedJob } = useCreateJob();

  return useMutation({
    mutationFn: async ({
      path,
      allowUnidentified,
      archiveOnly,
      trackedOnly,
    }: ImportServerFolderParams) => {
      if (trackedOnly) await requireTrackedTitles();
      const response = await importFromPath({
        body: {
          path,
          move: false,
          allowUnidentified,
          archiveOnly,
          trackedOnly,
        },
      });
      if (response.error || !response.data)
        throw new Error(apiError(response.error, 'Could not start the server folder import.'));
      return response.data as JobReference;
    },
    onSuccess: (data) => {
      seedJob(data);
    },
  });
}
