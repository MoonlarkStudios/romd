import { Alert, Badge, Button, Group, Modal, Stack, Text } from '@mantine/core';
import type { DatCatalogSubscriptionDto, UploadAccepted } from '@romd/admin-api-client';
import {
  checkCatalogSubscription,
  getCatalogSubscription,
  stopCatalogSubscription,
} from '@romd/admin-api-client';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useRef, useState } from 'react';
import { Link } from 'react-router';
import { managedSystemKeys } from '../../hooks/api/useManagedSystems';
import { DatReplacementReviewModal } from './DatReplacementReviewModal';

/** Status polling reads only this local subscription, never the remote publisher. */
export function SourceSubscriptionActions({
  subscription,
  onAccepted,
  manage = false,
  compact = false,
  settingsOnly = false,
}: {
  subscription: DatCatalogSubscriptionDto;
  manage?: boolean;
  compact?: boolean;
  settingsOnly?: boolean;
  onAccepted: (result: UploadAccepted) => void;
}) {
  const client = useQueryClient();
  const [review, setReview] = useState(false);
  const [checking, setChecking] = useState(false);
  const [stopping, setStopping] = useState(false);
  const [confirmStop, setConfirmStop] = useState(false);
  const [error, setError] = useState<string>();
  const status = useQuery({
    queryKey: [
      'source-subscription',
      subscription.id,
    ],
    queryFn: async ({ signal }) => {
      const response = await getCatalogSubscription({
        path: {
          subscriptionId: subscription.id,
        },
        signal,
      });
      if (response.error || !response.data) throw new Error('Update status could not be loaded.');
      return response.data;
    },
    initialData: subscription,
    refetchInterval: (q) => (q.state.data?.state === 'Applying' ? 3000 : 60000),
  });
  const current = status.data;
  const previousState = useRef(current.state);
  useEffect(() => {
    const completed = previousState.current === 'Applying' && current.state !== 'Applying';
    previousState.current = current.state;
    if (!completed) return;
    void client.invalidateQueries({
      queryKey: [
        'dats',
      ],
    });
    void client.invalidateQueries({
      queryKey: managedSystemKeys.all,
    });
    void client.invalidateQueries({
      queryKey: managedSystemKeys.catalogs,
    });
  }, [
    current.state,
    client,
  ]);
  const ready = current.state === 'ReadyToImport' || current.state === 'UpdateAvailable';
  const check = async () => {
    setChecking(true);
    setError(undefined);
    try {
      const response = await checkCatalogSubscription({
        body: {
          catalogId: current.catalogId,
        },
      });
      if (response.error || !response.data)
        throw new Error('The update check could not finish. Your installed source is unchanged.');
      client.setQueryData(
        [
          'source-subscription',
          current.id,
        ],
        response.data,
      );
      void client.invalidateQueries({
        queryKey: managedSystemKeys.catalogs,
      });
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Unable to check this source. Try again.');
    } finally {
      setChecking(false);
    }
  };
  const label = ready
    ? current.activeDatId
      ? 'Update available'
      : 'Ready for review'
    : current.state === 'Applying'
      ? 'Processing update'
      : current.state === 'CheckFailed'
        ? 'Check needs attention'
        : 'Up to date';
  return (
    <Stack gap="sm">
      <Group>
        <Badge
          color={
            ready || current.state === 'CheckFailed'
              ? 'orange'
              : current.state === 'Applying'
                ? 'blue'
                : 'gray'
          }
        >
          {label}
        </Badge>
        {!compact && current.lastCheckedAt && (
          <Text
            size="xs"
            c="dimmed"
          >
            Checked {new Date(current.lastCheckedAt).toLocaleString()}
          </Text>
        )}
      </Group>
      {!compact && <Text
        size="xs"
        c="dimmed"
      >
        {current.automaticChecksPaused
          ? 'Automatic checks are paused. Restore the system or enable its source and server subscriptions to resume.'
          : ready
            ? 'Waiting for your review. Automatic checks resume after activation.'
            : current.state === 'Applying'
              ? 'Automatic checks resume after processing finishes.'
              : current.nextCheckAt
                ? `${current.state === 'CheckFailed' ? 'Automatic retry' : 'Next automatic check'}: ${new Date(current.nextCheckAt).toLocaleString()}. Updates always require review.`
                : 'Checked daily. Updates always require review.'}
      </Text>}
      {(error || current.message || status.isError) && (
        <Alert color="yellow">
          {error ?? current.message ?? 'Status could not be loaded.'}
          {current.activeDatId && ' Your installed document is unchanged.'}
          {status.isError && (
            <Button
              variant="subtle"
              onClick={() => void status.refetch()}
            >
              Retry status
            </Button>
          )}
        </Alert>
      )}
      {!settingsOnly && <Group>
        {ready && (
          <Button
            variant="light"
            onClick={() => setReview(true)}
            disabled={checking}
          >
            {current.activeDatId ? 'Review update' : 'Review source'}
          </Button>
        )}
        {!compact && <Button
          variant="subtle"
          loading={checking}
          disabled={current.state === 'Applying'}
          onClick={() => void check()}
        >
          {current.state === 'CheckFailed' ? 'Retry check' : 'Check for updates'}
        </Button>}
        {current.jobId && (
          <Button
            component={Link}
            to={`/jobs/${current.jobId}`}
            variant="subtle"
          >
            View job
          </Button>
        )}
      </Group>}
      {manage && (
        <Button
          variant="subtle"
          color="gray"
          disabled={current.state === 'Applying' || checking}
          onClick={() => setConfirmStop(true)}
        >
          {current.activeDatId ? 'Switch to manual updates' : 'Remove pending subscription'}
        </Button>
      )}
      <Modal
        opened={confirmStop}
        onClose={() => setConfirmStop(false)}
        title={current.activeDatId ? 'Switch to manual updates?' : 'Remove pending subscription?'}
        centered
      >
        <Stack>
          <Text>
            Your installed source, DAT versions, and ROMs stay saved. Subscription tracking and its
            pending update selection are removed. You can connect a subscription again later.
          </Text>
          <Group justify="flex-end">
            <Button
              variant="default"
              onClick={() => setConfirmStop(false)}
            >
              Keep subscription
            </Button>
            <Button
              loading={stopping}
              onClick={async () => {
                setStopping(true);
                setError(undefined);
                try {
                  const response = await stopCatalogSubscription({
                    path: {
                      subscriptionId: current.id,
                    },
                  });
                  if (response.error)
                    throw new Error(
                      'Could not change update settings. Processing may still be running; try again after it finishes.',
                    );
                  await client.invalidateQueries({
                    queryKey: managedSystemKeys.catalogs,
                  });
                  setConfirmStop(false);
                } catch (e) {
                  setError(e instanceof Error ? e.message : 'Could not change update settings.');
                } finally {
                  setStopping(false);
                }
              }}
            >
              Confirm change
            </Button>
          </Group>
          {error && <Alert color="red">{error}</Alert>}
        </Stack>
      </Modal>
      {review && (
        <DatReplacementReviewModal
          subscription={current}
          onClose={() => setReview(false)}
          onAccepted={(result) => {
            setReview(false);
            client.setQueryData(
              [
                'source-subscription',
                current.id,
              ],
              {
                ...current,
                state: 'Applying',
                jobId: result.jobId,
              },
            );
            void client.invalidateQueries({
              queryKey: managedSystemKeys.catalogs,
            });
            onAccepted(result);
          }}
        />
      )}
    </Stack>
  );
}
