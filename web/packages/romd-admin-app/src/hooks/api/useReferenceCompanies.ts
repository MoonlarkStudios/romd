import { listAdminCompanies } from '@romd/admin-api-client';
import { useQuery } from '@tanstack/react-query';
import { collectPages } from '../../utils/collectPages';

export function useReferenceCompanies(enabled: boolean) {
  return useQuery({
    queryKey: [
      'reference-companies',
    ],
    enabled,
    queryFn: async ({ signal }) =>
      collectPages(async (cursor) => {
        signal.throwIfAborted();
        const response = await listAdminCompanies({
          signal,
          query: {
            cursor,
            limit: 100,
          },
          throwOnError: true,
        });
        return response.data;
      }),
  });
}
