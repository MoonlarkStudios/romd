import { Alert, Button, Group, Skeleton, Stack, Tabs, Text, Title } from '@mantine/core';
import type { UploadAccepted } from '@romd/admin-api-client';
import { IconArrowLeft } from '@tabler/icons-react';
import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { Link, useParams, useSearchParams } from 'react-router';
import workspace from '../../components/Workspace/Workspace.module.css';
import { useDatsByPlatform } from '../../hooks/api/useDats';
import { useJob } from '../../hooks/api/useJobs';
import { managedSystemKeys, useManagedSystems, useSetupCatalogs } from '../../hooks/api/useManagedSystems';
import { DatUploadZone } from './components/DatUploadZone';
import { DatReplacementReviewModal } from './DatReplacementReviewModal';
import { DatSubscriptionCard } from './DatSubscriptionCard';
import { SourceActions, sourcePath } from './SourceActions';
import { SourceEntryExplorer } from './SourceEntryExplorer';
import { SourceHelp } from './SourceHelp';
import { SourceStatusBadge } from './SourceStatusBadge';
import { SourceSubscriptionActions } from './SourceSubscriptionActions';
import classes from './Sources.module.css';

export function SourceWorkspace() {
  const { systemKey = '', sourceId = '' } = useParams();
  const dats = useDatsByPlatform(systemKey);
  const systems = useManagedSystems();
  const catalogs = useSetupCatalogs();
  const client = useQueryClient();
  const [params, setParams] = useSearchParams();
  const [file, setFile] = useState<File>();
  const [jobId, setJobId] = useState<string>();
  const job = useJob(jobId);
  const dat = dats.data?.find((d) => (d.sourceId ?? d.id) === sourceId);
  const system = systems.data?.find((s) => s.key === systemKey);
  const subscription = catalogs.data?.subscriptions.find((s) => s.systemKey === systemKey && s.activeDatId === dat?.id);
  const available = !catalogs.isPending && !catalogs.isError && !catalogs.data?.message;
  const active = ['entries', 'updates', 'settings'].find((value) => value === params.get('tab')) ?? 'entries';
  useEffect(() => { setFile(undefined); setJobId(undefined); }, [sourceId]);
  useEffect(() => {
    if (!job.data?.isTerminal) return;
    void client.invalidateQueries({ queryKey: ['dats'] });
    void client.invalidateQueries({ queryKey: managedSystemKeys.all });
    void client.invalidateQueries({ queryKey: managedSystemKeys.catalogs });
  }, [job.data?.isTerminal, client]);
  const accepted = (result: UploadAccepted) => {
    setFile(undefined); setJobId(result.jobId);
    void client.invalidateQueries({ queryKey: managedSystemKeys.all });
  };
  return <div className={workspace.page}><Stack gap="md">
    <Button component={Link} to={`/systems/${systemKey}?tab=sources`} variant="subtle" color="gray" p={0} w="fit-content" leftSection={<IconArrowLeft size={15} />}>{system?.name ?? 'System'} sources</Button>
    {dats.isPending ? <Skeleton height={240} /> : dats.isError ? <Alert color="red" title="Source could not be loaded"><Button onClick={() => void dats.refetch()}>Retry source</Button></Alert> : !dat ? <Alert color="yellow" title="Source not found">This source may have been removed. Return to the system's sources to choose another.</Alert> : <>
      <Group justify="space-between" align="flex-start" className={workspace.hero}><Stack gap={6} style={{ minWidth: 0, flex: 1 }}><Group gap="xs"><Title order={1} className={workspace.heading} style={{ overflowWrap: 'anywhere' }}>{dat.name}</Title><SourceHelp /></Group><Group gap="xs"><Text size="sm" c="dimmed">{dat.type}</Text><SourceStatusBadge status={dat.sourceStatus} /><Text size="sm" c="dimmed">{Number(dat.gameCount ?? 0).toLocaleString()} entries</Text></Group><Text size="sm">Installed DAT: {dat.version ?? 'Version not specified'}</Text></Stack><SourceActions dat={dat} path={sourcePath(systemKey, dat)} lifecycle={active === 'settings'} /></Group>
      {jobId && <Alert color={job.data?.hasErrors ? 'orange' : 'blue'} title={job.data?.isTerminal ? 'Source processing finished' : 'Processing source'}><Button component={Link} to={`/jobs/${jobId}`} variant="subtle">View processing details</Button>{job.data?.isTerminal && <Button variant="subtle" onClick={() => setJobId(undefined)}>Dismiss</Button>}</Alert>}
      <Tabs value={active} onChange={(value) => { if (!value) return; const next = new URLSearchParams(params); next.set('tab', value); setParams(next); }} keepMounted={false} color="teal" classNames={{ list: workspace.tabsList, tab: workspace.tab }}>
        <Tabs.List mb="md"><Tabs.Tab value="entries">Entries</Tabs.Tab><Tabs.Tab value="updates">Updates</Tabs.Tab><Tabs.Tab value="settings">Settings</Tabs.Tab></Tabs.List>
        <Tabs.Panel value="entries"><SourceEntryExplorer key={dat.id} datId={dat.id} /></Tabs.Panel>
        <Tabs.Panel value="updates"><Stack gap="md">
          <Title order={2} className={workspace.sectionHeading}>{!available ? 'Updates' : subscription ? 'Subscription updates' : 'Manual updates'}</Title>
          {!available ? <Alert color="yellow">Update availability has not been verified.<Button variant="subtle" onClick={() => void catalogs.refetch()}>Retry availability</Button></Alert> : subscription ? <SourceSubscriptionActions subscription={subscription} onAccepted={accepted} /> : <><Text size="sm" c="dimmed">The installed DAT stays active until a new version is reviewed and approved.</Text><DatUploadZone loading={!!jobId && !job.data?.isTerminal} onDrop={(files) => setFile(files[0])} /></>}
        </Stack></Tabs.Panel>
        <Tabs.Panel value="settings"><Stack gap="md">
          <section className={classes.section}><Stack gap="sm"><Title order={2} className={workspace.sectionHeading}>Update method</Title>{!available ? <Alert color="yellow">Subscription settings could not be verified.<Button variant="subtle" onClick={() => void catalogs.refetch()}>Retry availability</Button></Alert> : subscription ? <SourceSubscriptionActions subscription={subscription} manage settingsOnly onAccepted={accepted} /> : <><Text size="sm">Manual updates</Text>{catalogs.data?.catalogs.some((c) => c.name === dat.name && c.systemId === system?.key) && <DatSubscriptionCard dat={dat} onAccepted={accepted} />}</>}</Stack></section>
          <section className={classes.section}><Stack gap="sm"><Title order={2} className={workspace.sectionHeading}>Installed DAT</Title><Text size="sm">Version: {dat.version ?? 'Not specified'}</Text><Text size="sm" c="dimmed">Imported {dat.importedAt ? new Date(dat.importedAt).toLocaleString() : 'date unavailable'}</Text></Stack></section>
        </Stack></Tabs.Panel>
      </Tabs>
      {file && <DatReplacementReviewModal key={dat.id} dat={dat} file={file} onClose={() => setFile(undefined)} onAccepted={accepted} />}
    </>}
  </Stack></div>;
}
