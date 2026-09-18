import { Anchor, Badge, Button, Group, SimpleGrid, Stack, Table, Text, Title } from '@mantine/core';
import { IconRefresh, IconUpload } from '@tabler/icons-react';
import { Link } from 'react-router';
import workspace from '../../components/Workspace/Workspace.module.css';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import { OverviewJobs } from './OverviewJobs';
import { OverviewSection } from './OverviewSection';
import { useOverview } from './useOverview';

export function Dashboard() {
  const model = useOverview();
  const { health, enrichment, collection, running, queued, failed, audit, permissions } = model;
  const issues = [
    { label: 'Unidentified files', count: health.data?.unidentifiedCount ?? 0, to: '/roms?status=unidentified', scope: 'Files', visible: permissions.canUploadRoms },
    { label: 'Files without a destination', count: health.data?.unroutedCount ?? 0, to: '/roms?status=unrouted', scope: 'Files', visible: permissions.canUploadRoms },
    { label: 'Metadata requests failed', count: Number(enrichment.data?.failed ?? 0), to: '/catalog?view=tracked&enrichmentStatus=Failed', scope: 'Tracked titles', visible: permissions.canManageTitles },
    { label: 'Metadata match not found', count: Number(enrichment.data?.notFound ?? 0), to: '/catalog?view=tracked&enrichmentStatus=NotFound', scope: 'Tracked titles', visible: permissions.canManageTitles },
    { label: 'Metadata match needs review', count: Number(enrichment.data?.lowConfidence ?? 0), to: '/catalog?view=tracked&enrichmentStatus=LowConfidence', scope: 'Tracked titles', visible: permissions.canManageTitles },
  ].filter(issue => issue.visible && issue.count > 0);
  const attention = {
    isPending: health.isPending || enrichment.isPending || failed.isPending,
    isError: health.isError || enrichment.isError || failed.isError,
    dataUpdatedAt: Math.min(health.dataUpdatedAt, enrichment.dataUpdatedAt, failed.dataUpdatedAt),
    refetch: () => Promise.allSettled([health.refetch(), enrichment.refetch(), failed.refetch()]),
  };
  const stats = collection.data;
  return <Stack className={workspace.page} gap="lg">
    <Group className={workspace.hero} justify="space-between"><Title order={1} className={workspace.heading}>Dashboard</Title><Group gap="sm"><Button variant="default" leftSection={<IconRefresh size={16} />} loading={model.refreshing} onClick={() => void model.refresh()}>Refresh</Button>{permissions.canUploadRoms && <Button component={Link} to="/roms/import" {...workspaceActionProps} leftSection={<IconUpload size={16} />}>Import files</Button>}</Group></Group>
    <OverviewSection title="Needs attention" query={attention}>
      {!issues.length && !failed.data?.items.length ? <Group><Badge color="teal" variant="light">No outstanding items</Badge><Text size="sm" c="dimmed">No issues in the file, metadata, and failed-job summaries available to you.</Text></Group> : <Stack>
        {!!issues.length && <Table.ScrollContainer minWidth={430}><Table verticalSpacing="sm"><Table.Thead><Table.Tr><Table.Th>Issue</Table.Th><Table.Th>Scope</Table.Th><Table.Th>Count</Table.Th><Table.Th>Action</Table.Th></Table.Tr></Table.Thead><Table.Tbody>{issues.map(issue => <Table.Tr key={issue.label}><Table.Td>{issue.label}</Table.Td><Table.Td><Text size="sm" c="dimmed">{issue.scope}</Text></Table.Td><Table.Td><Badge color="orange" variant="light">{issue.count.toLocaleString()}</Badge></Table.Td><Table.Td><Anchor component={Link} to={issue.to} size="sm" aria-label={`Review ${issue.label.toLowerCase()}`}>Review</Anchor></Table.Td></Table.Tr>)}</Table.Tbody></Table></Table.ScrollContainer>}
        {!!failed.data?.items.length && <Stack gap="xs"><Group justify="space-between"><Text fw={600} size="sm">Recent failed jobs</Text><Anchor component={Link} to="/jobs?outcome=failed" size="sm">View failed jobs</Anchor></Group><OverviewJobs jobs={failed.data.items} />{failed.data.nextCursor && <Text size="xs" c="dimmed">Showing the latest five. More failed jobs are available in Jobs.</Text>}</Stack>}
      </Stack>}
    </OverviewSection>
    <SimpleGrid cols={{ base: 1, lg: 2 }}>
      <OverviewSection title="Running" query={running} action={<Anchor component={Link} to="/jobs?outcome=running" size="sm">View running jobs</Anchor>}>{running.data?.items.length ? <OverviewJobs jobs={running.data.items} /> : <Text c="dimmed" size="sm">No jobs are running.</Text>}{running.data?.nextCursor && <Text size="xs" c="dimmed">Showing the latest five running jobs.</Text>}</OverviewSection>
      <OverviewSection title="Queued" query={queued} action={<Anchor component={Link} to="/jobs?outcome=queued" size="sm">View queue</Anchor>}>{queued.data?.items.length ? <OverviewJobs jobs={queued.data.items} /> : <Text c="dimmed" size="sm">The queue is empty.</Text>}{queued.data?.nextCursor && <Text size="xs" c="dimmed">Showing the latest five queued jobs.</Text>}</OverviewSection>
    </SimpleGrid>
    <OverviewSection title="Collection progress" query={collection} action={<Anchor component={Link} to="/catalog?view=tracked" size="sm">Open tracked catalog</Anchor>}>
      {Number(stats?.trackedTitleCount ?? 0) === 0 ? <Text c="dimmed">Track titles in Catalog to define your collection goals.</Text> : <SimpleGrid cols={{ base: 1, sm: 3 }}>
        <Stack gap={4}><Text size="sm" c="dimmed">Tracked titles with a complete release</Text><Text size="xl" fw={650}>{Number(stats?.satisfiedTitleCount ?? 0).toLocaleString()} / {Number(stats?.trackedTitleCount ?? 0).toLocaleString()}</Text><Text size="sm">{Number(stats?.completionPercent ?? 0).toFixed(1)}% complete</Text></Stack>
        <Stack gap={4}><Text size="sm" c="dimmed">Without a complete release</Text><Text size="xl" fw={650}>{Number(stats?.missingTitleCount ?? 0).toLocaleString()}</Text><Anchor component={Link} to="/catalog?view=missing" size="sm">Review missing releases</Anchor></Stack>
        <Stack gap={4}><Text size="sm" c="dimmed">Preferred release missing</Text><Text size="xl" fw={650}>{Number(stats?.upgradeTitleCount ?? 0).toLocaleString()}</Text><Anchor component={Link} to="/catalog?view=upgrades" size="sm">Review preferred releases</Anchor></Stack>
      </SimpleGrid>}
    </OverviewSection>
    <OverviewSection title="Metadata quality" query={enrichment} action={<Anchor component={Link} to="/catalog?view=tracked" size="sm">Open catalog</Anchor>}><Group gap="xl"><Text><strong>{Number(enrichment.data?.completed ?? 0).toLocaleString()}</strong> successfully enriched</Text><Text><strong>{Number(enrichment.data?.pending ?? 0).toLocaleString()}</strong> pending</Text><Text c="dimmed" size="sm">Tracked titles only. Failed attempts and unresolved matches are excluded from success.</Text></Group></OverviewSection>
    {permissions.canManageUsers && <OverviewSection title="Recent administrative changes" query={audit} action={<Anchor component={Link} to="/audit" size="sm">Open audit log</Anchor>}>
      {audit.data?.items.length ? <Stack gap="sm">{audit.data.items.slice(0, 5).map(item => <Group key={item.id} justify="space-between" align="start"><div><Anchor component={Link} to={`/audit?type=${encodeURIComponent(item.targetType)}`} size="sm">{item.action}</Anchor><Text size="xs" c="dimmed">{item.actorEmail ?? 'System or deleted account'} · {item.targetType}</Text></div><Text size="xs" c="dimmed">{new Date(item.occurredAt).toLocaleString()}</Text></Group>)}</Stack> : <Text size="sm" c="dimmed">No administrative changes recorded yet.</Text>}
    </OverviewSection>}
  </Stack>;
}
