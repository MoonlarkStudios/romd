import { Alert, Anchor, Button, Group, Loader, Stack, Text } from '@mantine/core';
import { IconPhotoPlus } from '@tabler/icons-react';
import { useQueryClient } from '@tanstack/react-query';
import { useEffect } from 'react';
import { artworkRoleLabel, artworkSourceLabel } from '../../components/artworkPresentation';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import { useArtworkAcquisition, useFillMissingArtwork } from '../../hooks/api/useArtworkAcquisition';
import { titleDetailKeys } from '../../hooks/api/useTitleDetail';

const labels: Record<string, string> = {
  Updated: 'Automatically selected', AlreadyPresent: 'Existing artwork preserved',
  Disabled: 'Automatic acquisition disabled', ProviderDisabled: 'No automatic provider enabled',
  NeedsMatch: 'Provider match needs review', Failed: 'Acquisition failed', Unavailable: 'No suitable artwork found',
  NeedsReview: 'Backdrop candidates ready for review',
  Superseded: 'Curation changed; acquisition skipped',
};

export function ArtworkAcquisition({ titleId, onReviewBackdrop }: { titleId: string; onReviewBackdrop?: () => void }) {
  const query = useArtworkAcquisition(titleId);
  const fill = useFillMissingArtwork();
  const cache = useQueryClient();
  const fingerprint = JSON.stringify(query.data);
  useEffect(() => {
    if (!fingerprint) return;
    void cache.invalidateQueries({ queryKey: titleDetailKeys.detail(titleId) });
    void cache.invalidateQueries({ queryKey: ['artwork', 'saved', titleId] });
    void cache.invalidateQueries({ queryKey: ['artwork', 'selections', titleId] });
    void cache.invalidateQueries({ queryKey: ['catalog'] });
  }, [fingerprint, cache, titleId]);
  return <Stack gap="xs">
    <Group justify="space-between" align="flex-start">
      <Group gap="lg" align="flex-start" aria-label="Last artwork acquisition results">{query.data?.outcomes.map((result) => <Stack key={result.role} gap={2}>
        <Text size="xs" c="dimmed">{artworkRoleLabel(result.role)}</Text>
        <Text size="sm" c={result.status === 'Failed' ? 'red' : undefined}>{result.status === 'Updated' && result.sourceId ? `Automatically selected from ${artworkSourceLabel(result.sourceId)}` : labels[result.status] ?? result.status}</Text>
        {result.role === 'Backdrop' && result.status === 'NeedsReview' && onReviewBackdrop && <Button variant="subtle" size="compact-sm" onClick={onReviewBackdrop}>Review backdrop</Button>}
        <Text component="time" dateTime={result.updatedAt} size="xs" c="dimmed">{new Date(result.updatedAt).toLocaleString()}</Text>
      </Stack>)}</Group>
      <Button {...workspaceActionProps} variant="light" leftSection={<IconPhotoPlus size={16} />} loading={fill.isPending} disabled={!!query.data?.activeJobId || query.isLoading || query.isError} onClick={() => fill.mutate([titleId])}>Fill missing artwork</Button>
    </Group>
    {query.data?.activeJobId && <Group gap="xs"><Loader size="xs" /><Text size="sm">Enrichment in progress</Text><Anchor size="sm" href={`/jobs/${query.data.activeJobId}`}>View job</Anchor></Group>}
    {query.isError && <Alert color="red">{query.error.message}<Button variant="subtle" onClick={() => void query.refetch()}>Retry</Button></Alert>}
    {!!fill.data?.failed.length && <Alert color="red">Could not queue artwork acquisition. Try again.</Alert>}
  </Stack>;
}
