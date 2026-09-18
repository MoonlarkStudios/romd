import { Alert, Button, Group, Modal, Paper, Stack, Text, Title } from '@mantine/core';
import { useState } from 'react';
import { useNavigate } from 'react-router';
import { useManagedSystems, useSetSystemEnabled } from '../../hooks/api/useManagedSystems';
import { MetadataPolicy } from './MetadataPolicy';
export function SystemSettings({ systemKey }: { systemKey: string }) {
  const systems = useManagedSystems();
  const system = systems.data?.find((s) => s.key === systemKey);
  const enable = useSetSystemEnabled();
  const navigate = useNavigate();
  const [confirm, setConfirm] = useState(false);
  if (!system) return null;
  return (
    <Stack gap="lg"><MetadataPolicy systemKey={systemKey} /><Paper
      withBorder
      p="lg"
      radius="md"
    >
      <Stack align="flex-start">
        <Title order={3}>Your systems</Title>
        <Text>
          Removing a system hides it from your list. ROMs, catalog sources, installed DATs,
          subscriptions, and settings stay saved. You can add it again at any time.
        </Text>
        <Button
          variant="light"
          color={system.enabled ? 'red' : 'blue'}
          loading={enable.isPending}
          onClick={() =>
            system.enabled
              ? setConfirm(true)
              : enable.mutate({
                  systemKey,
                  enabled: true,
                })
          }
        >
          {system.enabled ? 'Remove from Your systems' : 'Add to Your systems'}
        </Button>
        {enable.isError && <Alert color="red">{enable.error.message}</Alert>}
        <Modal
          opened={confirm}
          onClose={() => setConfirm(false)}
          title={`Remove ${system.name} from Your systems?`}
          centered
        >
          <Stack>
            <Text>
              This hides the system; it does not delete anything. Sources, ROMs, and settings are
              preserved. Future subscription checks pause until you add this system again. Checks
              and processing already in progress can finish.
            </Text>
            <Group justify="flex-end">
              <Button
                variant="default"
                onClick={() => setConfirm(false)}
              >
                Keep system
              </Button>
              <Button
                color="red"
                loading={enable.isPending}
                onClick={() =>
                  enable.mutate(
                    {
                      systemKey,
                      enabled: false,
                    },
                    {
                      onSuccess: () => {
                        setConfirm(false);
                        navigate('/systems');
                      },
                    },
                  )
                }
              >
                Remove system
              </Button>
            </Group>
            {enable.isError && <Alert color="red">{enable.error.message}</Alert>}
          </Stack>
        </Modal>
      </Stack>
    </Paper></Stack>
  );
}
