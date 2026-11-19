import { Alert, Badge, Button, Group, Skeleton, Stack, Tabs, Text, Title } from '@mantine/core';
import type { JobReference } from '@romd/admin-api-client';
import { IconArrowLeft } from '@tabler/icons-react';
import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useRef, useState } from 'react';
import { Link, useLocation, useParams, useSearchParams } from 'react-router';
import workspace from '../../components/Workspace/Workspace.module.css';
import { useDatsByPlatform } from '../../hooks/api/useDats';
import { useJob } from '../../hooks/api/useJobs';
import {
  managedSystemKeys,
  useDatCatalogs,
  useDatSubscriptions,
} from '../../hooks/api/useManagedSystems';
import { usePlatform } from '../../hooks/api/usePlatforms';
import { usePermissions } from '../../hooks/usePermissions';
import type { DatReviewOutcome } from './DatReplacementReviewModal';
import { DatSubscriptionCard } from './DatSubscriptionCard';
import { withReviewView } from './datReviewSummary';
import { SourceActions, sourcePath } from './SourceActions';
import { SourceEntryExplorer } from './SourceEntryExplorer';
import { SourceHelp } from './SourceHelp';
import { SourceStatusBadge } from './SourceStatusBadge';
import { subscriptionReady } from './SourceSubscriptionActions';
import { SourceUpdates } from './SourceUpdates';
import { connectableCatalog, datProviderLabel } from './sourceCatalog';

export function SourceWorkspace() {
  const { systemKey = '', sourceId = '' } = useParams();
  const dats = useDatsByPlatform(systemKey);
  const system = usePlatform(systemKey);
  const { role, canManageSources } = usePermissions();
  const isAdmin = role === 'Admin';
  const subscriptions = useDatSubscriptions({
    enabled: isAdmin,
  });
  const client = useQueryClient();
  const { state } = useLocation();
  const [params, setParams] = useSearchParams();
  const [file, setFile] = useState<File>();
  const [jobId, setJobId] = useState<string>();
  const [outcome, setOutcome] = useState<DatReviewOutcome>();
  const job = useJob(jobId);
  const dat = dats.data?.find((d) => (d.sourceId ?? d.id) === sourceId);
  const subscription =
    isAdmin && dat ? subscriptions.data?.find((item) => item.activeDatId === dat.id) : undefined;
  const subscribed = !!(dat?.hasSubscription || subscription);
  const canUpload = canManageSources && !subscribed;
  // Settings and `review=` predate the two-tab page; their URLs open the Updates tab.
  const legacyTab = params.get('tab') === 'settings';
  const active = legacyTab
    ? 'updates'
    : ([
        'entries',
        'updates',
      ].find((value) => value === params.get('tab')) ?? 'entries');
  const catalogs = useDatCatalogs({
    enabled:
      isAdmin && active === 'updates' && !!dat && !subscribed && dat.sourceStatus === 'Active',
  });
  const catalog =
    isAdmin && !subscribed && dat && !catalogs.isError
      ? connectableCatalog(
          catalogs.data,
          subscriptions.isSuccess ? subscriptions.data : undefined,
          systemKey,
          dat,
        )
      : undefined;
  const previousSourceId = useRef(sourceId);
  useEffect(() => {
    if (previousSourceId.current === sourceId) return;
    previousSourceId.current = sourceId;
    setFile(undefined);
    setJobId(undefined);
    setOutcome(undefined);
  }, [
    sourceId,
  ]);
  useEffect(() => {
    if (!legacyTab && !params.has('review')) return;
    const next = new URLSearchParams(params);
    next.delete('review');
    if (legacyTab) next.set('tab', 'updates');
    setParams(next, {
      replace: true,
      state,
    });
  }, [
    legacyTab,
    params,
    setParams,
    state,
  ]);
  useEffect(() => {
    if (!job.data?.isTerminal) return;
    void client.invalidateQueries({
      queryKey: [
        'dats',
      ],
    });
    void client.invalidateQueries({
      queryKey: managedSystemKeys.all,
    });
    void client.invalidateQueries({
      queryKey: managedSystemKeys.subscriptions,
    });
  }, [
    job.data?.isTerminal,
    client,
  ]);
  const accepted = (result: JobReference, approved?: DatReviewOutcome) => {
    setJobId(result.jobId);
    setOutcome(approved);
    void client.invalidateQueries({
      queryKey: managedSystemKeys.all,
    });
    void client.invalidateQueries({
      queryKey: managedSystemKeys.subscriptions,
    });
  };
  const updateReady = !!subscription && subscriptionReady(subscription);
  const imported = dat?.importedAt ? Date.parse(dat.importedAt) : Number.NaN;
  const cancelled = job.data?.phase === 'Cancelled';
  const applied = job.data?.isTerminal && job.data.phase === 'Completed' && !job.data.hasErrors;
  const needsAttention = job.data?.isTerminal && !applied && !cancelled;
  const jobTitle = cancelled
    ? 'Update cancelled'
    : needsAttention
      ? 'Update needs attention'
      : applied
        ? outcome?.version
          ? `Applied ${outcome.version}`
          : 'Update applied'
        : outcome?.version
          ? `Applying ${outcome.version}`
          : 'Applying update';
  return (
    <div className={workspace.page}>
      <Stack gap="md">
        <Button
          component={Link}
          to={`/systems/${systemKey}?tab=sources`}
          state={state}
          variant="subtle"
          color="gray"
          p={0}
          w="fit-content"
          leftSection={<IconArrowLeft size={15} />}
        >
          {system.data?.name ?? 'System'} sources
        </Button>
        {dats.isPending ? (
          <Skeleton height={240} />
        ) : dats.isError ? (
          <Alert
            color="red"
            title="Source could not be loaded"
          >
            <Button onClick={() => void dats.refetch()}>Retry source</Button>
          </Alert>
        ) : !dat ? (
          <Alert
            color="yellow"
            title="Source not found"
          >
            This source may have been removed. Return to the system's sources to choose another.
          </Alert>
        ) : (
          <>
            <Group
              justify="space-between"
              align="flex-start"
              className={workspace.hero}
            >
              <Stack
                gap={6}
                style={{
                  minWidth: 0,
                  flex: 1,
                }}
              >
                <Group gap="xs">
                  <Title
                    order={1}
                    className={workspace.heading}
                    style={{
                      overflowWrap: 'anywhere',
                    }}
                  >
                    {dat.name}
                  </Title>
                  <SourceHelp />
                </Group>
                <Group gap="xs">
                  <Text
                    size="sm"
                    c="dimmed"
                  >
                    {datProviderLabel(dat.type)}
                  </Text>
                  <SourceStatusBadge status={dat.sourceStatus} />
                  <Text
                    size="sm"
                    c="dimmed"
                  >
                    {Number(dat.gameCount ?? 0).toLocaleString()} DAT entries
                  </Text>
                </Group>
                <Text size="sm">
                  Installed DAT: {dat.version ?? 'Version not specified'}
                  {Number.isFinite(imported) && (
                    <Text
                      span
                      c="dimmed"
                      inherit
                    >
                      {' '}
                      · imported{' '}
                      {new Date(imported).toLocaleDateString(undefined, {
                        day: 'numeric',
                        month: 'short',
                        year: 'numeric',
                      })}
                    </Text>
                  )}
                </Text>
              </Stack>
              <SourceActions
                dat={dat}
                path={sourcePath(systemKey, dat)}
                showUpdates={false}
                subscription={subscription}
                lifecycle
              />
            </Group>
            {jobId && (
              <Alert
                color={
                  cancelled ? 'gray' : needsAttention || job.data?.hasErrors ? 'orange' : 'blue'
                }
                title={jobTitle}
              >
                {outcome?.summary && !cancelled && !needsAttention && (
                  <Text
                    size="sm"
                    mb={4}
                  >
                    {outcome.summary}
                  </Text>
                )}
                <Button
                  component={Link}
                  to={`/jobs/${jobId}`}
                  variant="subtle"
                >
                  View processing details
                </Button>
                {job.data?.isTerminal && (
                  <Button
                    variant="subtle"
                    onClick={() => {
                      setJobId(undefined);
                      setOutcome(undefined);
                    }}
                  >
                    Dismiss
                  </Button>
                )}
              </Alert>
            )}
            <Tabs
              value={active}
              onChange={(value) => {
                if (!value) return;
                // A review position belongs to the Updates tab; other tabs keep a clean URL.
                const next = withReviewView(params, {});
                next.set('tab', value);
                setParams(next, {
                  state,
                });
              }}
              keepMounted={false}
              color="teal"
              classNames={{
                list: workspace.tabsList,
                tab: workspace.tab,
              }}
            >
              <Tabs.List mb="md">
                <Tabs.Tab value="entries">Entries</Tabs.Tab>
                <Tabs.Tab
                  value="updates"
                  aria-label={
                    updateReady
                      ? `Updates, ${subscription?.activeDatId ? 'update' : 'first DAT'} ready`
                      : undefined
                  }
                  rightSection={
                    updateReady ? (
                      <Badge
                        size="xs"
                        color="orange"
                        variant="light"
                      >
                        {subscription?.activeDatId ? 'Ready' : 'First DAT'}
                      </Badge>
                    ) : undefined
                  }
                >
                  Updates
                </Tabs.Tab>
              </Tabs.List>
              <Tabs.Panel value="entries">
                <SourceEntryExplorer
                  key={dat.id}
                  datId={dat.id}
                />
              </Tabs.Panel>
              <Tabs.Panel value="updates">
                <SourceUpdates
                  dat={dat}
                  systemName={system.data?.name}
                  provider={datProviderLabel(dat.type) ?? 'the publisher'}
                  isAdmin={isAdmin}
                  subscribed={subscribed}
                  subscription={subscription}
                  subscriptionStatus={{
                    pending: subscriptions.isPending,
                    retry: () => void subscriptions.refetch(),
                  }}
                  canUpload={canUpload}
                  file={file}
                  onFile={setFile}
                  uploading={!!jobId && !job.data?.isTerminal}
                  jobNotice={!!jobId}
                  offer={
                    isAdmin && dat.sourceStatus === 'Active' && !subscribed ? (
                      catalogs.isError || (catalogs.data?.enabled && catalogs.data.message) ? (
                        <Alert color="yellow">
                          The subscription directory is unavailable.
                          <Button
                            variant="subtle"
                            onClick={() => void catalogs.refetch()}
                          >
                            Retry directory
                          </Button>
                        </Alert>
                      ) : catalog ? (
                        <DatSubscriptionCard
                          dat={dat}
                          catalogId={catalog.catalogId}
                        />
                      ) : null
                    ) : null
                  }
                  onAccepted={accepted}
                />
              </Tabs.Panel>
            </Tabs>
          </>
        )}
      </Stack>
    </div>
  );
}
