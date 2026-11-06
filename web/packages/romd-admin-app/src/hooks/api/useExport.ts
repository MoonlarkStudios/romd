import {
  createDownloadTicket,
  type ExportLibraryRequest,
  exportLibrary,
} from '@romd/admin-api-client';
import { startBrowserDownload } from '@romd/delivery';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { jobKeys } from './useJobs';

interface ExportLibraryParams {
  libraryId?: string;
  format?: ExportLibraryRequest['format'];
}

export function useExportLibrary() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (params?: ExportLibraryParams) => {
      const response = await exportLibrary({
        body: {
          libraryId: params?.libraryId,
          format: params?.format ?? 'original',
        },
      });

      const data = response.data;
      if (response.error || !data || typeof data.jobId !== 'string' || !data.jobId)
        throw new Error('Failed to start export');
      return data;
    },
    onSuccess: async () => {
      await queryClient.invalidateQueries({
        queryKey: jobKeys.list(),
      });
    },
  });
}

export async function downloadExportFile(jobId: string): Promise<void> {
  const response = await createDownloadTicket({
    body: {
      target: 'ExportJob',
      id: jobId,
    },
  });
  if (response.error || !response.data) throw new Error('Download unavailable');
  startBrowserDownload(response.data.downloadUrl);
}
