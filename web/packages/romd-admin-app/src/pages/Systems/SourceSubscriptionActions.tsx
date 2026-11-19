import { Alert, Badge, Button, Group, Modal, Stack, Text } from '@mantine/core';
import type { DatCatalogSubscription, JobReference } from '@romd/admin-api-client';
import {
  checkDatSubscription,
  deleteDatSubscription,
  getDatSubscription,
} from '@romd/admin-api-client';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useRef, useState } from 'react';
import { Link } from 'react-router';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import { datKeys } from '../../hooks/api/useDats';
import { managedSystemKeys } from '../../hooks/api/useManagedSystems';
import { DatReplacementReviewModal, type DatReviewOutcome } from './DatReplacementReviewModal';

/** Status polling reads only this local subscription, never the remote publisher. */
export function SourceSubscriptionActions({
  subscription,
  systemName,
  provider,
  onAccepted,
  manage = false,
  compact = false,
  settingsOnly = false,
  primaryOnly = false,
  onReview,
  reviewHref,
}: {
  subscription: DatCatalogSubscription;
  systemName?: string;
  provider?: string;
  manage?: boolean;
  compact?: boolean;
  settingsOnly?: boolean;
  primaryOnly?: boolean;
  /** Opens the review in the caller's page instead of a dialog. */
  onReview?: () => void;
  /** Links to the source page's review instead of opening a dialog here. */
  reviewHref?: string;
  onAccepted: (result: JobReference, outcome?: DatReviewOutcome) => void;
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
      const response = await getDatSubscription({
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
    if (previousState.current === current.state) return;
    const completed = previousState.current === 'Applying';
    previousState.current = current.state;
    void client.invalidateQueries({
      queryKey: managedSystemKeys.subscriptions,
    });
    if (!completed) return;
    void client.invalidateQueries({
      queryKey: datKeys.all,
    });
    void client.invalidateQueries({
      queryKey: managedSystemKeys.all,
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
      const response = await checkDatSubscription({
        path: {
          subscriptionId: current.id,
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
        queryKey: managedSystemKeys.subscriptions,
      });
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Unable to check this source. Try again.');
    } finally {
      setChecking(false);
    }
  };
  const applying = current.state === 'Applying';
  const failed = current.state === 'CheckFailed';
  const paused = current.automaticChecksPaused;
  const canCheck =
    !settingsOnly &&
    !applying &&
    !paused &&
    (primaryOnly ? failed || current.state === 'NotChecked' : !compact || !ready);
  const label = checking
    ? 'Checking for updates'
    : ready
      ? current.activeDatId
        ? 'Update ready'
        : 'First DAT ready'
      : applying
        ? 'Import in progress'
        : paused
          ? 'Checks paused'
          : failed
            ? 'Check failed'
            : current.state === 'NotChecked'
              ? 'Not checked'
              : 'Up to date';
  return (
    <Stack gap={compact ? 6 : 'sm'}>
      {!primaryOnly && (
        <Group>
          <Badge
            role="status"
            color={ready || failed ? 'orange' : applying || checking ? 'blue' : 'gray'}
          >
            {label}
          </Badge>
          {compact && failed && !paused && current.nextCheckAt && (
            <Text
              size="xs"
              c="dimmed"
            >
              Next retry {new Date(current.nextCheckAt).toLocaleString()}
            </Text>
          )}
          {!compact && current.lastCheckedAt && (
            <Text
              size="xs"
              c="dimmed"
            >
              Checked {new Date(current.lastCheckedAt).toLocaleString()}
            </Text>
          )}
        </Group>
      )}
      {!compact && !primaryOnly && (
        <Text
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
        </Text>
      )}
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
      {!settingsOnly && (
        <Group>
          {ready &&
            (reviewHref && !checking ? (
              <Button
                {...(primaryOnly
                  ? workspaceActionProps
                  : {
                      variant: 'light',
                    })}
                component={Link}
                to={reviewHref}
                size={compact ? 'compact-sm' : 'sm'}
              >
                {current.activeDatId ? 'Review update' : 'Review first DAT'}
              </Button>
            ) : (
              <Button
                {...(primaryOnly
                  ? workspaceActionProps
                  : {
                      variant: 'light',
                    })}
                size={compact ? 'compact-sm' : 'sm'}
                onClick={() => (onReview ? onReview() : setReview(true))}
                disabled={checking}
              >
                {current.activeDatId ? 'Review update' : 'Review first DAT'}
              </Button>
            ))}
          {canCheck && (
            <Button
              {...(primaryOnly
                ? workspaceActionProps
                : {
                    variant: 'subtle',
                  })}
              size={compact ? 'compact-sm' : 'sm'}
              loading={checking}
              onClick={() => void check()}
            >
              {failed
                ? 'Retry check'
                : primaryOnly && !current.activeDatId
                  ? 'Check for first DAT'
                  : 'Check for updates'}
            </Button>
          )}
          {current.jobId && (applying || (!primaryOnly && (!compact || failed))) && (
            <Button
              component={Link}
              to={`/jobs/${current.jobId}`}
              variant="subtle"
            >
              {applying ? 'View progress' : 'View last job'}
            </Button>
          )}
        </Group>
      )}
      {manage && (
        <Button
          variant="subtle"
          color="gray"
          w="fit-content"
          size={compact ? 'compact-sm' : 'sm'}
          disabled={applying || checking}
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
                  const response = await deleteDatSubscription({
                    path: {
                      subscriptionId: current.id,
                    },
                  });
                  if (response.error)
                    throw new Error(
                      'Could not change update settings. Processing may still be running; try again after it finishes.',
                    );
                  await Promise.all([
                    client.invalidateQueries({
                      queryKey: managedSystemKeys.subscriptions,
                    }),
                    client.invalidateQueries({
                      queryKey: datKeys.all,
                    }),
                  ]);
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
          systemName={systemName}
          provider={provider}
          onClose={() => setReview(false)}
          onAccepted={(result, outcome) => {
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
              queryKey: managedSystemKeys.subscriptions,
            });
            onAccepted(result, outcome);
          }}
        />
      )}
    </Stack>
  );
}
