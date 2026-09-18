import { Alert, Badge, Button, Group, Stack, Text } from '@mantine/core';
import { Link } from 'react-router';
import { useManagedSystems, useSetupCatalogs } from '../../hooks/api/useManagedSystems';
import { systemAttention } from './systemAttention';
import { systemStateColors, systemStateLabels } from './systemState';

/** Availability is independent of individual source checks or candidate versions. */
export function SystemSetupSummary({ systemKey }: { systemKey: string }) {
  const systems = useManagedSystems();
  const catalogs = useSetupCatalogs();
  const system = systems.data?.find((s) => s.key === systemKey);
  if (!system)
    return systems.isError ? (
      <Alert color="red">
        System status could not be loaded.
        <Button onClick={() => void systems.refetch()}>Retry</Button>
      </Alert>
    ) : null;
  const { updates, failed: attention } = systemAttention(systemKey, catalogs.data?.subscriptions ?? []);
  return (
    <Stack gap="xs">
      <Group>
        <Badge color={systemStateColors[system.state]}>
          {systemStateLabels[system.state] ?? system.state}
        </Badge>
        <Text size="sm">
          {system.catalogCount} installed {system.catalogCount === 1 ? 'source' : 'sources'} ·{' '}
          {(system.trackedTitles ?? 0).toLocaleString()} tracked titles · {system.ownedTitles.toLocaleString()} titles with files
        </Text>
        {system.state === 'NeedsCatalog' && <Button component={Link} to={`/systems/${systemKey}?tab=sources`} variant="subtle" color="teal" size="compact-sm">Add a source</Button>}
        {updates > 0 && (
          <Text
            component={Link}
            to={`/systems/${systemKey}?tab=sources`}
            size="sm"
            c="orange"
          >
            {updates} {updates === 1 ? 'update' : 'updates'} to review
          </Text>
        )}
        {attention > 0 && (
          <Text
            component={Link}
            to={`/systems/${systemKey}?tab=sources`}
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
      {(catalogs.isError || catalogs.data?.message) && <Text size="xs" c="dimmed">Subscription status unavailable</Text>}
    </Stack>
  );
}
