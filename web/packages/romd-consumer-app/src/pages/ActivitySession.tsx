import { Badge, Button, Group, Paper, Stack, Text, Title } from '@mantine/core';
import { IconArrowLeft, IconTrash } from '@tabler/icons-react';
import type { ReactNode } from 'react';
import { Link, useNavigate, useParams } from 'react-router';
import { TitleLink } from '../components/navigation/TitleLink';
import { StatusState } from '../components/status/StatusState';
import { useTitleDetail } from '../hooks/useConsumerLibrary';
import { useHistoryBack } from '../hooks/useHistoryBack';
import { useDeletePlaySession, usePlaySession } from '../hooks/usePlayActivity';

export function ActivitySession() {
  const { sessionId } = useParams();
  const navigate = useNavigate();
  const goBack = useHistoryBack('/activity');
  const sessionQuery = usePlaySession(sessionId);
  const titleQuery = useTitleDetail(sessionQuery.data?.titleId);
  const deleteSession = useDeletePlaySession();
  const session = sessionQuery.data;

  if (sessionQuery.isLoading) {
    return <StatusState kind="loading" />;
  }

  if (sessionQuery.isError || !session) {
    return (
      <StatusState
        kind="error"
        message="This session does not exist or its title is no longer in your Library."
        onRetry={() => void sessionQuery.refetch()}
      />
    );
  }

  const remove = async () => {
    if (!window.confirm('Permanently delete this server-side play session? Local client history is unaffected.')) {
      return;
    }
    await deleteSession.mutateAsync(session.sessionId);
    navigate('/activity', { replace: true });
  };

  return (
    <Stack gap="var(--romd-page-gap)">
      <Button variant="subtle" color="gray" leftSection={<IconArrowLeft size={16} />} onClick={goBack} w="fit-content">
        Back
      </Button>
      <Stack gap={4}>
        <Title order={1} className="romd-page-heading">
          {titleQuery.data?.name ?? 'Play session'}
        </Title>
        <Text c="dimmed">Session {session.sessionId}</Text>
      </Stack>
      <Paper p="lg" withBorder style={{ background: 'var(--romd-panel-strong)' }}>
        <Stack gap="md">
          <Group><Text fw={700}>Status</Text><Badge color={session.endedAt ? 'gray' : 'bronze'}>{session.endedAt ? 'Ended' : 'Open'}</Badge></Group>
          <Detail label="Client" value={session.clientId} />
          <Detail label="Started" value={formatInstant(session.startedAt)} />
          <Detail label="Ended" value={session.endedAt ? formatInstant(session.endedAt) : 'Not reported'} />
          <Detail label="Active duration" value={formatDuration(session.activeDurationSeconds)} />
          <Detail
            label="Title"
            value={<Text component={TitleLink} to={`/titles/${session.titleId}`}>{titleQuery.data?.name ?? session.titleId}</Text>}
          />
          <Detail label="Release ID" value={session.releaseId} />
        </Stack>
      </Paper>
      <Button
        color="red"
        variant="light"
        leftSection={<IconTrash size={16} />}
        loading={deleteSession.isPending}
        onClick={() => void remove()}
        w="fit-content"
      >
        Delete session
      </Button>
    </Stack>
  );
}

function Detail({ label, value }: { label: string; value: ReactNode }) {
  return <Group justify="space-between" align="flex-start"><Text c="dimmed">{label}</Text><Text component="div">{value}</Text></Group>;
}

function formatInstant(value: string): string {
  return new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'long' }).format(new Date(value));
}

function formatDuration(value: number | null | undefined): string {
  if (value == null) return 'Not reported';
  const hours = Math.floor(value / 3600);
  const minutes = Math.floor((value % 3600) / 60);
  return hours > 0 ? `${hours}h ${minutes}m` : `${minutes}m`;
}
