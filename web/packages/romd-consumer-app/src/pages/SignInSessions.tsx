import { Badge, Button, Group, Modal, Paper, Stack, Text, Title } from '@mantine/core';
import {
  type ConsumerSignInSession,
  listConsumerSignInSessions,
  revokeConsumerSignInSession,
} from '@romd/consumer-api-client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { StatusState } from '../components/status/StatusState';
import { useAuth } from '../contexts/AuthContext';
import { collectConsumerPages } from '../services/collectConsumerPages';

const queryKey = [
  'consumer',
  'sign-in-sessions',
] as const;

export function SignInSessions() {
  const { logout } = useAuth();
  const cache = useQueryClient();
  const [selected, setSelected] = useState<ConsumerSignInSession | null>(null);
  const sessions = useQuery({
    queryKey,
    queryFn: ({ signal }) =>
      collectConsumerPages(async (cursor) => {
        const response = await listConsumerSignInSessions({
          signal,
          query: {
            cursor,
            limit: 100,
          },
        });
        if (!response.data) throw new Error('Could not load sign-in sessions.');
        return response.data;
      }),
  });
  const revoke = useMutation({
    mutationFn: async (session: ConsumerSignInSession) => {
      const response = await revokeConsumerSignInSession({
        path: {
          sessionId: session.id,
        },
      });
      if (response.error) throw new Error('Could not revoke this session.');
      if (session.isCurrent) await logout();
    },
    onSuccess: async () => {
      setSelected(null);
      await cache.invalidateQueries({
        queryKey,
      });
    },
  });

  return (
    <Stack>
      <Title
        order={1}
        className="romd-page-heading"
      >
        Sign-in sessions
      </Title>
      {sessions.isPending && (
        <StatusState
          kind="loading"
          label="Loading sign-in sessions…"
        />
      )}
      {sessions.isError && (
        <StatusState
          kind="error"
          message={sessions.error.message}
          onRetry={() => void sessions.refetch()}
        />
      )}
      {sessions.data?.length === 0 && (
        <StatusState
          kind="empty"
          title="No sessions found"
        />
      )}
      {sessions.data?.map((session) => (
        <Paper
          key={session.id}
          p="md"
          withBorder
        >
          <Group justify="space-between">
            <Stack gap={4}>
              <Group gap="xs">
                <Text fw={600}>{session.device}</Text>
                <Badge variant="light">
                  {session.isCurrent
                    ? 'This browser'
                    : session.status === 'Current'
                      ? 'Active'
                      : session.status}
                </Badge>
              </Group>
              <Text
                size="sm"
                c="dimmed"
              >
                Last used {new Date(session.lastUsedAt).toLocaleString()}
              </Text>
            </Stack>
            {session.status === 'Current' && (
              <Button
                variant="light"
                color="red"
                onClick={() => {
                  revoke.reset();
                  setSelected(session);
                }}
              >
                Revoke
              </Button>
            )}
          </Group>
        </Paper>
      ))}
      <Modal
        opened={selected !== null}
        onClose={() => setSelected(null)}
        title="Revoke sign-in session"
      >
        {selected && (
          <Stack>
            <Text>End the ROMD sign-in for {selected.device}?</Text>
            {revoke.isError && (
              <Text
                c="red"
                role="alert"
              >
                {revoke.error.message}
              </Text>
            )}
            <Group justify="flex-end">
              <Button
                variant="subtle"
                onClick={() => setSelected(null)}
              >
                Cancel
              </Button>
              <Button
                color="red"
                loading={revoke.isPending}
                onClick={() => revoke.mutate(selected)}
              >
                Revoke session
              </Button>
            </Group>
          </Stack>
        )}
      </Modal>
    </Stack>
  );
}
