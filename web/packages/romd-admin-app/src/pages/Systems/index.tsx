import { Alert, Button, Checkbox, Group, Skeleton, Stack, Text, TextInput, Title } from '@mantine/core';
import { IconPlus, IconSearch, IconUpload } from '@tabler/icons-react';
import { useState } from 'react';
import { Link } from 'react-router';
import workspace from '../../components/Workspace/Workspace.module.css';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import { useManagedSystems, useSetupCatalogs } from '../../hooks/api/useManagedSystems';
import { AddSystemFlow, matchesSystem } from './AddSystemFlow';
import { SystemHelp } from './SystemHelp';
import classes from './Systems.module.css';
import { systemAttention } from './systemAttention';
import { systemStateLabels } from './systemState';

export function Systems() {
  const systems = useManagedSystems();
  const catalogs = useSetupCatalogs();
  const [search, setSearch] = useState('');
  const [attentionOnly, setAttentionOnly] = useState(false);
  const [adding, setAdding] = useState(false);
  const [uploading, setUploading] = useState(false);
  const enabled = systems.data?.filter((s) => s.enabled) ?? [];
  const verified = catalogs.isSuccess && !catalogs.data.message;
  const rows = enabled.filter((s) => {
    const attention = systemAttention(s.key, catalogs.data?.subscriptions ?? []);
    return matchesSystem(s, search) && (!attentionOnly || !verified || s.state === 'NeedsCatalog' || s.state === 'NeedsAttention' || attention.updates > 0 || attention.failed > 0);
  });
  return <div className={workspace.page}><Stack gap="md">
    <Group justify="space-between" className={workspace.hero}>
      <Group gap="xs"><Title order={1} className={workspace.heading}>Systems</Title><SystemHelp /></Group>
      <Group gap="xs">
        <Button variant="default" leftSection={<IconUpload size={16} />} onClick={() => { setUploading(true); setAdding(true); }}>Upload a DAT</Button>
        <Button {...workspaceActionProps} leftSection={<IconPlus size={16} />} onClick={() => { setUploading(false); setAdding(true); }}>Add system</Button>
      </Group>
    </Group>
    {systems.isPending ? <Skeleton h={180} /> : systems.isError ? <Alert color="red" title="Could not load systems"><Button variant="subtle" onClick={() => void systems.refetch()}>Retry</Button></Alert> : !enabled.length ?
      <Stack align="flex-start" py="xl"><Title order={2} className={workspace.sectionHeading}>No systems added</Title><Button {...workspaceActionProps} onClick={() => { setUploading(false); setAdding(true); }}>Choose your first system</Button></Stack> : <>
        <Group justify="space-between">
          <Group gap="md"><Text size="sm" c="dimmed">{rows.length} {rows.length === 1 ? 'system' : 'systems'}</Text><Checkbox label="Needs attention" checked={attentionOnly} disabled={!verified} onChange={(event) => setAttentionOnly(event.currentTarget.checked)} /></Group>
          <TextInput className={classes.search} aria-label="Filter your systems" placeholder="Find a system..." leftSection={<IconSearch size={16} />} value={search} onChange={(event) => setSearch(event.currentTarget.value)} />
        </Group>
        {(catalogs.isError || catalogs.data?.message) && <Alert color="yellow">Subscription status is unavailable. Showing all matching systems.<Button variant="subtle" onClick={() => void catalogs.refetch()}>Retry status</Button></Alert>}
        {!rows.length ? <Stack align="flex-start" py="xl"><Text>{attentionOnly ? 'No matching systems need attention.' : 'No matching systems.'}</Text><Button variant="subtle" onClick={() => { setSearch(''); setAttentionOnly(false); }}>Clear filters</Button></Stack> :
          <table className={classes.table} aria-label="Systems"><thead><tr><th className={classes.name}>System</th><th className={classes.count}>Sources</th><th className={classes.count}>Tracked titles</th><th className={classes.count}>Titles with files</th><th className={classes.status}>Catalog</th><th className={classes.updates}>Source updates</th></tr></thead><tbody>
            {rows.map((s) => {
              const attention = systemAttention(s.key, catalogs.data?.subscriptions ?? []);
              return <tr key={s.key}>
                <td><Link to={`/systems/${s.key}`} className={classes.titleLink}>{s.name}</Link><Text size="xs" c="dimmed">{[s.manufacturer, s.shortName].filter(Boolean).join(' · ')}</Text>
                  <Text size="xs" c="dimmed" className={classes.mobileCounts}>{s.catalogCount} {s.catalogCount === 1 ? 'source' : 'sources'} · {s.trackedTitles ?? 0} tracked · {s.ownedTitles} with files</Text>
                  <Text size="xs" c="dimmed" className={classes.mobileState}>Status: {systemStateLabels[s.state] ?? s.state}</Text>
                </td>
                <td className={classes.count}><Link to={`/systems/${s.key}?tab=sources`} className={classes.titleLink}>{s.catalogCount.toLocaleString()}</Link></td>
                <td className={classes.count}>{(s.trackedTitles ?? 0).toLocaleString()}</td>
                <td className={classes.count}>{s.ownedTitles.toLocaleString()}</td>
                <td className={classes.status}><Text size="xs" c={s.state === 'NeedsAttention' ? 'orange' : 'dimmed'}>{systemStateLabels[s.state] ?? s.state}</Text></td>
                <td><Stack gap={4}>
                  {s.state !== 'Ready' && s.state !== 'Processing' && <Text component={Link} to={`/systems/${s.key}?tab=sources`} size="xs" c="orange">{s.state === 'NeedsCatalog' ? 'Add a source' : 'Review catalog'}</Text>}
                  {!verified ? <Text size="xs" c="dimmed">{catalogs.isPending ? 'Checking status...' : 'Status unavailable'}</Text> : <>
                    {attention.updates > 0 && <Text component={Link} to={`/systems/${s.key}?tab=sources`} size="xs" c="teal">{attention.updates} {attention.updates === 1 ? 'update' : 'updates'} to review</Text>}
                    {attention.failed > 0 && <Text component={Link} to={`/systems/${s.key}?tab=sources`} size="xs" c="orange">{attention.failed} {attention.failed === 1 ? 'check' : 'checks'} failed</Text>}
                    {!attention.updates && !attention.failed && <Text size="xs" c="dimmed">No pending reviews</Text>}
                  </>}
                </Stack></td>
              </tr>;
            })}
          </tbody></table>}
      </>}
    {adding && <AddSystemFlow startWithUpload={uploading} onClose={() => setAdding(false)} />}
  </Stack></div>;
}
