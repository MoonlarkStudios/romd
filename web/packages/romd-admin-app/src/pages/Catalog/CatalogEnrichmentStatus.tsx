import { Badge, Stack, Text, Tooltip } from '@mantine/core';
import type { CatalogTitle } from '@romd/admin-api-client';
import { titleStatuses, titleStatusLabels } from '../../components/Title/enrichmentPresentation';
import { titleStatusColors } from '../../components/Title/TitleStatusChips';
import { lastAttemptLabel } from './catalogEnrichmentFilters';

/** Current title status chips; the last attempt is history and never implies completeness. */
export function CatalogEnrichmentStatus({ title }: { title: CatalogTitle }) {
  const assessment = title.enrichmentAssessment;
  if (!assessment) return null;
  return (
    <Tooltip label={`Last attempt: ${lastAttemptLabel(title.enrichmentStatus)}`}>
      <Stack
        gap={4}
        align="flex-start"
      >
        {titleStatuses(assessment).map((status) =>
          status === 'Settled' ? (
            <Text
              key={status}
              size="xs"
              c="dimmed"
            >
              {titleStatusLabels[status]}
            </Text>
          ) : (
            <Badge
              key={status}
              size="xs"
              color={titleStatusColors[status]}
            >
              {titleStatusLabels[status]}
            </Badge>
          ),
        )}
      </Stack>
    </Tooltip>
  );
}
