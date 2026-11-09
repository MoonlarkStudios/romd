import { Alert, Button, Checkbox, Group, Stack, Text, Title } from '@mantine/core';
import {
  type ConsumerDiscSetStatus,
  issueConsumerDiscSetDownloadTicket,
  listConsumerDiscSets,
} from '@romd/consumer-api-client';
import { startBrowserDownload } from '@romd/delivery';
import { useInfiniteQuery } from '@tanstack/react-query';
import { useEffect, useRef, useState } from 'react';

export function DiscSetDownloads({ titleId }: { titleId: string }) {
  const query = useInfiniteQuery({
    queryKey: [
      'consumer',
      'disc-sets',
      titleId,
    ],
    initialPageParam: undefined as string | undefined,
    queryFn: async ({ pageParam, signal }) => {
      const response = await listConsumerDiscSets({
        query: {
          titleId,
          cursor: pageParam,
          limit: 50,
        },
        signal,
      });
      if (response.error || !response.data) throw new Error('Disc sets could not be loaded.');
      return response.data;
    },
    getNextPageParam: (page) => (page.hasNextPage ? (page.nextCursor ?? undefined) : undefined),
  });
  if (query.isPending) return <Text c="dimmed">Loading disc sets…</Text>;
  if (query.isError)
    return (
      <Alert color="red">
        Disc sets could not be loaded.{' '}
        <Button
          variant="subtle"
          onClick={() => void query.refetch()}
        >
          Retry
        </Button>
      </Alert>
    );
  const sets = query.data.pages.flatMap((page) => page.items);
  if (!sets.length) return null;
  return (
    <Stack gap="md">
      <Title order={3}>Disc sets</Title>
      <Text
        size="sm"
        c="dimmed"
      >
        Extract the ZIP, then open its M3U playlist in your emulator. Each disc stays separate.
      </Text>
      {sets.map((set) => (
        <DiscSetDownload
          key={`${set.discSetId}:${set.observationId}`}
          set={set}
          onStale={() => void query.refetch()}
        />
      ))}
      {query.hasNextPage && (
        <Button
          variant="subtle"
          loading={query.isFetchingNextPage}
          onClick={() => void query.fetchNextPage()}
        >
          More disc sets
        </Button>
      )}
    </Stack>
  );
}

function DiscSetDownload({ set, onStale }: { set: ConsumerDiscSetStatus; onStale: () => void }) {
  const [selected, setSelected] = useState(() =>
    set.discs.filter((disc) => disc.readiness === 'ready').map((disc) => disc.releaseId),
  );
  const [consent, setConsent] = useState(false);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState('');
  const [error, setError] = useState('');
  const active = useRef<AbortController | null>(null);
  useEffect(() => () => active.current?.abort(), []);
  const partial = selected.length < set.discs.length || set.missingExpectedOrdinals.length > 0;
  const download = async (format: string) => {
    if (active.current) return;
    const operation = new AbortController();
    active.current = operation;
    setBusy(true);
    setError('');
    setMessage('');
    try {
      const response = await issueConsumerDiscSetDownloadTicket({
        path: {
          discSetId: set.discSetId,
        },
        query: {
          format,
        },
        body: {
          contractVersion: set.contractVersion,
          revision: set.revision,
          observationId: set.observationId,
          selectedReleaseIds: set.discs
            .filter((disc) => selected.includes(disc.releaseId))
            .map((disc) => disc.releaseId),
          allowPartial: partial && consent,
        },
        signal: operation.signal,
      });
      if (operation.signal.aborted) return;
      if (response.error || !response.data) {
        setError(
          'This download could not be prepared. Review the refreshed disc set and try again.',
        );
        onStale();
        return;
      }
      startBrowserDownload(response.data.downloadUrl);
      setMessage('Download handed to your browser.');
    } catch {
      if (!operation.signal.aborted) setError('This download could not be prepared. Try again.');
    } finally {
      if (!operation.signal.aborted) setBusy(false);
      active.current = null;
    }
  };
  return (
    <Stack
      gap="sm"
      p="md"
      style={{
        border: '1px solid var(--mantine-color-default-border)',
        borderRadius: 'var(--mantine-radius-md)',
      }}
    >
      <Text fw={600}>{set.name}</Text>
      <Text
        size="sm"
        c="dimmed"
      >
        {set.expectedDiscCount === null
          ? 'Total disc count is unknown.'
          : `${set.expectedDiscCount} discs expected.`}
      </Text>
      {set.discs.map((disc) => (
        <Checkbox
          key={disc.releaseId}
          label={`Disc ${disc.ordinal}: ${disc.name}${disc.readiness === 'ready' ? '' : ' — unavailable'}`}
          checked={selected.includes(disc.releaseId)}
          disabled={busy || disc.readiness !== 'ready'}
          onChange={(event) => {
            setSelected(
              event.currentTarget.checked
                ? [
                    ...selected,
                    disc.releaseId,
                  ]
                : selected.filter((id) => id !== disc.releaseId),
            );
            setConsent(false);
            setMessage('');
          }}
        />
      ))}
      {set.missingExpectedOrdinals.length > 0 && (
        <Text size="sm">Missing discs: {set.missingExpectedOrdinals.join(', ')}.</Text>
      )}
      {partial && (
        <Checkbox
          checked={consent}
          disabled={busy}
          onChange={(event) => setConsent(event.currentTarget.checked)}
          label="Download only the selected discs; this set will be incomplete."
        />
      )}
      <Group>
        {set.archiveFormats?.map((choice) => (
          <Button
            key={choice.format}
            variant="light"
            loading={busy}
            disabled={!selected.length || (partial && !consent)}
            onClick={() => void download(choice.format)}
          >
            Download {choice.label}
          </Button>
        ))}
      </Group>
      <Text
        size="xs"
        c="dimmed"
      >
        CHD uses one file per eligible disc. Other discs keep their original files and companions.
      </Text>
      {error && <Alert color="red">{error}</Alert>}
      {message && (
        <Text
          role="status"
          size="sm"
        >
          {message}
        </Text>
      )}
    </Stack>
  );
}
