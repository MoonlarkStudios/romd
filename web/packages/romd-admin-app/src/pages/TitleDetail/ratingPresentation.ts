import type { ReferenceCatalogData } from '@romd/consumer-ui';

/** The editor resolves draft classifications; consumer pages receive these facts in their resource. */
export function ratingPresentation(catalog: ReferenceCatalogData | undefined, rating: { board: string; code: string }) {
  const mark = catalog?.ratings.find(item => item.board === rating.board && item.code === rating.code);
  return { ...rating, ...mark, boardName: catalog?.ratingBoards.find(item => item.key === rating.board)?.name ?? rating.board };
}
