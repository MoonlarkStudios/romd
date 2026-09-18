import { Alert, Button, Drawer, Group, Loader, Paper, Stack, Text, TextInput } from '@mantine/core';
import { useState } from 'react';
import { useNavigate } from 'react-router';
import {
  useManagedSystems,
  useSetSystemEnabled,
  useSetupCatalogs,
} from '../../hooks/api/useManagedSystems';
import { AddSourceFlow } from './AddSourceFlow';
import { matchesSystem } from './systemSearch';

export { matchesSystem } from './systemSearch';

/** Adding a system is a local preference, independent of catalog acquisition. */
export function AddSystemFlow({
  startWithUpload = false,
  onClose,
}: {
  startWithUpload?: boolean;
  onClose: () => void;
}) {
  const systems = useManagedSystems();
  const catalogs = useSetupCatalogs();
  const enable = useSetSystemEnabled();
  const navigate = useNavigate();
  const [search, setSearch] = useState('');
  if (startWithUpload)
    return (
      <AddSourceFlow
        startWithUpload
        onClose={onClose}
      />
    );
  return (
    <Drawer
      opened
      onClose={onClose}
      title="Add system"
      position="right"
      size="min(560px, 100%)"
    >
      <Stack gap="lg">
        <Text>
          Choose a system for your collection. You can add catalog sources after adding the system.
        </Text>
        <TextInput
          label="Find a system"
          placeholder="Try PS1, PSX, or PlayStation"
          value={search}
          onChange={(e) => setSearch(e.currentTarget.value)}
          data-autofocus
        />
        {enable.isError && <Alert color="red">{enable.error.message}</Alert>}
        {systems.isPending ? (
          <Loader />
        ) : systems.isError ? (
          <Alert color="red">
            Your systems could not be loaded.
            <Button onClick={() => void systems.refetch()}>Retry</Button>
          </Alert>
        ) : (
          <Stack gap="sm">
            {systems.data
              ?.filter((s) => matchesSystem(s, search))
              .map((s) => {
                const available =
                  catalogs.data?.catalogs.filter(
                    (c) => c.systemId === s.key && c.health === 'healthy' && c.documentHash,
                  ) ?? [];
                return (
                  <Paper
                    withBorder
                    p="md"
                    radius="md"
                    key={s.key}
                  >
                    <Stack gap="sm">
                      <div>
                        <Text fw={600}>{s.name}</Text>
                        <Text
                          size="xs"
                          c="dimmed"
                        >
                          {[
                            s.shortName,
                            ...s.aliases,
                          ].join(' · ')}
                        </Text>
                      </div>
                      <Text
                        size="sm"
                        c="dimmed"
                      >
                        {available.length
                          ? `${available.map((c) => c.provider).join(', ')} subscription available`
                          : 'Bring your own DAT; sources can be added later.'}
                      </Text>
                      <Group justify="flex-end">
                        <Button
                          variant={s.enabled ? 'light' : 'filled'}
                          loading={enable.isPending && enable.variables?.systemKey === s.key}
                          disabled={enable.isPending}
                          aria-label={`${s.enabled ? 'Open' : 'Add'} ${s.name}`}
                          onClick={async () => {
                            try {
                              if (!s.enabled)
                                await enable.mutateAsync({
                                  systemKey: s.key,
                                  enabled: true,
                                });
                              onClose();
                              navigate(`/systems/${s.key}`);
                            } catch {
                              /* Mutation error remains visible. */
                            }
                          }}
                        >
                          {s.enabled ? 'Open system' : 'Add to your systems'}
                        </Button>
                      </Group>
                    </Stack>
                  </Paper>
                );
              })}
            {!systems.data?.some((s) => matchesSystem(s, search)) && (
              <Text>No matching systems. Try another name or alias.</Text>
            )}
          </Stack>
        )}
        {(catalogs.isError || catalogs.data?.message) && (
          <Text
            size="sm"
            c="dimmed"
          >
            Subscription availability is unavailable. You can still add a system and upload a DAT.
          </Text>
        )}
      </Stack>
    </Drawer>
  );
}
