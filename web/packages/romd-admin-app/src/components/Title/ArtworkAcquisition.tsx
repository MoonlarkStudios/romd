import { Alert, Anchor, Button, Group, Loader, Stack, Text } from '@mantine/core';
import { IconPhotoPlus } from '@tabler/icons-react';
import { useQueryClient } from '@tanstack/react-query';
import { useEffect } from 'react';
import {
  useArtworkAcquisition,
  useFillMissingArtwork,
} from '../../hooks/api/useArtworkAcquisition';
import { titleDetailKeys } from '../../hooks/api/useTitleDetail';
import { artworkRoleLabel, artworkSourceLabel } from '../artworkPresentation';
import { workspaceActionProps } from '../Workspace/workspaceActions';

const labels: Record<string, string> = {
  LeftEmpty: 'Left empty by you',
  Unlinked: 'Provider explicitly unlinked',
  ProviderUnconfigured: 'Provider configuration required',
  Updated: 'Automatically selected',
  AlreadyPresent: 'Existing artwork preserved',
  Disabled: 'Automatic acquisition disabled',
  ProviderDisabled: 'No automatic provider enabled',
  NeedsMatch: 'Provider match needs review',
  Failed: 'Acquisition failed',
  Unavailable: 'No suitable artwork found',
  NeedsReview: 'Backdrop candidates ready for review',
  Superseded: 'Curation changed; acquisition skipped',
};

export function ArtworkAcquisition({
  titleId,
  onReviewBackdrop,
  resolvedRoles = [],
  disabled = false,
}: {
  titleId: string;
  onReviewBackdrop?: () => void;
  resolvedRoles?: string[];
  disabled?: boolean;
}) {
  const query = useArtworkAcquisition(titleId);
  const fill = useFillMissingArtwork();
  const cache = useQueryClient();
  const fingerprint = JSON.stringify(query.data);
  useEffect(() => {
    if (!fingerprint) return;
    void cache.invalidateQueries({
      queryKey: [
        'enrichment-workspace',
      ],
    });
    void cache.invalidateQueries({
      queryKey: titleDetailKeys.detail(titleId),
    });
    void cache.invalidateQueries({
      queryKey: [
        'artwork',
        'saved',
        titleId,
      ],
    });
    void cache.invalidateQueries({
      queryKey: [
        'artwork',
        'selections',
        titleId,
      ],
    });
    void cache.invalidateQueries({
      queryKey: [
        'catalogSearch',
      ],
    });
  }, [
    fingerprint,
    cache,
    titleId,
  ]);
  const needsBackdropReview =
    query.data?.outcomes.some(
      (result) => result.role === 'Backdrop' && result.status === 'NeedsReview',
    ) && !resolvedRoles.includes('Backdrop');
  return (
    <Stack gap="xs">
      <Group justify="space-between">
        <Group>
          <Button
            {...workspaceActionProps}
            leftSection={<IconPhotoPlus size={16} />}
            loading={fill.isPending}
            disabled={disabled || !!query.data?.activeJobId || query.isLoading || query.isError}
            onClick={() =>
              fill.mutate([
                titleId,
              ])
            }
          >
            Fill missing artwork
          </Button>
          {needsBackdropReview && onReviewBackdrop && (
            <Button
              variant="light"
              disabled={disabled}
              onClick={onReviewBackdrop}
            >
              Review backdrop
            </Button>
          )}
        </Group>
        {!!query.data?.outcomes.length && (
          <details>
            <summary>Last automatic run</summary>
            <Stack
              mt="sm"
              aria-label="Last artwork acquisition results"
            >
              <Text
                size="xs"
                c="dimmed"
              >
                Historical results. Your current choices are shown below.
              </Text>
              {query.data.outcomes.map((result) => (
                <Group
                  key={result.role}
                  gap="sm"
                >
                  <Text
                    size="sm"
                    fw={600}
                  >
                    {artworkRoleLabel(result.role)}
                  </Text>
                  <Text size="sm">
                    {result.status === 'Updated' && result.sourceId
                      ? `Automatically selected from ${artworkSourceLabel(result.sourceId)}`
                      : (labels[result.status] ?? result.status)}
                  </Text>
                  <Text
                    component="time"
                    dateTime={result.updatedAt}
                    size="xs"
                    c="dimmed"
                  >
                    {new Date(result.updatedAt).toLocaleString()}
                  </Text>
                </Group>
              ))}
            </Stack>
          </details>
        )}
      </Group>
      {query.data?.activeJobId && (
        <Group gap="xs">
          <Loader size="xs" />
          <Text size="sm">Finding missing artwork</Text>
          <Anchor
            size="sm"
            href={`/jobs/${query.data.activeJobId}`}
          >
            View job
          </Anchor>
        </Group>
      )}
      {query.isError && (
        <Alert color="red">
          {query.error.message}
          <Button
            variant="subtle"
            onClick={() => void query.refetch()}
          >
            Retry
          </Button>
        </Alert>
      )}
      {!!fill.data?.failed.length && (
        <Alert color="red">Could not queue artwork acquisition. Try again.</Alert>
      )}
    </Stack>
  );
}
