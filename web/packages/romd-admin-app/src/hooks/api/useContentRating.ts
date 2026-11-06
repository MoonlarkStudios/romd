import type {
  RatingBoard,
  RatingDesignation,
  ReferenceRatingDto,
  TitleDetail,
} from '@romd/admin-api-client';
import { deleteTitleContentRating, replaceTitleContentRating } from '@romd/admin-api-client';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { RATING_BOARD_ORDER } from '../../pages/TitleDetail/contentRatings';
import { useAdminCatalogSnapshot } from '../../providers/EffectiveReferenceCatalog';
import type { AssembledReferenceCatalog } from '../../utils/referenceCatalog';
import { cacheAcceptedTitle } from './titleCache';
import { ifMatch, titleWriteResult } from './titleConflict';
import { catalogSearchKeys } from './useCatalogSearch';
import { titleEnrichmentKeys } from './useTitleEnrichment';

/** One category a board can assign, as the re-rate picker offers it. */
export interface RatingCategoryOption {
  code: string;
  designation: RatingDesignation;
  minimumAge: number | null;
}

/** A rating board and the categories it can assign, youngest first with pending and refused last. */
export interface RatingBoardOption {
  board: RatingBoard;
  categories: RatingCategoryOption[];
}

const RATING_DESIGNATIONS = [
  'Rated',
  'RatingPending',
  'RefusedClassification',
] as const satisfies readonly RatingDesignation[];

function isRatingDesignation(value: string | null | undefined): value is RatingDesignation {
  return RATING_DESIGNATIONS.some((designation) => designation === value);
}

// The snapshot carries boards and designations as plain strings; only values the title write accepts are offered.
function toCategory(rating: ReferenceRatingDto): RatingCategoryOption[] {
  return !rating.retired && isRatingDesignation(rating.designation)
    ? [
        {
          code: rating.code,
          designation: rating.designation,
          minimumAge: rating.minimumAge ?? null,
        },
      ]
    : [];
}

// The order the domain catalog assigns: by minimum age, unrated designations last, then by code.
function byAgeThenCode(left: RatingCategoryOption, right: RatingCategoryOption): number {
  const leftAge = left.minimumAge ?? Number.MAX_SAFE_INTEGER;
  const rightAge = right.minimumAge ?? Number.MAX_SAFE_INTEGER;
  if (leftAge !== rightAge) return leftAge - rightAge;
  if (left.code === right.code) return 0;
  return left.code < right.code ? -1 : 1;
}

/** The re-rate picker's view of the published catalog: every board in catalog order with its categories. */
export function toRatingBoardCatalog(catalog: AssembledReferenceCatalog): RatingBoardOption[] {
  return RATING_BOARD_ORDER.filter((board) =>
    catalog.ratingBoards.some((item) => item.key === board && !item.retired),
  ).map((board) => ({
    board,
    categories: catalog.ratings
      .filter((rating) => rating.board === board)
      .flatMap(toCategory)
      .sort(byAgeThenCode),
  }));
}

/** The rating boards and their valid categories, read from the published reference catalog snapshot. */
export function useRatingBoardCatalog() {
  return useAdminCatalogSnapshot(toRatingBoardCatalog);
}

interface SetContentRatingParams {
  titleId: string;
  /** The revision of the title the curator is rating. */
  revision: string;
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
    mutationFn: async ({ titleId, revision, board, code }: SetContentRatingParams) => {
      const path = {
        titleId,
        board,
      };
      return titleWriteResult<TitleDetail>(
        code
          ? await replaceTitleContentRating({
              path,
              headers: ifMatch(revision),
              body: {
                code,
              },
            })
          : await deleteTitleContentRating({
              path,
              headers: ifMatch(revision),
            }),
        'Failed to update content rating',
      );
    },
    onSuccess: async (data, { titleId }) => {
      await cacheAcceptedTitle(queryClient, data);
      await Promise.all([
        queryClient.invalidateQueries({
          queryKey: titleEnrichmentKeys.detail(titleId),
        }),
        queryClient.invalidateQueries({
          queryKey: catalogSearchKeys.all,
        }),
      ]);
    },
  });
}
