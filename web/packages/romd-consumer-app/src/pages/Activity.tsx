import { ActionIcon, Alert, Box, Button, Group, Menu, Modal, Paper, Stack, Table, Text, Title, VisuallyHidden } from '@mantine/core';
import type { PlaySessionDto } from '@romd/consumer-api-client';
import { IconDots } from '@tabler/icons-react';
import { useState } from 'react';
import { Link } from 'react-router';
import { ArtworkFallback } from '../components/library/ArtworkFallback';
import { ArtworkImage } from '../components/library/ArtworkImage';
import { TitleLink } from '../components/navigation/TitleLink';
import { StatusState } from '../components/status/StatusState';
import { useTitleDetail } from '../hooks/useConsumerLibrary';
import { useClearPlaySessions, useDeletePlaySession, usePlaySessions } from '../hooks/usePlayActivity';

type PendingDeletion = { kind: 'session'; sessionId: string } | { kind: 'all' } | null;

export function Activity() {
  const sessionsQuery = usePlaySessions();
  const deleteSession = useDeletePlaySession();
  const clearSessions = useClearPlaySessions();
  const [pendingDeletion, setPendingDeletion] = useState<PendingDeletion>(null);
  const [deletionError, setDeletionError] = useState<string | null>(null);
  const sessions = sessionsQuery.data?.pages.flatMap((page) => page.items) ?? [];
  const deleting = deleteSession.isPending || clearSessions.isPending;

  if (sessionsQuery.isLoading) return <StatusState kind="loading" />;
  if (sessionsQuery.isError && !sessionsQuery.data) {
    return <StatusState kind="error" message="Your play history could not be loaded." onRetry={() => void sessionsQuery.refetch()} />;
  }

  const requestDeletion = (value: PendingDeletion) => { setDeletionError(null); setPendingDeletion(value); };
  const confirmDeletion = async () => {
    try {
      if (pendingDeletion?.kind === 'session') await deleteSession.mutateAsync(pendingDeletion.sessionId);
      else if (pendingDeletion?.kind === 'all') await clearSessions.mutateAsync();
      setPendingDeletion(null);
    } catch {
      setDeletionError('Your history could not be updated. Please try again.');
    }
  };

  return (
    <Stack gap="var(--romd-page-gap)">
      <Group justify="space-between" align="flex-end">
        <Stack gap={6}>
          <Text className="romd-eyebrow" c="dimmed">Your activity</Text>
          <Title order={1} className="romd-page-heading">Play history</Title>
          <Text c="dimmed">Revisit the games you’ve played and the time you’ve spent with them.</Text>
        </Stack>
        {sessions.length > 0 && <Menu position="bottom-end"><Menu.Target>
          <Button variant="default" rightSection={<IconDots size={16} />}>Manage history</Button>
        </Menu.Target><Menu.Dropdown><Menu.Item color="red" onClick={() => requestDeletion({ kind: 'all' })}>Clear all history</Menu.Item></Menu.Dropdown></Menu>}
      </Group>

      {sessions.length === 0 ? (
        <StatusState kind="empty" title="Your next game starts here" message="Once you start playing, your games and sessions will appear here." action={<Button component={Link} to="/library" variant="light">Browse games</Button>} />
      ) : (
        <Paper withBorder radius="md" style={{ overflow: 'hidden', background: 'var(--romd-panel-strong)' }}>
          <Group justify="space-between" px="lg" py="md">
            <Text fw={600}>Recent sessions</Text>
            <Text size="sm" c="dimmed">Newest first</Text>
          </Group>
          <Table.ScrollContainer minWidth={660}>
            <Table verticalSpacing="md" horizontalSpacing="lg" highlightOnHover aria-label="Play history">
              <Table.Thead><Table.Tr>
                <Table.Th>Game</Table.Th><Table.Th>Played</Table.Th><Table.Th>Play time</Table.Th><Table.Th>Played on</Table.Th><Table.Th><VisuallyHidden>Actions</VisuallyHidden></Table.Th>
              </Table.Tr></Table.Thead>
              <Table.Tbody>{sessions.map(session => <SessionRow key={session.sessionId} session={session} onDelete={() => requestDeletion({ kind: 'session', sessionId: session.sessionId })} />)}</Table.Tbody>
            </Table>
          </Table.ScrollContainer>
          <Group justify="space-between" px="lg" py="md" style={{ borderTop: '1px solid var(--romd-border)' }}>
            <Text size="sm" c="dimmed">{sessions.length} {sessions.length === 1 ? 'session' : 'sessions'}{sessionsQuery.hasNextPage ? ' loaded' : ''}</Text>
            {sessionsQuery.hasNextPage && <Button variant="subtle" loading={sessionsQuery.isFetchingNextPage} onClick={() => void sessionsQuery.fetchNextPage()}>Load older sessions</Button>}
          </Group>
          {sessionsQuery.isError && <Alert color="red" m="md">More history could not be loaded. <Button variant="subtle" onClick={() => void sessionsQuery.fetchNextPage()}>Try again</Button></Alert>}
        </Paper>
      )}

      <Modal opened={pendingDeletion !== null} onClose={() => !deleting && setPendingDeletion(null)} title={pendingDeletion?.kind === 'all' ? 'Clear play history?' : 'Remove this session?'} centered closeOnClickOutside={!deleting} closeOnEscape={!deleting} withCloseButton={!deleting}>
        <Stack>
          <Text>{pendingDeletion?.kind === 'all' ? 'This permanently removes all sessions from your ROMD play history. Your games, saves, and local device history will stay unchanged.' : 'This permanently removes this session from your ROMD play history. Your game and saves will stay unchanged.'}</Text>
          {deletionError && <Alert color="red">{deletionError}</Alert>}
          <Group justify="flex-end"><Button variant="default" disabled={deleting} onClick={() => setPendingDeletion(null)}>Cancel</Button><Button color="red" loading={deleting} onClick={() => void confirmDeletion()}>{pendingDeletion?.kind === 'all' ? 'Clear history' : 'Remove session'}</Button></Group>
        </Stack>
      </Modal>
    </Stack>
  );
}

function SessionRow({ session, onDelete }: { session: PlaySessionDto; onDelete: () => void }) {
  // The shared query cache deduplicates repeated games across sessions and pages.
  const title = useTitleDetail(session.titleId);
  const name = title.data?.name ?? (title.isLoading ? 'Loading game…' : 'Game unavailable');
  const date = new Date(session.startedAt);
  return <Table.Tr>
    <Table.Td><Group wrap="nowrap" gap="md">
      <Box w={44} style={{ flexShrink: 0, borderRadius: 'var(--mantine-radius-sm)', overflow: 'hidden' }}>
        <ArtworkImage artwork={title.data?.artwork} fallback={<ArtworkFallback name={name} fontSize={20} />} />
      </Box>
      <Stack gap={4} style={{ minWidth: 0 }}>
        <Text component={TitleLink} to={`/titles/${session.titleId}`} fw={600} style={{ color: 'inherit', textDecoration: 'none' }}>{name}</Text>
        <Text size="xs" c="dimmed">{title.data?.system.name ?? '—'}</Text>
        {title.isError && <Button size="compact-xs" variant="subtle" w="fit-content" onClick={() => void title.refetch()}>Retry game details</Button>}
      </Stack>
    </Group></Table.Td>
    <Table.Td><Stack gap={3}><Text component="time" dateTime={session.startedAt} size="sm" style={{ whiteSpace: 'nowrap' }}>{new Intl.DateTimeFormat(undefined, { dateStyle: 'medium' }).format(date)}</Text><Text size="xs" c="dimmed">{new Intl.DateTimeFormat(undefined, { timeStyle: 'short' }).format(date)}</Text></Stack></Table.Td>
    <Table.Td><Text size="sm" style={{ whiteSpace: 'nowrap', fontVariantNumeric: 'tabular-nums' }}>{formatDuration(session.activeDurationSeconds)}</Text>{!session.endedAt && <Text size="xs" c="dimmed">End not recorded</Text>}</Table.Td>
    <Table.Td><Text size="sm" c="dimmed">{session.clientId === 'romd.web' ? 'Browser' : session.clientId === 'romd.console' ? 'ROMD Console' : 'Other device'}</Text></Table.Td>
    <Table.Td><Menu position="bottom-end"><Menu.Target><ActionIcon variant="subtle" color="gray" aria-label={`Session options for ${name}`}><IconDots size={18} /></ActionIcon></Menu.Target><Menu.Dropdown><Menu.Item component={Link} to={`/activity/sessions/${session.sessionId}`}>Session details</Menu.Item><Menu.Item color="red" onClick={onDelete}>Remove from history</Menu.Item></Menu.Dropdown></Menu></Table.Td>
  </Table.Tr>;
}

function formatDuration(seconds: number | null | undefined): string {
  if (seconds == null) return 'Not recorded';
  if (seconds < 60) return `${seconds}s`;
  const minutes = Math.floor(seconds / 60);
  return minutes < 60 ? `${minutes}m` : `${Math.floor(minutes / 60)}h ${minutes % 60}m`;
}
