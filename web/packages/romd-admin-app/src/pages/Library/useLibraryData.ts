import { useMemo } from 'react';
import { type StatusFilter, useRomsList } from '../../hooks/api/useRomsList';
import { isStatusFilter, type QueueFilter } from './useLibraryFilters';

function toApiFilters(filter: QueueFilter): { status?: StatusFilter } {
  if (filter === 'all') {
    return {};
  }

  if (isStatusFilter(filter)) {
    return { status: filter.toLowerCase() as StatusFilter };
  }

  return {};
}

export function useLibraryData(filter: QueueFilter) {
  const { status } = toApiFilters(filter);
  const query = useRomsList({ status });

  const items = useMemo(() => {
    return query.data?.pages.flatMap((page) => page.items) ?? [];
  }, [query.data]);

  return {
    ...query,
    items,
    hasMore: query.hasNextPage ?? false,
    loadMore: query.fetchNextPage,
  };
}
