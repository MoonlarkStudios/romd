import { Alert, Button, Group, Paper, Stack, Text } from '@mantine/core';
import type { Dat } from '@romd/admin-api-client';
import { createDatSubscription } from '@romd/admin-api-client';
import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { managedSystemKeys } from '../../hooks/api/useManagedSystems';

/** Subscribes this installed source to its publisher catalog; never binds by a system-wide guess. */
export function DatSubscriptionCard({
  dat,
  catalogId,
}: {
  dat: Pick<Dat, 'id'>;
  catalogId: string;
}) {
  const client = useQueryClient();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const subscribe = async () => {
    setBusy(true);
    setError(undefined);
    try {
      const response = await createDatSubscription({
        body: {
          catalogId,
          datId: dat.id,
        },
      });
      if (response.error || !response.data)
        throw new Error(
          response.error?.detail ??
            'The subscription could not be connected. Your current catalog is unchanged. Try again shortly.',
        );
      // The source workspace renders the new subscription's review and update actions.
      await client.invalidateQueries({
        queryKey: managedSystemKeys.subscriptions,
      });
      // Enrollment is idempotent on the catalog, so it can return a pending subscription without a source.
      if (response.data.activeDatId !== dat.id)
        setError(
          'This catalog is already tracked by a pending subscription. Review or remove it from Sources first.',
        );
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Unable to reach ROMD. Try again.');
    } finally {
      setBusy(false);
    }
  };
  return (
    <Paper
      withBorder
      p="md"
    >
      <Stack gap="sm">
        <Text fw={600}>Connect a subscription</Text>
        <Text
          size="sm"
          c="dimmed"
        >
          Use the compatible subscription catalog to update this source. ROMD checks daily, and
          changes require review before activation.
        </Text>
        {error && <Alert color="red">{error}</Alert>}
        <Group>
          <Button
            variant="light"
            loading={busy}
            onClick={() => void subscribe()}
          >
            Use subscription
          </Button>
        </Group>
      </Stack>
    </Paper>
  );
}
