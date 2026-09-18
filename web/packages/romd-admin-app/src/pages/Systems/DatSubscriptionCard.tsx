import { Alert, Badge, Button, Group, Paper, Stack, Text } from '@mantine/core';
import type { Dat, UploadAccepted } from '@romd/admin-api-client';
import { checkDatSubscription, getDatSubscription } from '@romd/admin-api-client';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { managedSystemKeys } from '../../hooks/api/useManagedSystems';
import { DatReplacementReviewModal } from './DatReplacementReviewModal';

/** Explicitly connect a compatible installed source; never bind by a system-wide guess. */
export function DatSubscriptionCard({
  dat,
  onAccepted,
}: {
  dat: Dat;
  onAccepted: (result: UploadAccepted) => void;
}) {
  const client = useQueryClient();
  const key = [
    'dat-subscription',
    dat.id,
  ];
  const status = useQuery({
    queryKey: key,
    queryFn: async ({ signal }) => {
      const response = await getDatSubscription({
        path: {
          datId: dat.id,
        },
        signal,
      });
      if (response.response?.status === 403) return null;
      if (response.error || !response.data)
        throw new Error(
          'Unable to load catalog update status. Retry when the server is available.',
        );
      return response.data;
    },
    retry: false,
  });
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const [review, setReview] = useState(false);
  if (!status.data?.available && !status.isError) return null;
  const current = status.data;
  const check = async () => {
    setBusy(true);
    setError(undefined);
    try {
      const response = await checkDatSubscription({
        path: {
          datId: dat.id,
        },
      });
      if (response.error || !response.data)
        throw new Error(
          'The check could not finish. Your current catalog is unchanged. Try again shortly.',
        );
      client.setQueryData(key, response.data);
      void client.invalidateQueries({
        queryKey: managedSystemKeys.catalogs,
      });
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
        {current?.state === 'UpdateAvailable' && <Badge>Update available</Badge>}
        {current?.state === 'UpToDate' && <Badge color="green">Up to date</Badge>}
        {(error || status.error || current?.message) && (
          <Alert color="red">{error ?? status.error?.message ?? current?.message}</Alert>
        )}
        <Group>
          {status.isError ? (
            <Button onClick={() => void status.refetch()}>Retry status</Button>
          ) : (
            <Button
              variant="light"
              loading={busy}
              disabled={current?.state === 'Applying'}
              onClick={() => void check()}
            >
              {!current?.subscribed
                ? 'Use subscription'
                : current.state === 'CheckFailed'
                  ? 'Retry check'
                  : 'Check for updates'}
            </Button>
          )}
          {current?.state === 'UpdateAvailable' && (
            <Button onClick={() => setReview(true)}>Review update</Button>
          )}
        </Group>
        {review && (
          <DatReplacementReviewModal
            dat={dat}
            onClose={() => setReview(false)}
            onAccepted={(result) => {
              setReview(false);
              onAccepted(result);
            }}
          />
        )}
      </Stack>
    </Paper>
  );
}
