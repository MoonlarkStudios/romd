import { Alert, Button, Group, Skeleton, Stack, Text, Title } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import type { Dat } from '@romd/admin-api-client';
import { IconInboxOff, IconTrash } from '@tabler/icons-react';
import { useState } from 'react';
import { Link, useSearchParams } from 'react-router';
import { AsyncBoundary } from '../../components/AsyncBoundary';
import { EmptyState } from '../../components/EmptyState';
import { importReceiptReturn } from '../../components/Import/importReceiptLinks';
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
  const [params, setParams] = useSearchParams();
  const datId = params.get('dat');
  const receipt = importReceiptReturn(params);
  const { canManageTitles } = usePermissions();
  const unroutedQuery = useUnroutedDats();
  const platformsQuery = usePlatforms();
  const statsQuery = useLibraryStats();
  const purgeMutation = usePurgeUnidentifiedRoms();

  const [newSystemTarget, setNewSystemTarget] = useState<Dat | null>(null);
  const [newSystemRememberAlias, setNewSystemRememberAlias] = useState(true);

  const unidentifiedCount = Number(statsQuery.data?.unidentifiedCount ?? 0);
  const unroutedCount = Number(statsQuery.data?.unroutedCount ?? 0);
  const unroutedDats = unroutedQuery.data ?? [];
  const targetDats = datId ? unroutedDats.filter((dat) => dat.id === datId) : unroutedDats;

  const isLoaded = unroutedQuery.isSuccess && statsQuery.isSuccess;
  const allCaughtUp =
    isLoaded &&
    !datId &&
    unroutedDats.length === 0 &&
    unidentifiedCount === 0 &&
    unroutedCount === 0;

  const handleCreateSystem = (summary: Dat, rememberAlias: boolean) => {
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
        message:
          `Removed ${deleted.toLocaleString()} unidentified file${deleted === 1 ? '' : 's'}` +
          (reclaimed > 0
            ? `; reclaimed ${reclaimed.toLocaleString()} stored file${reclaimed === 1 ? '' : 's'}.`
            : '.'),
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
      {receipt && (
        <Button
          component={Link}
          to={receipt}
          variant="subtle"
          w="fit-content"
        >
          Back to import results
        </Button>
      )}
      {datId && (
        <Group justify="space-between">
          <Text size="sm">Catalog from this import</Text>
          <Button
            variant="subtle"
            onClick={() =>
              setParams((prior) => {
                const next = new URLSearchParams(prior);
                next.delete('dat');
                return next;
              })
            }
          >
            Show all catalogs
          </Button>
        </Group>
      )}
      {datId && unroutedQuery.isSuccess && targetDats.length === 0 && (
        <Alert color="gray">This catalog has been routed or is no longer available.</Alert>
      )}
      {!embedded && (
        <Stack gap={0}>
          <Title order={2}>Inbox</Title>
          <Text
            size="sm"
            c="dimmed"
          >
            Team-wide triage — route incoming DATs, review unidentified files
          </Text>
        </Stack>
      )}

      {allCaughtUp ? (
        <EmptyState
          icon={<IconInboxOff size={48} />}
          title="You're all caught up"
          description="Every DAT is routed and every stored file is identified. New uploads that need attention will land here."
        />
      ) : (
        <>
          <Stack gap="sm">
            <Group
              justify="space-between"
              align="baseline"
            >
              <Text
                size="xs"
                c="dimmed"
                tt="uppercase"
                fw={700}
                style={{
                  letterSpacing: '0.5px',
                }}
              >
                Unrouted DATs ({targetDats.length})
              </Text>
              {!datId && unroutedCount > 0 && (
                <Button
                  component={Link}
                  to="/roms?tab=all&status=unrouted"
                  variant="subtle"
                  size="xs"
                >
                  Review {unroutedCount.toLocaleString()} unrouted{' '}
                  {unroutedCount === 1 ? 'file' : 'files'}
                </Button>
              )}
            </Group>
            <AsyncBoundary
              query={unroutedQuery}
              loadingFallback={
                <Skeleton
                  height={120}
                  radius="md"
                />
              }
              emptyFallback={
                <EmptyState
                  title="No unrouted DATs"
                  description="Uploaded DATs that can't route themselves by header name will appear here."
                  size="sm"
                />
              }
            >
              {() => (
                <Stack gap="sm">
                  {targetDats.map((summary) => (
                    <UnroutedDatCard
                      key={summary.id}
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

          {!datId && (
            <Stack gap="sm">
              <Group
                justify="space-between"
                align="center"
              >
                <Text
                  size="xs"
                  c="dimmed"
                  tt="uppercase"
                  fw={700}
                  style={{
                    letterSpacing: '0.5px',
                  }}
                >
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
          )}
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
