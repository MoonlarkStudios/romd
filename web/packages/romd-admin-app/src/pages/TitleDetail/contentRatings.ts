import type { RatingBoard, RatingDesignation } from '@romd/admin-api-client';
import { formatRatingLabel, ratingBoards } from '@romd/foundation/catalog';

/** Catalog order is checked against the backend's default board preference. */
export const RATING_BOARD_ORDER: readonly RatingBoard[] = ratingBoards.map((item) => item.key);
export const RATING_BOARD_LABELS = Object.fromEntries(
  ratingBoards.map((item) => [
    item.key,
    item.label,
  ]),
) as Record<RatingBoard, string>;
export function boardLabel(board: RatingBoard): string {
  return RATING_BOARD_LABELS[board] ?? `Board ${board}`;
}
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

const boardRegions: Record<RatingBoard, string> = {
  Esrb: 'North America',
  Pegi: 'Europe',
  Cero: 'Japan',
  Usk: 'Germany',
  Grac: 'South Korea',
  ClassInd: 'Brazil',
  Acb: 'Australia',
};

export function boardRegion(board: RatingBoard): string {
  return boardRegions[board] ?? '—';
}

export function isManualRating(sourceId: string | undefined): boolean {
  return sourceId?.toLowerCase() === 'user';
}

export function ratingSourceLabel(sourceId: string | undefined): string {
  if (!sourceId) return 'Manual';
  if (isManualRating(sourceId)) return 'Manual';
  if (sourceId.toLowerCase() === 'igdb') return 'IGDB';
  return sourceId;
}
