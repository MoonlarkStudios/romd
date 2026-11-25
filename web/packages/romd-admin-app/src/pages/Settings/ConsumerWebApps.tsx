import { Alert, Badge, Button, Code, Group, Modal, Stack, Text, TextInput } from '@mantine/core';
import {
  type ConsumerWebApp,
  disableConsumerWebApp,
  listConsumerWebApps,
  registerConsumerWebApp,
} from '@romd/admin-api-client';
import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
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
  const [cursors, setCursors] = useState<string[]>([]);
  const cursor = cursors.at(-1);
  const apps = useQuery({
    queryKey: [
      ...queryKey,
      cursor,
    ],
    placeholderData: keepPreviousData,
    queryFn: async ({ signal }) => {
      const response = await listConsumerWebApps({
        signal,
        query: {
          cursor,
          limit: 25,
        },
      });
      if (!response.data)
        throw new Error(apiError(response.error, 'Could not load consumer web apps.'));
      return response.data;
    },
    refetchInterval: (query) => {
      const items = query.state.data?.items;
      if (items?.some((app) => app.status === 'Pending')) return 2000;
      if (items?.some((app) => app.status === 'ProvisioningFailed')) return 10000;
      return false;
    },
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
      return response.data;
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
      {apps.data?.items.map((app) => (
        <Stack
          key={app.clientId}
          gap={4}
        >
          <Group justify="space-between">
            <Group gap="xs">
              <Text fw={600}>{app.displayName}</Text>
              <Badge
                variant="light"
                color={
                  app.status === 'ProvisioningFailed'
                    ? 'orange'
                    : app.status === 'Active'
                      ? 'green'
                      : 'gray'
                }
              >
                {app.status === 'ProvisioningFailed' ? 'Setup failed' : app.status}
              </Badge>
            </Group>
            {app.status !== 'Disabled' && (
              <Button
                variant="subtle"
                color="red"
                aria-label={`Disable ${app.displayName}`}
                onClick={() => {
                  disable.reset();
                  setSelected(app);
                }}
              >
                Disable
              </Button>
            )}
          </Group>
          {app.status === 'ProvisioningFailed' && (
            <Text
              c="dimmed"
              size="sm"
            >
              The worker will retry automatically. Check the worker logs if setup keeps failing.
            </Text>
          )}
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
      {(cursor || apps.data?.hasNextPage) && (
        <Group
          justify="flex-end"
          aria-label="Web app pages"
        >
          <Button
            variant="default"
            disabled={!cursor || apps.isPlaceholderData}
            onClick={() => setCursors((current) => current.slice(0, -1))}
          >
            Previous
          </Button>
          <Button
            variant="default"
            disabled={!apps.data?.hasNextPage || apps.isPlaceholderData}
            onClick={() => {
              const next = apps.data?.nextCursor;
              if (next)
                setCursors((current) => [
                  ...current,
                  next,
                ]);
            }}
          >
            Next
          </Button>
        </Group>
      )}
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
        {register.data && (
          <Alert role="status">
            Registered {register.data.displayName}. Use client ID{' '}
            <Code>{register.data.clientId}</Code> once the registration is active.
          </Alert>
        )}
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
            <Text>
              Disable {selected.displayName} and end all of its ROMD sign-ins? To reconnect it,
              register the app again with a new client ID.
            </Text>
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
