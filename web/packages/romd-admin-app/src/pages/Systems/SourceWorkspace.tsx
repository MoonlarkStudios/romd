import { Alert, Button, Group, Skeleton, Stack, Tabs, Text, Title } from '@mantine/core';
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
import { DatUploadZone } from './components/DatUploadZone';
import { DatReplacementReview, type DatReviewOutcome } from './DatReplacementReviewModal';
import { DatSubscriptionCard } from './DatSubscriptionCard';
import { SourceActions, sourcePath } from './SourceActions';
import { SourceEntryExplorer } from './SourceEntryExplorer';
import { SourceHelp } from './SourceHelp';
import { SourceStatusBadge } from './SourceStatusBadge';
import { SourceSubscriptionActions } from './SourceSubscriptionActions';
import classes from './Sources.module.css';
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
  // The review lives in the Updates tab URL, so Back, reload, and shared links return to it.
  // An upload's bytes stay in this page's memory, so its review cannot survive a reload.
  const reviewParam = params.get('review');
  const reviewing =
    reviewParam === 'subscription'
      ? !!subscription
      : reviewParam === 'upload'
        ? !!file && canUpload
        : false;
  const active =
    [
      'entries',
      'updates',
      'settings',
    ].find((value) => value === params.get('tab')) ?? 'entries';
  const catalogs = useDatCatalogs({
    enabled:
      isAdmin && active === 'settings' && !!dat && !subscribed && dat.sourceStatus === 'Active',
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
    if (reviewParam !== 'upload' || file) return;
    const next = new URLSearchParams(params);
    next.delete('review');
    setParams(next, {
      replace: true,
      state,
    });
  }, [
    reviewParam,
    file,
    params,
    setParams,
    state,
  ]);
  const openReview = (kind: 'subscription' | 'upload') => {
    const next = new URLSearchParams(params);
    next.set('tab', 'updates');
    next.set('review', kind);
    setParams(next, {
      state,
    });
  };
  const closeReview = () => {
    setFile(undefined);
    const next = new URLSearchParams(params);
    next.delete('review');
    setParams(next, {
      state,
    });
  };
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
    if (subscription && reviewParam === 'subscription')
      client.setQueryData(
        [
          'source-subscription',
          subscription.id,
        ],
        {
          ...subscription,
          state: 'Applying',
          jobId: result.jobId,
        },
      );
    if (reviewParam) closeReview();
    else setFile(undefined);
    void client.invalidateQueries({
      queryKey: managedSystemKeys.all,
    });
    void client.invalidateQueries({
      queryKey: managedSystemKeys.subscriptions,
    });
  };
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
                <Text size="sm">Installed DAT: {dat.version ?? 'Version not specified'}</Text>
              </Stack>
              <SourceActions
                dat={dat}
                path={sourcePath(systemKey, dat)}
                lifecycle={active === 'settings'}
              />
            </Group>
            {jobId && (
              <Alert
                color={job.data?.hasErrors ? 'orange' : 'blue'}
                title={
                  job.data?.isTerminal
                    ? job.data.hasErrors
                      ? 'Update needs attention'
                      : outcome?.version
                        ? `Applied ${outcome.version}`
                        : 'Update applied'
                    : outcome?.version
                      ? `Applying ${outcome.version}`
                      : 'Applying update'
                }
              >
                {outcome?.summary && (
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
                const next = new URLSearchParams(params);
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
                <Tabs.Tab value="updates">Updates</Tabs.Tab>
                <Tabs.Tab value="settings">Settings</Tabs.Tab>
              </Tabs.List>
              <Tabs.Panel value="entries">
                <SourceEntryExplorer
                  key={dat.id}
                  datId={dat.id}
                />
              </Tabs.Panel>
              <Tabs.Panel value="updates">
                {reviewing ? (
                  <Stack gap="sm">
                    <Button
                      variant="subtle"
                      color="gray"
                      p={0}
                      w="fit-content"
                      leftSection={<IconArrowLeft size={15} />}
                      onClick={closeReview}
                    >
                      Back to updates
                    </Button>
                    {subscription && reviewParam === 'subscription' ? (
                      <DatReplacementReview
                        key={subscription.id}
                        layout="page"
                        subscription={subscription}
                        systemName={system.data?.name}
                        provider={datProviderLabel(dat.type)}
                        onClose={closeReview}
                        onAccepted={accepted}
                      />
                    ) : (
                      file && (
                        <DatReplacementReview
                          key={`${dat.id}:${file.name}:${file.lastModified}`}
                          layout="page"
                          dat={dat}
                          file={file}
                          systemName={system.data?.name}
                          onClose={closeReview}
                          onAccepted={accepted}
                        />
                      )
                    )}
                  </Stack>
                ) : (
                  <Stack gap="md">
                    <Title
                      order={2}
                      className={workspace.sectionHeading}
                    >
                      {subscribed ? 'Subscription updates' : 'Manual updates'}
                    </Title>
                    {subscribed && !isAdmin ? (
                      <Text
                        size="sm"
                        c="dimmed"
                      >
                        An administrator manages this subscription.
                      </Text>
                    ) : subscribed && !subscription ? (
                      subscriptions.isPending ? (
                        <Skeleton height={64} />
                      ) : (
                        <Alert color="yellow">
                          Subscription status could not be loaded.
                          <Button
                            variant="subtle"
                            onClick={() => void subscriptions.refetch()}
                          >
                            Retry status
                          </Button>
                        </Alert>
                      )
                    ) : subscription ? (
                      <SourceSubscriptionActions
                        subscription={subscription}
                        systemName={system.data?.name}
                        provider={datProviderLabel(dat.type)}
                        onReview={() => openReview('subscription')}
                        onAccepted={accepted}
                      />
                    ) : (
                      <>
                        <Text
                          size="sm"
                          c="dimmed"
                        >
                          The installed DAT stays active until a new version is reviewed and
                          approved.
                        </Text>
                        {canUpload && (
                          <DatUploadZone
                            loading={!!jobId && !job.data?.isTerminal}
                            onDrop={(files) => {
                              setFile(files[0]);
                              openReview('upload');
                            }}
                          />
                        )}
                      </>
                    )}
                  </Stack>
                )}
              </Tabs.Panel>
              <Tabs.Panel value="settings">
                <Stack gap="md">
                  <section className={classes.section}>
                    <Stack gap="sm">
                      <Title
                        order={2}
                        className={workspace.sectionHeading}
                      >
                        Update method
                      </Title>
                      {subscribed && !subscription ? (
                        <Text size="sm">
                          {isAdmin
                            ? 'Subscribed'
                            : 'This subscription is managed by an administrator.'}
                        </Text>
                      ) : subscription ? (
                        <SourceSubscriptionActions
                          subscription={subscription}
                          manage
                          settingsOnly
                          onAccepted={accepted}
                        />
                      ) : (
                        <>
                          <Text size="sm">Manual updates</Text>
                          {isAdmin &&
                            dat.sourceStatus === 'Active' &&
                            (catalogs.isError ||
                              (catalogs.data?.enabled && catalogs.data.message)) && (
                              <Alert color="yellow">
                                Subscription directory unavailable.
                                <Button
                                  variant="subtle"
                                  onClick={() => void catalogs.refetch()}
                                >
                                  Retry directory
                                </Button>
                              </Alert>
                            )}
                          {catalog && (
                            <DatSubscriptionCard
                              dat={dat}
                              catalogId={catalog.catalogId}
                            />
                          )}
                        </>
                      )}
                    </Stack>
                  </section>
                  <section className={classes.section}>
                    <Stack gap="sm">
                      <Title
                        order={2}
                        className={workspace.sectionHeading}
                      >
                        Installed DAT
                      </Title>
                      <Text size="sm">Version: {dat.version ?? 'Not specified'}</Text>
                      <Text
                        size="sm"
                        c="dimmed"
                      >
                        Imported{' '}
                        {dat.importedAt
                          ? new Date(dat.importedAt).toLocaleString()
                          : 'date unavailable'}
                      </Text>
                    </Stack>
                  </section>
                </Stack>
              </Tabs.Panel>
            </Tabs>
          </>
        )}
      </Stack>
    </div>
  );
}
