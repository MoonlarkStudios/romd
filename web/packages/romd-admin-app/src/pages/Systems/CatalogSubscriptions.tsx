import { Alert, Badge, Button, Drawer, Group, Loader, Paper, Stack, Text } from '@mantine/core';
import type { DatCatalogDirectoryDto, DatCatalogSubscriptionDto } from '@romd/admin-api-client';
import {
  checkCatalogSubscription,
  discoverDatCatalogs,
  getCatalogSubscription,
} from '@romd/admin-api-client';
import { useEffect, useState } from 'react';
import { DatReplacementReviewModal } from './DatReplacementReviewModal';

const labels: Record<string, string> = {
  ReadyToImport: 'Ready for first import',
  UpdateAvailable: 'Update available',
  UpToDate: 'Up to date',
  Applying: 'Applying catalog',
  CheckFailed: 'Check needs attention',
};
function failure(error: unknown): string {
  return typeof error === 'object' &&
    error !== null &&
    'detail' in error &&
    typeof error.detail === 'string'
    ? error.detail
    : 'Could not reach the catalog service. Your installed catalogs are unchanged. Try again.';
}
export function CatalogSubscriptions({
  onClose,
  onCatalogChanged,
}: {
  onClose: () => void;
  onCatalogChanged?: () => void;
}) {
  const [directory, setDirectory] = useState<DatCatalogDirectoryDto>();
  const [error, setError] = useState<string>();
  const [busy, setBusy] = useState<string>();
  const [attempt, setAttempt] = useState(0);
  const [review, setReview] = useState<DatCatalogSubscriptionDto>();
  const save = (subscription: DatCatalogSubscriptionDto) =>
    setDirectory((previous) =>
      previous
        ? {
            ...previous,
            subscriptions: [
              ...previous.subscriptions.filter((s) => s.id !== subscription.id),
              subscription,
            ],
          }
        : previous,
    );
  // biome-ignore lint/correctness/useExhaustiveDependencies: attempt intentionally triggers an explicit user retry, including identical requests.
  useEffect(() => {
    const controller = new AbortController();
    setError(undefined);
    void discoverDatCatalogs({
      signal: controller.signal,
    })
      .then((response) => {
        if (controller.signal.aborted) return;
        if (response.data) setDirectory(response.data);
        else setError(failure(response.error));
      })
      .catch(() => {
        if (!controller.signal.aborted) setError(failure(undefined));
      });
    return () => controller.abort();
  }, [
    attempt,
  ]);
  const pendingJobs =
    directory?.subscriptions
      .filter((item) => item.state === 'Applying')
      .map((item) => `${item.id}:${item.jobId ?? ''}`)
      .sort()
      .join(',') ?? '';
  useEffect(() => {
    if (!pendingJobs) return;
    const pending = pendingJobs.split(',').map((item) => {
      const [id, jobId] = item.split(':');
      return {
        id,
        jobId,
      };
    });
    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout> | undefined;
    const poll = async () => {
      const results = await Promise.allSettled(
        pending.map((item) =>
          getCatalogSubscription({
            path: {
              subscriptionId: item.id,
            },
            signal: controller.signal,
          }),
        ),
      );
      if (controller.signal.aborted) return;
      const statuses = results.flatMap((result) =>
        result.status === 'fulfilled' && result.value.data
          ? [
              result.value.data,
            ]
          : [],
      );
      setDirectory((previous) => {
        if (!previous) return previous;
        let changed = false;
        const subscriptions = previous.subscriptions.map((item) => {
          const latest = statuses.find(
            (status) => status.id === item.id && status.jobId === item.jobId,
          );
          if (
            item.state === 'Applying' &&
            latest &&
            (latest.state !== item.state ||
              latest.activeDatId !== item.activeDatId ||
              latest.message !== item.message)
          ) {
            changed = true;
            return latest;
          }
          return item;
        });
        return changed
          ? {
              ...previous,
              subscriptions,
            }
          : previous;
      });
      if (
        statuses.some(
          (status) =>
            status.state !== 'Applying' &&
            pending.some((item) => item.id === status.id && item.jobId === status.jobId),
        )
      )
        onCatalogChanged?.();
      if (!controller.signal.aborted) timer = setTimeout(() => void poll(), 3000);
    };
    void poll();
    return () => {
      controller.abort();
      if (timer) clearTimeout(timer);
    };
  }, [
    pendingJobs,
    onCatalogChanged,
  ]);

  const check = async (catalogId: string) => {
    setBusy(catalogId);
    setError(undefined);
    try {
      const response = await checkCatalogSubscription({
        body: {
          catalogId,
        },
      });
      if (response.data) save(response.data);
      else setError(failure(response.error));
    } catch {
      setError(failure(undefined));
    } finally {
      setBusy(undefined);
    }
  };
  const refresh = async (subscription: DatCatalogSubscriptionDto) => {
    setBusy(subscription.catalogId);
    setError(undefined);
    try {
      const response = await getCatalogSubscription({
        path: {
          subscriptionId: subscription.id,
        },
      });
      if (response.data) save(response.data);
      else setError(failure(response.error));
    } catch {
      setError(failure(undefined));
    } finally {
      setBusy(undefined);
    }
  };
  const ids = new Set([
    ...(directory?.catalogs.map((c) => c.catalogId) ?? []),
    ...(directory?.subscriptions.map((s) => s.catalogId) ?? []),
  ]);
  return (
    <Drawer
      opened
      onClose={onClose}
      title="Catalog subscriptions"
      size="lg"
    >
      <Stack>
        <Text>
          Get complete source catalogs from the ROMD mirror. Review each candidate before importing
          or replacing your installed catalog.
        </Text>
        <Text
          size="sm"
          c="dimmed"
        >
          The publisher checks upstream daily. Your checks use the mirror and do not request another
          upstream download.
        </Text>
        {error && (
          <Alert
            color="red"
            title="Could not finish"
          >
            {error}
          </Alert>
        )}
        {directory?.message && <Alert color="yellow">{directory.message}</Alert>}
        {!directory && !error && (
          <Group>
            <Loader size="sm" />
            <Text>Checking available catalogs…</Text>
          </Group>
        )}
        {directory && !directory.enabled && (
          <Alert>Catalog updates are not configured on this ROMD server.</Alert>
        )}
        {directory?.enabled && ids.size === 0 && (
          <Alert title="No published catalogs yet">
            System definitions do not mean a DAT is available. Available catalogs will appear here
            after the publisher verifies and publishes them.
          </Alert>
        )}
        {[
          ...ids,
        ].map((id) => {
          const catalog = directory?.catalogs.find((c) => c.catalogId === id);
          const subscription = directory?.subscriptions.find((s) => s.catalogId === id);
          const healthy = catalog?.health === 'healthy';
          const applying = subscription?.state === 'Applying';
          return (
            <Paper
              key={id}
              withBorder
              p="md"
            >
              <Stack gap="sm">
                <Group justify="space-between">
                  <Text fw={600}>{catalog?.name ?? subscription?.name}</Text>
                  <Badge color={subscription?.state === 'CheckFailed' ? 'red' : 'blue'}>
                    {subscription
                      ? (labels[subscription.state] ?? subscription.state)
                      : 'Available'}
                  </Badge>
                </Group>
                {catalog && (
                  <Text size="sm">
                    Complete catalog · {catalog.entryCount.toLocaleString()} entries ·{' '}
                    {catalog.fileCount.toLocaleString()} files
                  </Text>
                )}
                {catalog?.lastChangedAt && (
                  <Text
                    size="xs"
                    c="dimmed"
                  >
                    Published document changed {new Date(catalog.lastChangedAt).toLocaleString()}
                  </Text>
                )}
                {subscription?.lastCheckedAt && (
                  <Text
                    size="xs"
                    c="dimmed"
                  >
                    Last checked {new Date(subscription.lastCheckedAt).toLocaleString()}
                  </Text>
                )}
                {!healthy && (
                  <Alert color="yellow">
                    The publisher cannot currently offer a verified update. Your installed catalog
                    remains available. Retry later.
                  </Alert>
                )}
                {subscription?.message && <Alert color="yellow">{subscription.message}</Alert>}
                <Group>
                  <Button
                    variant="light"
                    loading={busy === id}
                    disabled={busy != null || !directory?.enabled || applying}
                    onClick={() => void check(id)}
                  >
                    {!subscription
                      ? 'Subscribe and check'
                      : subscription.state === 'CheckFailed'
                        ? 'Retry check'
                        : 'Check for updates'}
                  </Button>
                  {subscription &&
                    [
                      'ReadyToImport',
                      'UpdateAvailable',
                    ].includes(subscription.state) && (
                      <Button
                        disabled={busy != null}
                        onClick={() => setReview(subscription)}
                      >
                        {subscription.state === 'ReadyToImport'
                          ? 'Review first import'
                          : 'Review update'}
                      </Button>
                    )}
                  {applying && subscription && (
                    <Button
                      variant="subtle"
                      disabled={busy != null}
                      onClick={() => void refresh(subscription)}
                    >
                      Refresh status
                    </Button>
                  )}
                  {subscription?.jobId && (
                    <Button
                      component="a"
                      href={`/jobs/${subscription.jobId}`}
                      variant="subtle"
                    >
                      View job
                    </Button>
                  )}
                </Group>
              </Stack>
            </Paper>
          );
        })}
        <Button
          variant="default"
          disabled={busy != null}
          onClick={() => setAttempt((value) => value + 1)}
        >
          Refresh available catalogs
        </Button>
        {review && (
          <DatReplacementReviewModal
            subscription={review}
            onClose={() => setReview(undefined)}
            onAccepted={(result) => {
              save({
                ...review,
                state: 'Applying',
                jobId: result.jobId,
              });
              setReview(undefined);
            }}
          />
        )}
      </Stack>
    </Drawer>
  );
}
