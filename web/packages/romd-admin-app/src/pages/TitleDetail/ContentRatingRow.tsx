import { RatingMark, useReferenceCatalog } from '@romd/consumer-ui';
import '@romd/consumer-ui/styles.css';
import { Badge, Box, Group, Tooltip } from '@mantine/core';
import type { TitleContentRating } from '@romd/admin-api-client';
import {
  boardSortIndex,
  describeDesignation,
  formatContentRating,
  formatSourceLabel,
} from './contentRatings';
import { ratingPresentation } from './ratingPresentation';

interface ContentRatingRowProps {
  ratings: TitleContentRating[] | undefined;
  /** Mark height in pixels. */
  size?: number;
  /** Cap the number of marks shown; the remainder collapse into a "+N" indicator. */
  max?: number;
}

/**
 * Horizontal strip of official board rating marks, ordered by board preference.
 * Each mark sits on a light chip for legibility on dark backgrounds and falls back to a
 * text badge when no artwork exists for the (board, code) pair. When {@link max} is set,
 * marks beyond the cap collapse into a "+N" badge to keep compact placements (e.g. the hero) tidy.
 */
export function ContentRatingRow({ ratings, size = 44, max }: ContentRatingRowProps) {
  const catalog = useReferenceCatalog();
  const sorted = [...(ratings ?? [])].sort(
    (left, right) => boardSortIndex(left.board) - boardSortIndex(right.board),
  );
  if (sorted.length === 0) {
    return null;
  }

  const visible = max != null ? sorted.slice(0, max) : sorted;
  const overflow = max != null ? sorted.slice(max) : [];

  return (
    <Group gap="sm" align="center">
      {visible.map((rating) => {
        const label = formatContentRating(rating.board, rating.code);
        const tooltip = `${label} · ${describeDesignation(rating.designation, rating.minimumAge)} · ${formatSourceLabel(rating.sourceId)}`;

        return (
          <Tooltip key={`${rating.board}:${rating.code}`} label={tooltip} withArrow>
              <Box
                bg="white"
                p={4}
                style={{
                  borderRadius: 'var(--mantine-radius-sm)',
                  display: 'inline-flex',
                  lineHeight: 0,
                }}
              >
                <RatingMark {...ratingPresentation(catalog, rating)} size={size} />
              </Box>
          </Tooltip>
        );
      })}
      {overflow.length > 0 && (
        <Tooltip
          withArrow
          label={overflow.map((rating) => formatContentRating(rating.board, rating.code)).join(', ')}
        >
          <Badge variant="light" color="gray" size="lg">
            +{overflow.length}
          </Badge>
        </Tooltip>
      )}
    </Group>
  );
}
