import { Button, Group, Skeleton, Stack, Text, Title } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import type { UnroutedDat } from '@romd/admin-api-client';
import { IconInboxOff, IconTrash } from '@tabler/icons-react';
import { useState } from 'react';
import { AsyncBoundary } from '../../components/AsyncBoundary';
import { EmptyState } from '../../components/EmptyState';
import { useUnroutedDats } from '../../hooks/api/useDats';
import { usePlatforms } from '../../hooks/api/usePlatforms';
import { useLibraryStats, usePurgeUnidentifiedRoms } from '../../hooks/api/useRoms';
import { usePermissions } from '../../hooks/usePermissions';
import { NewSystemModal } from './NewSystemModal';
import { UnidentifiedList } from './UnidentifiedList';
import { UnroutedDatCard } from './UnroutedDatCard';

/**
 * Team-wide triage tray: unrouted DATs (with the ROMs blocked behind them)
 * and unidentified files. Every item resolved here permanently teaches the
 * system — assignments can be remembered as aliases for future auto-routing.
 */
export function Inbox({ embedded = false }: { embedded?: boolean }) {
  const { canManageTitles } = usePermissions();
  const unroutedQuery = useUnroutedDats();
  const platformsQuery = usePlatforms();
  const statsQuery = useLibraryStats();
  const purgeMutation = usePurgeUnidentifiedRoms();

  const [newSystemTarget, setNewSystemTarget] = useState<UnroutedDat | null>(null);
  const [newSystemRememberAlias, setNewSystemRememberAlias] = useState(true);

  const unidentifiedCount = Number(statsQuery.data?.unidentifiedCount ?? 0);
  const unroutedDats = unroutedQuery.data ?? [];
  const totalBlockedRoms = unroutedDats.reduce(
    (sum, summary) => sum + Number(summary.matchedRomFileCount ?? 0),
    0,
  );

  const isLoaded = unroutedQuery.isSuccess && statsQuery.isSuccess;
  const allCaughtUp = isLoaded && unroutedDats.length === 0 && unidentifiedCount === 0;

  const handleCreateSystem = (summary: UnroutedDat, rememberAlias: boolean) => {
    setNewSystemRememberAlias(rememberAlias);
    setNewSystemTarget(summary);
  };

  const handlePurgeInbox = async () => {
    const confirmed = window.confirm(
      `Delete all ${unidentifiedCount.toLocaleString()} unidentified files? This cannot be undone.`,
    );
    if (!confirmed) return;
    try {
      const result = await purgeMutation.mutateAsync();
      const deleted = Number(result.deletedCount);
      const reclaimed = Number(result.reclaimedFileCount);
      notifications.show({
        title: 'Inbox cleared',
        message: `Removed ${deleted.toLocaleString()} unidentified file${deleted === 1 ? '' : 's'}` +
          (reclaimed > 0 ? `; reclaimed ${reclaimed.toLocaleString()} stored file${reclaimed === 1 ? '' : 's'}.` : '.'),
        color: 'green',
      });
    } catch {
      notifications.show({
        title: 'Clear failed',
        message: 'Failed to clear the inbox. Please try again.',
        color: 'red',
      });
    }
  };

  return (
    <Stack gap="xl">
      {!embedded && <Stack gap={0}>
        <Title order={2}>Inbox</Title>
        <Text size="sm" c="dimmed">
          Team-wide triage — route incoming DATs, review unidentified files
        </Text>
      </Stack>}

      {allCaughtUp ? (
        <EmptyState
          icon={<IconInboxOff size={48} />}
          title="You're all caught up"
          description="Every DAT is routed and every stored file is identified. New uploads that need attention will land here."
        />
      ) : (
        <>
          <Stack gap="sm">
            <Group justify="space-between" align="baseline">
              <Text size="xs" c="dimmed" tt="uppercase" fw={700} style={{ letterSpacing: '0.5px' }}>
                Unrouted DATs ({unroutedDats.length})
              </Text>
              {totalBlockedRoms > 0 && (
                <Text size="sm" fw={600} c="orange">
                  blocking {totalBlockedRoms.toLocaleString()} matched ROM{totalBlockedRoms === 1 ? '' : 's'}
                </Text>
              )}
            </Group>
            <AsyncBoundary
              query={unroutedQuery}
              loadingFallback={<Skeleton height={120} radius="md" />}
              emptyFallback={
                <EmptyState
                  title="No unrouted DATs"
                  description="Uploaded DATs that can't route themselves by header name will appear here."
                  size="sm"
                />
              }
            >
              {(summaries) => (
                <Stack gap="sm">
                  {summaries.map((summary) => (
                    <UnroutedDatCard
                      key={summary.dat.id}
                      summary={summary}
                      platforms={platformsQuery.data ?? []}
                      canRoute={canManageTitles}
                      onCreateSystem={handleCreateSystem}
                    />
                  ))}
                </Stack>
              )}
            </AsyncBoundary>
          </Stack>

          <Stack gap="sm">
            <Group justify="space-between" align="center">
              <Text size="xs" c="dimmed" tt="uppercase" fw={700} style={{ letterSpacing: '0.5px' }}>
                Unidentified files ({unidentifiedCount.toLocaleString()})
              </Text>
              {canManageTitles && unidentifiedCount > 0 && (
                <Button
                  size="xs"
                  variant="light"
                  color="red"
                  leftSection={<IconTrash size={14} />}
                  onClick={handlePurgeInbox}
                  loading={purgeMutation.isPending}
                >
                  Delete all
                </Button>
              )}
            </Group>
            <UnidentifiedList />
          </Stack>
        </>
      )}

      <NewSystemModal
        target={newSystemTarget}
        rememberAlias={newSystemRememberAlias}
        onClose={() => setNewSystemTarget(null)}
      />
    </Stack>
  );
}
