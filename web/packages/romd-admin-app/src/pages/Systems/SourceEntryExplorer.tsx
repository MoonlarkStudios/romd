import { ActionIcon, Alert, Anchor, Badge, Button, Group, Loader, Stack, Text, TextInput } from '@mantine/core';
import { useDebouncedValue } from '@mantine/hooks';
import { getGameByDat, listSourceEntries, type SourceEntryReference } from '@romd/admin-api-client';
import { IconChevronRight } from '@tabler/icons-react';
import { useInfiniteQuery, useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { Link, useSearchParams } from 'react-router';
import { DatEntryFiles } from './DatGameRow';
import classes from './Sources.module.css';

export function SourceEntryExplorer({ datId }: { datId: string }) {
  const [search, setSearch] = useState('');
  const [term] = useDebouncedValue(search, 250);
  const [params, setParams] = useSearchParams();
  const titleId = params.get('sourceTitle') ?? undefined;
  const entries = useInfiniteQuery({
    queryKey: [
      'source-entries',
      datId,
      term,
      titleId,
    ],
    initialPageParam: undefined as string | undefined,
    queryFn: async ({ pageParam, signal }) => {
      const result = await listSourceEntries({
        path: {
          datId,
        },
        query: {
          cursor: pageParam,
          search: term,
          titleId,
          limit: 50,
        },
        signal,
      });
      if (result.error || !result.data) throw new Error('Source entries could not be loaded.');
      return result.data;
    },
    getNextPageParam: (page) => page.nextCursor ?? undefined,
  });
  return (
    <Stack gap="sm">
      <TextInput
        label="Search source entries"
        placeholder="Entry or title name"
        value={search}
        maxLength={200}
        onChange={(e) => setSearch(e.currentTarget.value)}
      />
      {titleId && (
        <Group>
          <Badge color="gray">Entries for the selected title</Badge>
          <Button
            size="xs"
            variant="subtle"
            onClick={() => {
              const next = new URLSearchParams(params);
              next.delete('sourceTitle');
              setParams(next);
            }}
          >
            Show all entries
          </Button>
        </Group>
      )}
      {entries.isPending && <Loader size="sm" />}
      {entries.isError && (
        <Text c="red">
          Entries could not be loaded.{' '}
          <Button
            variant="subtle"
            onClick={() => void entries.refetch()}
          >
            Retry
          </Button>
        </Text>
      )}
      {entries.data?.pages
        .flatMap((p) => p.items)
        .map((entry) => (
          <SourceEntry key={entry.gameId} entry={entry} datId={datId} />
        ))}
      {entries.data?.pages[0]?.items.length === 0 && <Text c="dimmed" size="sm">No entries match this search.</Text>}
      {entries.hasNextPage && <Button variant="light" loading={entries.isFetchingNextPage} onClick={() => void entries.fetchNextPage()}>Load more entries</Button>}
    </Stack>
  );
}

function SourceEntry({ entry, datId }: { entry: SourceEntryReference; datId: string }) {
  const [expanded, setExpanded] = useState(false);
  const files = useQuery({
    queryKey: ['source-entry-files', datId, entry.gameId],
    enabled: expanded,
    queryFn: async ({ signal }) => {
      const result = await getGameByDat({ path: { datId, gameId: entry.gameId }, signal });
      if (result.error || !result.data) throw new Error('Expected files could not be loaded.');
      return result.data;
    },
  });
  return <article className={classes.entry} aria-label={entry.name}>
    <Group align="flex-start" wrap="nowrap" gap="sm">
      <ActionIcon variant="subtle" color="gray" title="Expected files" aria-label={`Expected files for ${entry.name}`} aria-expanded={expanded} aria-controls={`files-${entry.gameId}`} onClick={() => setExpanded((value) => !value)} style={{ flexShrink: 0 }}><IconChevronRight size={18} style={{ transform: expanded ? 'rotate(90deg)' : undefined }} /></ActionIcon>
      <div style={{ minWidth: 0, flex: 1 }}>
            <Stack gap={4}>
              <Text
                size="sm"
                fw={500}
                style={{
                  overflowWrap: 'anywhere',
                }}
              >
                {entry.name}
              </Text>
              {entry.titleId ? (
                <Anchor
                  component={Link}
                  to={`/titles/${entry.titleId}`}
                  size="sm"
                >
                  {entry.titleName ?? 'Open title'}
                </Anchor>
              ) : (
                <Text
                  size="xs"
                  c="dimmed"
                >
                  No title assigned
                </Text>
              )}
              <Group gap="xs">
                <Badge
                  size="xs"
                  color={entry.hasLocalPayload ? 'teal' : 'gray'}
                >
                  {entry.hasLocalPayload ? 'Local ROM payload' : 'No local ROM payload'}
                </Badge>
                <Text
                  size="xs"
                  c="dimmed"
                >
                  {Number(entry.activeSources) > 0
                    ? `${entry.activeSources} active catalog source${Number(entry.activeSources) === 1 ? '' : 's'}`
                    : 'No active catalog definition'}
                </Text>
              </Group>
            </Stack>
      </div>
    </Group>
    {expanded && <Stack id={`files-${entry.gameId}`} gap="sm" mt="md"><Text size="sm" fw={600}>Expected files</Text>{files.isPending ? <Loader size="sm" /> : files.isError ? <Alert color="red">Expected files could not be loaded.<Button variant="subtle" onClick={() => void files.refetch()}>Retry files</Button></Alert> : <DatEntryFiles game={files.data} />}</Stack>}
  </article>;
}
