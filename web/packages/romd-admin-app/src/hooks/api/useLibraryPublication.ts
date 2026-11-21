import { getLibraryPublicationStatus, reviewLibraryPublication } from '@romd/admin-api-client';
import { useQuery } from '@tanstack/react-query';
import { libraryManagementKeys } from './useLibraryManagement';

export function useLibraryPublicationStatus(libraryId: string) {
  return useQuery({
    queryKey: [
      ...libraryManagementKeys.all,
      'publication',
      libraryId,
    ],
    enabled: !!libraryId,
    queryFn: async ({ signal }) => {
      const result = await getLibraryPublicationStatus({
        path: {
          libraryId,
        },
        signal,
      });
      if (result.error || !result.data) throw new Error('Could not check publication status.');
      return result.data;
    },
    refetchInterval: (query) =>
      query.state.data?.latestAttempt?.isTerminal === false ? 1000 : 8000,
  });
}

export function useLibraryPublicationReview(libraryId: string) {
  return useQuery({
    queryKey: [
      ...libraryManagementKeys.all,
      'publication-review',
      libraryId,
    ],
    queryFn: async ({ signal }) => {
      const result = await reviewLibraryPublication({
        path: {
          libraryId,
        },
        signal,
      });
      if (result.error || !result.data)
        throw new Error(result.error?.detail ?? 'Could not review publication changes.');
      return result.data;
    },
    retry: false,
  });
}
