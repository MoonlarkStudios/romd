import type { RatingBoard, RatingBoardCatalogResponse, TitleDetail } from '@romd/admin-api-client';
import { getRatingBoards, setTitleContentRating } from '@romd/admin-api-client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { catalogSearchKeys } from './useCatalogSearch';
import { titleDetailKeys } from './useTitleDetail';
import { titleEnrichmentKeys } from './useTitleEnrichment';

export const ratingBoardKeys = {
  all: ['ratingBoards'] as const,
};

/**
 * Fetches the canonical content rating board catalog (boards and their valid categories).
 * Reference data — cached indefinitely.
 */
export function useRatingBoardCatalog() {
  return useQuery({
    queryKey: ratingBoardKeys.all,
    queryFn: async () => {
      const response = await getRatingBoards();
      if (response.error) {
        throw new Error('Failed to fetch rating boards');
      }
      return response.data as RatingBoardCatalogResponse;
    },
    staleTime: Number.POSITIVE_INFINITY,
  });
}

interface SetContentRatingParams {
  titleId: string;
  board: RatingBoard;
  /** Canonical code to set, or null to clear the user override for the board. */
  code: string | null;
}

/**
 * Hook to set or clear the user content rating override for a single board.
 * Writes a user-layer claim that wins the cascade for its board and flows through
 * the same library parental-control enforcement.
 */
export function useSetTitleContentRating() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async ({ titleId, board, code }: SetContentRatingParams) => {
      const response = await setTitleContentRating({
        path: { titleId },
        body: { board, code },
      });
      if (response.error) {
        throw new Error('Failed to update content rating');
      }
      return response.data as TitleDetail;
    },
    onSuccess: async (data, { titleId }) => {
      queryClient.setQueryData(titleDetailKeys.detail(titleId), data);
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: titleEnrichmentKeys.detail(titleId) }),
        queryClient.invalidateQueries({ queryKey: catalogSearchKeys.all }),
      ]);
    },
  });
}
