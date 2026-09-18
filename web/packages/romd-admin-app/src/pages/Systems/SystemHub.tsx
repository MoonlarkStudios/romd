import { Alert, Button, Group, Popover, SegmentedControl, Skeleton, Stack, Tabs, Text, Title } from '@mantine/core';
import { IconArrowLeft, IconSparkles, IconUpload } from '@tabler/icons-react';
import { useState } from 'react';
import { Link, useNavigate, useParams, useSearchParams } from 'react-router';
import { EmptyState } from '../../components/EmptyState';
import workspace from '../../components/Workspace/Workspace.module.css';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import { EnrichmentScopeValue, useBulkEnrichment } from '../../hooks/api/useBulkEnrichment';
import { usePlatform, usePlatformAliases } from '../../hooks/api/usePlatforms';
import { usePermissions } from '../../hooks/usePermissions';
import { TitlesLens } from '../Catalog/TitlesLens';
import { BiosTab } from './BiosTab';
import { SourcesTab } from './SourcesTab';
import { SystemHelp } from './SystemHelp';
import { SystemSettings } from './SystemSettings';
import { SystemSetupSummary } from './SystemSetupSummary';

const HUB_TABS = ['titles', 'sources', 'bios', 'settings'] as const;

export function SystemHub() {
  const { systemKey } = useParams<{ systemKey: string }>();
  const navigate = useNavigate();
  const [params, setParams] = useSearchParams();
  const platformQuery = usePlatform(systemKey);
  const aliasesQuery = usePlatformAliases(systemKey);
  const { canTriggerEnrichment, canUploadRoms } = usePermissions();
  const active = HUB_TABS.find((tab) => tab === params.get('tab')) ?? (params.has('dat') ? 'sources' : 'titles');
  const view = params.get('view') === 'all' || (!params.has('view') && params.get('tracked') === 'untracked') ? 'all' : 'tracked';
  const setView = (value: string) => setParams((previous) => {
    const next = new URLSearchParams(previous);
    next.set('view', value); next.delete('tracked');
    return next;
  });
  if (platformQuery.isLoading) return <Skeleton height={240} />;
  const platform = platformQuery.data;
  if (platformQuery.isError || !platform || !systemKey) return <EmptyState title="System not found" description="It may have been deleted or merged into another system." action={{ label: 'Back to Systems', onClick: () => navigate('/systems') }} />;
  const allTitlesParams = new URLSearchParams(params);
  allTitlesParams.set('tab', 'titles'); allTitlesParams.set('view', 'all'); allTitlesParams.delete('tracked');
  return <div className={workspace.page}><Stack gap="md">
    <Button component={Link} to="/systems" variant="subtle" color="gray" p={0} w="fit-content" leftSection={<IconArrowLeft size={15} />}>All systems</Button>
    <Group justify="space-between" className={workspace.hero} data-testid="system-hub-header">
      <Stack gap={3} style={{ minWidth: 0, flex: 1 }}><Group gap="xs"><Title order={1} className={workspace.heading}>{platform.name}</Title><SystemHelp /></Group><Text size="sm" c="dimmed">{[platform.compactLabel].filter(Boolean).join(' · ')}</Text></Stack>
      {canUploadRoms && <Button component={Link} to="/import" variant="default" leftSection={<IconUpload size={16} />}>Import ROMs</Button>}
    </Group>
    <SystemSetupSummary systemKey={systemKey} />
    <Tabs value={active} onChange={(value) => {
      if (!value) return;
      const next = new URLSearchParams(params); next.set('tab', value); next.delete('dat'); next.delete('sourceTitle'); setParams(next);
    }} keepMounted={false} color="teal" classNames={{ list: workspace.tabsList, tab: workspace.tab }}>
      <Tabs.List mb="md"><Tabs.Tab value="titles">Titles</Tabs.Tab><Tabs.Tab value="sources">Sources</Tabs.Tab><Tabs.Tab value="bios">BIOS</Tabs.Tab><Tabs.Tab value="settings">Settings</Tabs.Tab></Tabs.List>
      <Tabs.Panel value="titles"><Stack gap="md">
        <Group justify="space-between"><SegmentedControl aria-label="Title scope" value={view} onChange={setView} data={[{ value: 'tracked', label: 'Tracked titles' }, { value: 'all', label: 'All titles' }]} />{canTriggerEnrichment && <SystemEnrichment systemKey={systemKey} />}</Group>
        <TitlesLens key={`${systemKey}-${view}`} showHeader={false} lockedFilters={{ systemKey, ...(view === 'tracked' ? { tracked: 'tracked' as const } : {}) }} lockedFilterLabels={{ systemKey: platform.compactLabel || platform.name }} browseAllHref={`/systems/${systemKey}?${allTitlesParams}`} />
      </Stack></Tabs.Panel>
      <Tabs.Panel value="sources"><SourcesTab systemKey={systemKey} /></Tabs.Panel>
      <Tabs.Panel value="bios"><BiosTab systemKey={systemKey} /></Tabs.Panel>
      <Tabs.Panel value="settings"><Stack gap="md">
        <div><Text fw={600} size="sm">Aliases</Text>{aliasesQuery.isError ? <Button variant="subtle" onClick={() => aliasesQuery.refetch()}>Retry aliases</Button> : <Text size="sm" c="dimmed">{aliasesQuery.isPending ? 'Loading aliases...' : (aliasesQuery.data ?? []).filter((alias) => alias.type === 'name').map((alias) => alias.value).join(', ') || 'No aliases'}</Text>}</div>
        <SystemSettings systemKey={systemKey} />
      </Stack></Tabs.Panel>
    </Tabs>
  </Stack></div>;
}

function SystemEnrichment({ systemKey }: { systemKey: string }) {
  const [opened, setOpened] = useState(false);
  const [scope, setScope] = useState<'tracked' | 'all'>('tracked');
  const mutation = useBulkEnrichment();
  return <Popover opened={opened} onChange={setOpened} width={300} position="bottom-end" withArrow>
    <Popover.Target><Button {...workspaceActionProps} variant="subtle" leftSection={<IconSparkles size={16} />} onClick={() => { mutation.reset(); setOpened((value) => !value); }}>Enrich titles</Button></Popover.Target>
    <Popover.Dropdown><Stack gap="sm">
      <SegmentedControl aria-label="Enrichment scope" value={scope} onChange={(value) => setScope(value as 'tracked' | 'all')} data={[{ label: 'Tracked titles', value: 'tracked' }, { label: 'All titles', value: 'all' }]} />
      <Text size="sm">{scope === 'tracked' ? 'Enrich unenriched titles you have chosen to track for this system.' : 'Enrich every unenriched title on this system, including untracked titles. This can be a large job.'}</Text>
      {mutation.isError && <Alert color="red">Could not start enrichment. Try again.</Alert>}
      <Group justify="flex-end"><Button variant="subtle" disabled={mutation.isPending} onClick={() => setOpened(false)}>Cancel</Button><Button {...workspaceActionProps} loading={mutation.isPending} onClick={() => mutation.mutate({ systemKey, scope: scope === 'all' ? EnrichmentScopeValue.All : EnrichmentScopeValue.Tracked }, { onSuccess: () => setOpened(false) })}>Start enrichment</Button></Group>
    </Stack></Popover.Dropdown>
  </Popover>;
}
