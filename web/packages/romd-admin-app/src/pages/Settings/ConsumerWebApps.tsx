import { Alert, Badge, Button, Code, Group, Modal, Stack, Text, TextInput } from '@mantine/core';
import {
  type ConsumerWebApp,
  disableConsumerWebApp,
  listConsumerWebApps,
  registerConsumerWebApp,
} from '@romd/admin-api-client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { apiError } from '../../components/Administration/apiError';

const queryKey = [
  'server',
  'consumer-web-apps',
] as const;
const emptyDraft = {
  displayName: '',
  callbackUri: '',
  browserOrigin: '',
};

export function ConsumerWebApps() {
  const cache = useQueryClient();
  const [draft, setDraft] = useState(emptyDraft);
  const [selected, setSelected] = useState<ConsumerWebApp | null>(null);
  const apps = useQuery({
    queryKey,
    queryFn: async ({ signal }) => {
      const response = await listConsumerWebApps({
        signal,
      });
      if (!response.data)
        throw new Error(apiError(response.error, 'Could not load consumer web apps.'));
      return response.data;
    },
    refetchInterval: (query) =>
      query.state.data?.some((app) => app.status === 'Pending') ? 2000 : false,
  });
  const refresh = () =>
    cache.invalidateQueries({
      queryKey,
    });

  const register = useMutation({
    mutationFn: async () => {
      const response = await registerConsumerWebApp({
        body: draft,
      });
      if (!response.data)
        throw new Error(apiError(response.error, 'Could not register the web app.'));
    },
    onSuccess: async () => {
      setDraft(emptyDraft);
      await refresh();
    },
  });
  const disable = useMutation({
    mutationFn: async (clientId: string) => {
      const response = await disableConsumerWebApp({
        path: {
          clientId,
        },
      });
      if (response.error)
        throw new Error(apiError(response.error, 'Could not disable the web app.'));
    },
    onSuccess: async () => {
      setSelected(null);
      await refresh();
    },
  });

  return (
    <Stack mt="sm">
      <Text
        c="dimmed"
        size="sm"
      >
        Approve a separately hosted app to let members sign in with their ROMD account.
      </Text>
      {apps.isPending && <Text role="status">Loading web apps…</Text>}
      {apps.isError && (
        <Alert
          color="red"
          role="alert"
        >
          {apps.error.message}
          <Button
            variant="subtle"
            onClick={() => void apps.refetch()}
          >
            Retry
          </Button>
        </Alert>
      )}
      {apps.data?.map((app) => (
        <Stack
          key={app.clientId}
          gap={4}
        >
          <Group justify="space-between">
            <Group gap="xs">
              <Text fw={600}>{app.displayName}</Text>
              <Badge
                variant="light"
                color={app.status === 'Active' ? 'green' : 'gray'}
              >
                {app.status}
              </Badge>
            </Group>
            {app.status !== 'Disabled' && (
              <Button
                variant="subtle"
                color="red"
                onClick={() => {
                  disable.reset();
                  setSelected(app);
                }}
              >
                Disable
              </Button>
            )}
          </Group>
          <Text
            c="dimmed"
            size="sm"
            style={{
              overflowWrap: 'anywhere',
            }}
          >
            {app.callbackUri}
          </Text>
          <Text
            c="dimmed"
            size="sm"
          >
            {app.browserOrigin}
          </Text>
          <Code
            style={{
              alignSelf: 'flex-start',
              overflowWrap: 'anywhere',
              whiteSpace: 'normal',
            }}
          >
            {app.clientId}
          </Code>
        </Stack>
      ))}
      <Stack
        component="form"
        gap="sm"
        onSubmit={(event) => {
          event.preventDefault();
          register.mutate();
        }}
      >
        <TextInput
          required
          label="Display name"
          maxLength={120}
          value={draft.displayName}
          onChange={(event) =>
            setDraft({
              ...draft,
              displayName: event.currentTarget.value,
            })
          }
        />
        <TextInput
          required
          type="url"
          label="HTTPS callback URL"
          maxLength={2048}
          placeholder="https://games.example.org/auth/callback"
          value={draft.callbackUri}
          onChange={(event) =>
            setDraft({
              ...draft,
              callbackUri: event.currentTarget.value,
            })
          }
        />
        <TextInput
          required
          type="url"
          label="HTTPS browser origin"
          maxLength={255}
          placeholder="https://games.example.org"
          value={draft.browserOrigin}
          onChange={(event) =>
            setDraft({
              ...draft,
              browserOrigin: event.currentTarget.value,
            })
          }
        />
        {register.isError && (
          <Alert
            color="red"
            role="alert"
          >
            {register.error.message}
          </Alert>
        )}
        <Group justify="flex-end">
          <Button
            type="submit"
            loading={register.isPending}
          >
            Register web app
          </Button>
        </Group>
      </Stack>
      <Modal
        opened={selected !== null}
        onClose={() => setSelected(null)}
        title="Disable web app"
      >
        {selected && (
          <Stack>
            <Text>Disable {selected.displayName} and end all of its ROMD sign-ins?</Text>
            {disable.isError && (
              <Alert
                color="red"
                role="alert"
              >
                {disable.error.message}
              </Alert>
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
                loading={disable.isPending}
                onClick={() => disable.mutate(selected.clientId)}
              >
                Disable web app
              </Button>
            </Group>
          </Stack>
        )}
      </Modal>
    </Stack>
  );
}
