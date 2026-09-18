import { Badge, Button, Group, Stack, Tabs, Title } from '@mantine/core';
import { IconArrowLeft, IconUpload } from '@tabler/icons-react';
import { Link, Navigate, useLocation, useSearchParams } from 'react-router';
import workspace from '../../components/Workspace/Workspace.module.css';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import { useUnroutedDats } from '../../hooks/api/useDats';
import { useLibraryStats } from '../../hooks/api/useRoms';
import { usePermissions } from '../../hooks/usePermissions';
import { ImportPage } from '../Import';
import { Inbox } from '../Inbox';
import { RomInventory } from './RomInventory';

export function LegacyRomRedirect({ importing = false }: { importing?: boolean }) {
  const location = useLocation();
  const params = new URLSearchParams(location.search);
  if (!importing) params.set('tab', 'attention');
  return <Navigate replace to={{ pathname: importing ? '/roms/import' : '/roms', search: params.toString(), hash: location.hash }} />;
}

export function Roms({ importing = false }: { importing?: boolean }) {
  const [params, setParams] = useSearchParams();
  const { canUploadRoms } = usePermissions();
  const stats = useLibraryStats();
  const unrouted = useUnroutedDats();
  const count = stats.isSuccess && unrouted.isSuccess ? Number(stats.data?.unidentifiedCount ?? 0) + (unrouted.data?.length ?? 0) : undefined;
  const active = params.get('tab') === 'all' ? 'all' : 'attention';
  return <div className={workspace.page}><Stack gap="md">
    {importing && <Button component={Link} to={`/roms?${params}`} variant="subtle" color="gray" p={0} w="fit-content" leftSection={<IconArrowLeft size={15} />}>ROMs</Button>}
    <Group justify="space-between" className={workspace.hero}><Title order={1} className={workspace.heading}>{importing ? 'Import ROMs' : 'ROMs'}</Title>{!importing && canUploadRoms && <Button {...workspaceActionProps} component={Link} to={`/roms/import?${params}`} leftSection={<IconUpload size={16} />}>Import ROMs</Button>}</Group>
    {importing ? <ImportPage embedded /> : <Tabs value={active} keepMounted={false} color="teal" classNames={{ list: workspace.tabsList, tab: workspace.tab }} onChange={(value) => { if (value) { const next = new URLSearchParams(params); next.set('tab', value); setParams(next); } }}>
      <Tabs.List mb="md"><Tabs.Tab value="attention" rightSection={count ? <Badge size="xs" color="orange">{count.toLocaleString()}</Badge> : undefined}>Needs attention</Tabs.Tab><Tabs.Tab value="all">All files</Tabs.Tab></Tabs.List>
      <Tabs.Panel value="attention"><Inbox embedded /></Tabs.Panel><Tabs.Panel value="all"><RomInventory /></Tabs.Panel>
    </Tabs>}
  </Stack></div>;
}
