import { Alert, Badge, Button, Group, Skeleton, Stack, Text, TextInput, Title } from '@mantine/core';
import { IconPlus, IconSearch } from '@tabler/icons-react';
import { useState } from 'react';
import { Link, Navigate, useSearchParams } from 'react-router';
import workspace from '../../components/Workspace/Workspace.module.css';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import { useDatsByPlatform } from '../../hooks/api/useDats';
import { useManagedSystems, useSetupCatalogs } from '../../hooks/api/useManagedSystems';
import { AddSourceFlow } from './AddSourceFlow';
import { SourceActions, sourcePath } from './SourceActions';
import { SourceHelp } from './SourceHelp';
import { SourceStatusBadge } from './SourceStatusBadge';
import { SourceSubscriptionActions } from './SourceSubscriptionActions';
import classes from './Sources.module.css';

export function SourcesTab({ systemKey }: { systemKey: string }) {
  const dats = useDatsByPlatform(systemKey);
  const systems = useManagedSystems();
  const catalogs = useSetupCatalogs();
  const system = systems.data?.find((s) => s.key === systemKey);
  const [params, setParams] = useSearchParams();
  const [adding, setAdding] = useState(false);
  const [search, setSearch] = useState('');
  const subscriptions = catalogs.data?.subscriptions.filter((s) => s.systemKey === systemKey) ?? [];
  const selected = dats.data?.find((d) => d.id === params.get('dat'));
  const pending = subscriptions.filter((s) => !s.activeDatId || !dats.data?.some((d) => d.id === s.activeDatId));
  const available = !catalogs.isPending && !catalogs.isError && !catalogs.data?.message;
  if (selected) {
    const next = new URLSearchParams();
    if (params.has('sourceTitle')) next.set('sourceTitle', params.get('sourceTitle')!);
    return <Navigate replace to={`${sourcePath(systemKey, selected)}${next.size ? `?${next}` : ''}`} />;
  }
  const installed = dats.data?.filter((d) => d.name.toLowerCase().includes(search.toLowerCase())) ?? [];
  return <Stack gap="md">
    <Group justify="space-between"><Group gap="xs"><Title order={2} className={workspace.sectionHeading}>Sources</Title><SourceHelp /></Group><Button {...workspaceActionProps} leftSection={<IconPlus size={16} />} onClick={() => setAdding(true)} disabled={!system}>Add source</Button></Group>
    {(catalogs.isError || catalogs.data?.message) && <Alert color="yellow">Subscription availability could not be verified. Installed sources are unchanged.<Button variant="subtle" onClick={() => void catalogs.refetch()}>Retry availability</Button></Alert>}
    {dats.isPending ? <Skeleton height={180} /> : dats.isError ? <Alert color="red" title="Sources could not be loaded"><Button onClick={() => void dats.refetch()}>Retry sources</Button></Alert> : <>
      {params.has('dat') && <Alert color="yellow" title="That DAT version is no longer installed">Choose a source below to inspect its current version.<Button variant="subtle" onClick={() => { const next = new URLSearchParams(params); next.delete('dat'); next.delete('sourceTitle'); setParams(next, { replace: true }); }}>Dismiss</Button></Alert>}
      {!dats.data?.length && !pending.length && <Stack align="flex-start" py="xl"><Title order={3} className={workspace.sectionHeading}>No sources yet</Title><Button {...workspaceActionProps} onClick={() => setAdding(true)} disabled={!system}>Choose a source</Button></Stack>}
      {(dats.data?.length ?? 0) > 5 && <TextInput aria-label="Filter sources" placeholder="Find a source..." leftSection={<IconSearch size={16} />} value={search} onChange={(e) => setSearch(e.currentTarget.value)} />}
      <div className={classes.list}>
        {installed.map((dat) => {
          const subscription = subscriptions.find((s) => s.activeDatId === dat.id);
          const path = sourcePath(systemKey, dat);
          return <article className={classes.row} aria-label={dat.name} key={dat.sourceId ?? dat.id}>
            <Stack gap={5}><Link className={classes.name} to={path}>{dat.name}</Link><Group gap="xs"><Text size="xs" c="dimmed">{dat.type}</Text><SourceStatusBadge status={dat.sourceStatus} /></Group></Stack>
            <Stack gap={4}><Text size="sm">{dat.version ?? 'Version not specified'}</Text><Text size="xs" c="dimmed">{Number(dat.gameCount ?? 0).toLocaleString()} entries</Text></Stack>
            <Group justify="space-between" align="flex-start" wrap="nowrap"><Stack gap={6} style={{ minWidth: 0 }}><Text size="xs" c="dimmed">{subscription ? 'Subscription' : available ? 'Manual updates' : 'Updates unverified'}</Text>{subscription && <SourceSubscriptionActions subscription={subscription} compact onAccepted={() => {}} />}</Stack><SourceActions dat={dat} path={path} manual={!subscription && available} /></Group>
          </article>;
        })}
        {pending.filter((s) => s.name.toLowerCase().includes(search.toLowerCase())).map((s) => <article className={classes.row} aria-label={s.name} key={s.id}><Stack gap={4}><Text fw={600} size="sm">{s.name}</Text><Badge color="gray" w="fit-content">Subscription</Badge></Stack><Text size="sm" c="dimmed">No installed DAT</Text><SourceSubscriptionActions subscription={s} manage onAccepted={() => {}} /></article>)}
      </div>
      {search && !installed.length && !pending.some((s) => s.name.toLowerCase().includes(search.toLowerCase())) && <Text c="dimmed" size="sm">No sources match this search.</Text>}
    </>}
    {adding && system && <AddSourceFlow system={system} onClose={() => setAdding(false)} />}
  </Stack>;
}
