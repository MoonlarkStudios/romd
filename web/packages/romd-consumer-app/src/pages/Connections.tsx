import {
  Badge,
  Button,
  Group,
  Modal,
  NativeSelect,
  Paper,
  Stack,
  Text,
  TextInput,
  Title,
} from '@mantine/core';
import type { VfsConnectionCredentialDto, VfsConnectionDto } from '@romd/consumer-api-client';
import {
  createConsumerConnection,
  deleteConsumerConnection,
  listConsumerConnections,
  rotateConsumerConnection,
  updateConsumerConnection,
} from '@romd/consumer-api-client';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { ConnectionCredentialDialog } from '../components/connections/ConnectionCredentialDialog';
import { StatusState } from '../components/status/StatusState';
import { collectConsumerPages } from '../services/collectConsumerPages';

type Action =
  | {
      kind: 'create';
    }
  | {
      kind: 'edit' | 'rotate' | 'revoke';
      connection: VfsConnectionDto;
    };
const queryKey = [
  'consumer',
  'connections',
] as const;

export function Connections() {
  const queryClient = useQueryClient();
  const connections = useQuery({
    queryKey,
    queryFn: ({ signal }) =>
      collectConsumerPages(async (cursor) => {
        const response = await listConsumerConnections({
          query: {
            cursor,
            limit: 100,
          },
          signal,
        });
        if (response.error || !response.data) throw new Error('Unable to load connections.');
        return response.data;
      }),
  });
  const [action, setAction] = useState<Action | null>(null);
  const [name, setName] = useState('');
  const [format, setFormat] = useState<'stored' | 'original'>('stored');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // Secrets belong only to this one-time dialog, never the query cache or browser storage.
  const [credential, setCredential] = useState<VfsConnectionCredentialDto | null>(null);

  function begin(next: Action) {
    setName(next.kind === 'edit' ? next.connection.name : '');
    setFormat(
      next.kind === 'edit' && next.connection.format === 'original' ? 'original' : 'stored',
    );
    setError(null);
    setAction(next);
  }

  async function submit() {
    if (!action || busy) return;
    setBusy(true);
    setError(null);
    try {
      switch (action.kind) {
        case 'create': {
          const result = await createConsumerConnection({
            body: {
              name: name.trim(),
              format,
            },
          });
          if (result.error || !result.data)
            throw new Error(result.error?.detail ?? 'Connection could not be created.');
          setCredential(result.data);
          break;
        }
        case 'rotate': {
          const result = await rotateConsumerConnection({
            path: {
              connectionId: action.connection.id,
            },
          });
          if (result.error || !result.data)
            throw new Error(result.error?.detail ?? 'Password could not be replaced.');
          setCredential(result.data);
          break;
        }
        case 'edit': {
          const result = await updateConsumerConnection({
            path: {
              connectionId: action.connection.id,
            },
            body: {
              name: name.trim(),
              format,
            },
          });
          if (result.error)
            throw new Error(result.error.detail ?? 'Connection could not be updated.');
          break;
        }
        case 'revoke': {
          const result = await deleteConsumerConnection({
            path: {
              connectionId: action.connection.id,
            },
          });
          if (result.error)
            throw new Error(result.error.detail ?? 'Connection could not be revoked.');
          break;
        }
      }
      setAction(null);
      await queryClient.invalidateQueries({
        queryKey,
      });
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : 'Connection could not be updated.');
    } finally {
      setBusy(false);
    }
  }

  const title =
    action?.kind === 'create'
      ? 'New connection'
      : action?.kind === 'edit'
        ? 'Edit connection'
        : action?.kind === 'rotate'
          ? 'Replace password'
          : 'Revoke connection';
  const needsName = action?.kind === 'create' || action?.kind === 'edit';

  return (
    <Stack gap="xl">
      <Group justify="space-between">
        <Title
          order={1}
          className="romd-page-heading"
        >
          Connections
        </Title>
        <Button
          onClick={() =>
            begin({
              kind: 'create',
            })
          }
        >
          New connection
        </Button>
      </Group>
      <Text c="dimmed">
        Connect a device to your read-only library using WebDAV or rclone. Each connection has its
        own app password, valid until revoked. Account password changes and recovery invalidate
        these passwords.
      </Text>
      <Text c="dimmed">
        Stored is the default: each game appears in the format ROMD stores it in when emulators read
        that format directly, and as its original file otherwise. Original always delivers the
        original file. Changing this setting, or a game being converted to its stored format, can
        rename files. Refresh your file list; rescan your game library or remount if paths are
        cached.
      </Text>
      {connections.isPending ? (
        <StatusState
          kind="loading"
          label="Loading connections"
        />
      ) : connections.isError ? (
        <StatusState
          kind="error"
          message="Connections could not be loaded."
          onRetry={() => void connections.refetch()}
        />
      ) : connections.data.length === 0 ? (
        <StatusState
          kind="empty"
          title="No connections yet"
          message="Create a named connection for each device, such as Steam Deck or Living room."
        />
      ) : (
        <Stack gap="md">
          {connections.data.map((connection) => (
            <Paper
              key={connection.id}
              withBorder
              p="lg"
            >
              <Stack gap="sm">
                <Group justify="space-between">
                  <Title
                    order={2}
                    className="romd-card-title"
                  >
                    {connection.name}
                  </Title>
                  <Badge variant="light">{connection.status}</Badge>
                </Group>
                <Text className="romd-metadata">
                  Created {new Date(connection.createdAt).toLocaleDateString()} ·{' '}
                  {connection.lastUsedAt
                    ? `Last used ${new Date(connection.lastUsedAt).toLocaleString()}`
                    : 'Not used yet'}
                </Text>
                <Text className="romd-metadata">
                  Media format: {connection.format === 'original' ? 'Original' : 'Stored'}
                </Text>
                {connection.status === 'Invalidated' && (
                  <Text size="sm">
                    Account security changed. Replace this password to reconnect.
                  </Text>
                )}
                <Group gap="sm">
                  <Button
                    variant="subtle"
                    onClick={() =>
                      begin({
                        kind: 'edit',
                        connection,
                      })
                    }
                    aria-label={`Edit ${connection.name}`}
                  >
                    Edit
                  </Button>
                  {connection.status !== 'Revoked' && (
                    <>
                      <Button
                        variant="light"
                        onClick={() =>
                          begin({
                            kind: 'rotate',
                            connection,
                          })
                        }
                        aria-label={`Replace password for ${connection.name}`}
                      >
                        Replace password / config
                      </Button>
                      <Button
                        variant="subtle"
                        onClick={() =>
                          begin({
                            kind: 'revoke',
                            connection,
                          })
                        }
                        aria-label={`Revoke ${connection.name}`}
                      >
                        Revoke
                      </Button>
                    </>
                  )}
                </Group>
              </Stack>
            </Paper>
          ))}
        </Stack>
      )}
      <Modal
        opened={action !== null}
        onClose={() => {
          if (!busy) setAction(null);
        }}
        title={title}
        closeOnClickOutside={!busy}
        closeOnEscape={!busy}
        withCloseButton={!busy}
      >
        <Stack>
          {needsName && (
            <TextInput
              label="Connection name"
              placeholder="Steam Deck"
              maxLength={120}
              value={name}
              disabled={busy}
              onChange={(event) => setName(event.currentTarget.value)}
              data-autofocus
            />
          )}
          {needsName && (
            <NativeSelect
              label="Media format"
              value={format}
              disabled={busy}
              onChange={(event) => {
                if (
                  event.currentTarget.value === 'stored' ||
                  event.currentTarget.value === 'original'
                )
                  setFormat(event.currentTarget.value);
              }}
              data={[
                {
                  value: 'stored',
                  label: 'Stored (default)',
                },
                {
                  value: 'original',
                  label: 'Original',
                },
              ]}
            />
          )}
          {action?.kind === 'rotate' && (
            <Text>
              Replace the password for {action.connection.name}? All existing copies of its password
              and config will stop working. Save the replacement and update your device.
            </Text>
          )}
          {action?.kind === 'revoke' && (
            <Text>
              Revoke {action.connection.name}? It will no longer be able to access your library. You
              can create a new connection later.
            </Text>
          )}
          {error && (
            <StatusState
              kind="error"
              message={error}
            />
          )}
          <Group justify="flex-end">
            <Button
              variant="subtle"
              disabled={busy}
              onClick={() => setAction(null)}
            >
              Cancel
            </Button>
            <Button
              loading={busy}
              disabled={needsName && !name.trim()}
              onClick={() => void submit()}
            >
              {action?.kind === 'edit' ? 'Save connection' : title}
            </Button>
          </Group>
        </Stack>
      </Modal>
      <ConnectionCredentialDialog
        credential={credential}
        onClose={() => setCredential(null)}
      />
    </Stack>
  );
}
