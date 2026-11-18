import { Alert, Badge, Button, Group, Stack, Text } from '@mantine/core';
import { Link, useLocation } from 'react-router';
import { useDatSubscriptions, useManagedSystems } from '../../hooks/api/useManagedSystems';
import { usePermissions } from '../../hooks/usePermissions';
import { DatImportDate, datCountLabel } from './systemMetrics';
import { systemStateColors, systemStateLabels } from './systemState';
import { systemSubscriptionStatus } from './systemsListStatus';

/** Availability is independent of individual source checks or candidate versions. */
export function SystemSetupSummary({
  systemKey,
  viewingSources = false,
}: {
  systemKey: string;
  viewingSources?: boolean;
}) {
  const systems = useManagedSystems();
  const subscriptions = useDatSubscriptions();
  const { role } = usePermissions();
  const { state } = useLocation();
  const system = systems.data?.find((s) => s.key === systemKey);
  if (!system)
    return systems.isError ? (
      <Alert color="red">
        System status could not be loaded.
        <Button onClick={() => void systems.refetch()}>Retry</Button>
      </Alert>
    ) : null;
  const sourcesPath = `/systems/${systemKey}?tab=sources`;
  const related = (subscriptions.data ?? []).filter(
    (subscription) => subscription.systemKey === systemKey,
  );
  const status = systemSubscriptionStatus(related);
  const firstDats = status.firstDats.length;
  const updates = status.updates.length;
  const attention = status.failed.length;
  return (
    <Stack gap="xs">
      <Group>
        <Badge color={systemStateColors[system.state]}>
          {systemStateLabels[system.state] ?? system.state}
        </Badge>
        <Text size="sm">
          {datCountLabel(system.catalogCount)} · {(system.knownTitles ?? 0).toLocaleString()} known
          titles · {system.ownedTitles.toLocaleString()} titles with files
          {system.lastDatImportedAt && (
            <>
              {' '}
              · Last DAT import <DatImportDate value={system.lastDatImportedAt} />
            </>
          )}
        </Text>
        {!viewingSources &&
          role === 'Admin' &&
          system.enabled &&
          system.state === 'NeedsCatalog' &&
          !related.length &&
          subscriptions.isSuccess && (
            <Button
              component={Link}
              to={`${sourcesPath}&setup=1`}
              state={state}
              variant="subtle"
              color="teal"
              size="compact-sm"
            >
              Set up catalog
            </Button>
          )}
        {!viewingSources && firstDats > 0 && (
          <Text
            component={Link}
            to={sourcesPath}
            state={state}
            size="sm"
            c="orange"
          >
            {firstDats === 1 ? 'First DAT ready for review' : `${firstDats} new DATs to review`}
          </Text>
        )}
        {!viewingSources && updates > 0 && (
          <Text
            component={Link}
            to={sourcesPath}
            state={state}
            size="sm"
            c="orange"
          >
            {updates} {updates === 1 ? 'update' : 'updates'} to review
          </Text>
        )}
        {!viewingSources && attention > 0 && (
          <Text
            component={Link}
            to={sourcesPath}
            state={state}
            size="sm"
            c="orange"
          >
            {attention} {attention === 1 ? 'source needs' : 'sources need'} attention
          </Text>
        )}
      </Group>
      {!system.enabled && (
        <Alert color="gray">
          This system is not in Your systems. Its sources, ROMs, and settings are preserved. Add it
          again in Settings.
        </Alert>
      )}
      {system.message && <Alert color="yellow">{system.message}</Alert>}
      {(system.state === 'Processing' || system.state === 'NeedsAttention') && (
        <Button
          component={Link}
          to="/jobs"
          variant="subtle"
          style={{
            alignSelf: 'flex-start',
          }}
        >
          View processing details
        </Button>
      )}
      {!viewingSources && subscriptions.isError && (
        <Text
          size="xs"
          c="dimmed"
        >
          Subscription status unavailable
        </Text>
      )}
    </Stack>
  );
}
