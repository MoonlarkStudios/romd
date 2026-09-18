import { Alert, Anchor, Badge, Button, Group, Skeleton, Stack, Text, Title } from '@mantine/core';
import { Link } from 'react-router';
import workspace from '../../components/Workspace/Workspace.module.css';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import { useOperationalDiagnostics } from '../../hooks/api/useOperationalDiagnostics';
import { formatBytes } from '../../utils/format';

export function StorageCapacity() {
  const query = useOperationalDiagnostics();
  const storage = query.data?.storage;
  return <Stack className={workspace.panel} gap="sm">
    <Group justify="space-between"><Title order={2} className={workspace.sectionHeading}>Volume capacity</Title><Button {...workspaceActionProps} variant="subtle" loading={query.isFetching} onClick={() => void query.refetch()}>Refresh capacity</Button></Group>
    {query.isPending ? <Skeleton h={50} /> : <>
      {query.isError && <Alert color="orange">Could not refresh capacity.{storage ? ' Showing the previous snapshot.' : ''}</Alert>}
      {storage && <>
        <Group justify="space-between"><Text fw={600}>{storage.dataVolumeFreeBytes != null && storage.dataVolumeAvailability === 'Available' ? `${formatBytes(storage.dataVolumeFreeBytes)} free on data volume` : 'Free space unavailable'}</Text><Badge color={storage.casAvailability === 'Available' ? 'teal' : 'orange'}>Storage root {storage.casAvailability.toLowerCase()}</Badge></Group>
        <Text size="xs" c="dimmed">Snapshot {query.data ? new Date(query.data.generatedAt).toLocaleString() : 'Unknown'}. Free space is shared with other content on this volume.</Text>
      </>}
    </>}
    <Anchor component={Link} to="/diagnostics" size="sm">Inspect storage dependencies and maintenance jobs</Anchor>
  </Stack>;
}
