import type { BatchDeleteResponse as ApiBatchDeleteResponse } from '@romd/admin-api-client';
import { batchDeleteRoms } from '@romd/admin-api-client';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { romKeys } from './useRoms';

export interface BatchDeleteRequest {
  romIds: string[];
}

export interface BatchDeleteResponse {
  deletedCount: number;
  failedCount: number;
  errors: string[];
}

const mapBatchDeleteResponse = (response: ApiBatchDeleteResponse): BatchDeleteResponse => ({
  deletedCount: response.deletedCount,
  failedCount: response.failedCount,
  errors: response.errors,
});

export function useBatchDeleteRoms() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (request: BatchDeleteRequest): Promise<BatchDeleteResponse> => {
      const response = await batchDeleteRoms({ body: request });
      if (response.error) {
        throw new Error('Failed to delete ROMs');
      }

      if (!response.data) {
        throw new Error('Failed to delete ROMs');
      }

      return mapBatchDeleteResponse(response.data as ApiBatchDeleteResponse);
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: romKeys.all });
    },
  });
}

export default { useBatchDeleteRoms };
