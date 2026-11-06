import { Badge, Group, type MantineColor, Tooltip, VisuallyHidden } from '@mantine/core';
import type { EnrichmentAssessmentDto } from '@romd/admin-api-client';
import { useId } from 'react';
import {
  settledExplanation,
  type TitleStatus,
  titleStatuses,
  titleStatusLabels,
} from './enrichmentPresentation';

export const titleStatusColors: Record<TitleStatus, MantineColor> = {
  NeedsReview: 'orange',
  Blocked: 'red',
  Processing: 'blue',
  Automatic: 'gray',
  Settled: 'teal',
};

export type TitleStatusFlags = Pick<
  EnrichmentAssessmentDto,
  'needsReview' | 'hasOperationalBlocker' | 'hasActiveWork' | 'canEnrichAutomatically'
>;

/**
 * The title's current status flags in their fixed order; render only with a loaded assessment so Settled is never
 * implied. `describeSettled` gives the Settled chip what Settled means, for headers that show it once per title.
 */
export function TitleStatusChips({
  flags,
  label = 'Enrichment status',
  describeSettled = false,
}: {
  flags: TitleStatusFlags;
  label?: string;
  describeSettled?: boolean;
}) {
  const explanationId = useId();
  const statuses = titleStatuses(flags);
  const described = describeSettled && statuses[0] === 'Settled';
  return (
    <>
      <Group
        gap={6}
        role="group"
        aria-label={label}
      >
        {statuses.map((status) => {
          const chip = (
            <Badge
              key={status}
              variant="light"
              size="sm"
              color={titleStatusColors[status]}
              aria-describedby={described ? explanationId : undefined}
            >
              {titleStatusLabels[status]}
            </Badge>
          );
          return described ? (
            <Tooltip
              key={status}
              label={settledExplanation}
              multiline
              maw={280}
            >
              {chip}
            </Tooltip>
          ) : (
            chip
          );
        })}
      </Group>
      {described && <VisuallyHidden id={explanationId}>{settledExplanation}</VisuallyHidden>}
    </>
  );
}
