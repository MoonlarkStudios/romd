import { exportLibrary } from '@romd/admin-api-client';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { authenticatedDownload } from '../../utils/download';
import { jobKeys } from './useJobs';

interface ExportLibraryParams {
  libraryId?: string;
}

export function useExportLibrary() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (params?: ExportLibraryParams) => {
      const response = await exportLibrary({
        body: params?.libraryId ? { libraryId: params.libraryId } : null,
      });

      if (response.error) {
        throw new Error('Failed to start export');
      }

      return response.data as { jobId: string };
    },
    onSuccess: () => {
      setTimeout(() => {
        queryClient.invalidateQueries({ queryKey: jobKeys.list() });
      }, 500);
    },
  });
}

export function downloadExportFile(jobId: string): Promise<void> {
  return authenticatedDownload(`/api/export/${jobId}/download`, 'romd-export.zip');
}
