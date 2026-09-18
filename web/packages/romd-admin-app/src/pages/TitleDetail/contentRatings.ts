import type { RatingBoard, RatingDesignation } from '@romd/admin-api-client';
import { formatRatingLabel, ratingBoards } from '@romd/foundation/catalog';

/** Catalog order is checked against the backend's default board preference. */
export const RATING_BOARD_ORDER: readonly RatingBoard[] = ratingBoards.map(item => item.key);
export const RATING_BOARD_LABELS = Object.fromEntries(ratingBoards.map(item => [item.key, item.label])) as Record<RatingBoard, string>;
export function boardLabel(board: RatingBoard): string { return RATING_BOARD_LABELS[board] ?? `Board ${board}`; }
export function boardSortIndex(board: RatingBoard): number {
  const index = RATING_BOARD_ORDER.indexOf(board);
  return index < 0 ? RATING_BOARD_ORDER.length : index;
}
export const formatContentRating = formatRatingLabel;

/** Human-readable summary of a rating's age/designation. */
export function describeDesignation(
  designation: RatingDesignation,
  minimumAge: number | string | null | undefined,
): string {
  switch (designation) {
    case 'RatingPending':
      return 'Pending classification';
    case 'RefusedClassification':
      return 'Refused classification';
    default: {
      const age = minimumAge === null || minimumAge === undefined ? null : Number(minimumAge);
      return age === null ? 'Rated' : age === 0 ? 'All ages' : `Ages ${age}+`;
    }
  }
}

/** Mantine color for a rating's provenance source badge. */
export function ratingSourceColor(sourceId: string | null | undefined): string {
  if (sourceId?.toLowerCase() === 'user') return 'green';
  if (sourceId?.toLowerCase() === 'igdb') return 'violet';
  return sourceId ? 'blue' : 'gray';
}

export function formatSourceLabel(sourceId: string | null | undefined): string {
  if (!sourceId) return 'None';
  if (sourceId.toLowerCase() === 'user') return 'User';
  if (sourceId.toLowerCase() === 'igdb') return 'IGDB';
  return sourceId;
}
