import { Alert, Button, Group, Skeleton, Stack, Text, TextInput, Title } from '@mantine/core';
import type { DatCatalogSubscription } from '@romd/admin-api-client';
import { IconPlus, IconSearch } from '@tabler/icons-react';
import { useState } from 'react';
import { Link, Navigate, useLocation, useSearchParams } from 'react-router';
import workspace from '../../components/Workspace/Workspace.module.css';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import { useDatsByPlatform } from '../../hooks/api/useDats';
import {
  useDatCatalogs,
  useDatSubscriptions,
  useManagedSystems,
} from '../../hooks/api/useManagedSystems';
import { usePermissions } from '../../hooks/usePermissions';
import { AddSourceFlow } from './AddSourceFlow';
import { DatSubscriptionCard } from './DatSubscriptionCard';
import { SourceActions, sourcePath } from './SourceActions';
import { SourceHelp } from './SourceHelp';
import { SourceStatusBadge } from './SourceStatusBadge';
import { SourceSubscriptionActions } from './SourceSubscriptionActions';
import classes from './Sources.module.css';
import { connectableCatalog, datProviderLabel } from './sourceCatalog';

function attentionRank(subscription: DatCatalogSubscription | undefined) {
  switch (subscription?.state) {
    case 'ReadyToImport':
    case 'UpdateAvailable':
      return 0;
    case 'CheckFailed':
    case 'NotChecked':
      return subscription.automaticChecksPaused ? 3 : 1;
    case 'Applying':
      return 2;
    default:
      return 3;
  }
}

export function SourcesTab({ systemKey }: { systemKey: string }) {
  const dats = useDatsByPlatform(systemKey);
  const { role, canManageSources } = usePermissions();
  const isAdmin = role === 'Admin';
  const systems = useManagedSystems('', {
    enabled: isAdmin,
  });
  const tracked = useDatSubscriptions({
    enabled: isAdmin,
  });
  const { state } = useLocation();
  const system = systems.data?.find((s) => s.key === systemKey);
  const [params, setParams] = useSearchParams();
  const [adding, setAdding] = useState(false);
  const [search, setSearch] = useState('');
  const subscriptions = isAdmin
    ? (tracked.data?.filter((s) => s.systemKey === systemKey) ?? [])
    : [];
  const hasConnectableSource = dats.data?.some(
    (dat) =>
      dat.sourceStatus === 'Active' &&
      !dat.hasSubscription &&
      !subscriptions.some((subscription) => subscription.activeDatId === dat.id),
  );
  const shouldCheckDirectory = isAdmin && tracked.isSuccess && !!hasConnectableSource;
  const directory = useDatCatalogs({
    enabled: shouldCheckDirectory,
  });
  const directoryUnavailable =
    shouldCheckDirectory &&
    (directory.isError || (directory.data?.enabled && !!directory.data.message));
  const selected = dats.data?.find((d) => d.id === params.get('dat'));
  const pending = subscriptions.filter(
    (s) => !s.activeDatId || !dats.data?.some((d) => d.id === s.activeDatId),
  );
  const setupRequested = params.get('setup') === '1';
  const empty =
    dats.isSuccess && (!isAdmin || tracked.isSuccess) && !dats.data.length && !pending.length;
  const closeSetup = () => {
    setAdding(false);
    if (!setupRequested) return;
    const next = new URLSearchParams(params);
    next.delete('setup');
    setParams(next, {
      replace: true,
      state,
    });
  };
  if (selected) {
    const next = new URLSearchParams();
    const sourceTitle = params.get('sourceTitle');
    if (sourceTitle !== null) next.set('sourceTitle', sourceTitle);
    return (
      <Navigate
        replace
        state={state}
        to={`${sourcePath(systemKey, selected)}${next.size ? `?${next}` : ''}`}
      />
    );
  }
  const rows = [
    ...(dats.data ?? []).map((dat) => ({
      key: dat.sourceId ?? dat.id,
      name: dat.name,
      dat,
      subscription: subscriptions.find((item) => item.activeDatId === dat.id),
    })),
    ...pending.map((subscription) => ({
      key: subscription.id,
      name: subscription.name,
      dat: undefined,
      subscription,
    })),
  ]
    .filter(({ name }) => name.toLowerCase().includes(search.trim().toLowerCase()))
    .sort((a, b) => attentionRank(a.subscription) - attentionRank(b.subscription));
  return (
    <Stack gap="md">
      <Group justify="space-between">
        <Group gap="xs">
          <Title
            order={2}
            className={workspace.sectionHeading}
          >
            Catalog sources
          </Title>
          <SourceHelp />
        </Group>
        {isAdmin && !empty && (
          <Button
            {...workspaceActionProps}
            leftSection={<IconPlus size={16} />}
            onClick={() => setAdding(true)}
            disabled={!system}
          >
            Add source
          </Button>
        )}
      </Group>
      {isAdmin && tracked.isError && (
        <Alert color="yellow">
          Subscription status could not be loaded.
          <Button
            variant="subtle"
            onClick={() => void tracked.refetch()}
          >
            Retry status
          </Button>
        </Alert>
      )}
      {directoryUnavailable && (
        <Alert
          color="yellow"
          role="status"
        >
          Subscription directory unavailable. You can still upload new DAT versions and review saved
          updates.
          <Button
            variant="subtle"
            loading={directory.isFetching}
            onClick={() => void directory.refetch()}
          >
            Retry directory
          </Button>
        </Alert>
      )}
      {dats.isPending ? (
        <Skeleton height={180} />
      ) : dats.isError ? (
        <Alert
          color="red"
          title="Sources could not be loaded"
        >
          <Button onClick={() => void dats.refetch()}>Retry sources</Button>
        </Alert>
      ) : (
        <>
          {params.has('dat') && (
            <Alert
              color="yellow"
              title="That DAT version is no longer installed"
            >
              Choose a source below to inspect its current version.
              <Button
                variant="subtle"
                onClick={() => {
                  const next = new URLSearchParams(params);
                  next.delete('dat');
                  next.delete('sourceTitle');
                  setParams(next, {
                    replace: true,
                    state,
                  });
                }}
              >
                Dismiss
              </Button>
            </Alert>
          )}
          {empty && (
            <Stack
              align="flex-start"
              py="xl"
            >
              <Title
                order={3}
                className={workspace.sectionHeading}
              >
                No DAT installed
              </Title>
              <Text
                size="sm"
                c="dimmed"
              >
                Add a DAT to identify releases and match ROM files.
              </Text>
              {isAdmin && (
                <Button
                  {...workspaceActionProps}
                  onClick={() => setAdding(true)}
                  disabled={!system}
                >
                  Add first DAT
                </Button>
              )}
            </Stack>
          )}
          {(dats.data?.length ?? 0) + pending.length > 5 && (
            <TextInput
              aria-label="Filter sources"
              placeholder="Find a source..."
              leftSection={<IconSearch size={16} />}
              value={search}
              onChange={(e) => setSearch(e.currentTarget.value)}
            />
          )}
          {rows.length > 0 && (
            <div className={classes.list}>
              <div
                className={classes.listHeader}
                aria-hidden="true"
              >
                <span>Source</span>
                <span>Installed DAT</span>
                <span>Updates</span>
              </div>
              {rows.map(({ key, name, dat, subscription }) => {
                const path = dat ? sourcePath(systemKey, dat) : undefined;
                const subscribed = !!(subscription || dat?.hasSubscription);
                const provider = dat && datProviderLabel(dat.type);
                const catalog =
                  dat && shouldCheckDirectory && !directoryUnavailable
                    ? connectableCatalog(directory.data, subscriptions, systemKey, dat)
                    : undefined;
                const canUpload = canManageSources && !subscribed && !!path;
                return (
                  <article
                    className={classes.row}
                    aria-label={name}
                    key={key}
                  >
                    <Stack gap={5}>
                      {path ? (
                        <Link
                          className={classes.name}
                          to={path}
                          state={state}
                        >
                          {name}
                        </Link>
                      ) : (
                        <Text
                          fw={600}
                          size="sm"
                        >
                          {name}
                        </Text>
                      )}
                      {dat && (
                        <Group gap="xs">
                          {provider && (
                            <Text
                              size="xs"
                              c="dimmed"
                            >
                              {provider}
                            </Text>
                          )}
                          <SourceStatusBadge status={dat.sourceStatus} />
                        </Group>
                      )}
                    </Stack>
                    <Stack gap={3}>
                      <Text size="sm">
                        {dat ? (dat.version ?? 'Version not specified') : 'No DAT installed'}
                      </Text>
                      {dat && (
                        <Text
                          size="xs"
                          c="dimmed"
                        >
                          {(dat.gameCount ?? 0).toLocaleString()} DAT{' '}
                          {(dat.gameCount ?? 0) === 1 ? 'entry' : 'entries'}
                        </Text>
                      )}
                    </Stack>
                    <Group
                      justify="space-between"
                      align="flex-start"
                      wrap="nowrap"
                      gap="xs"
                    >
                      <Stack
                        gap={6}
                        className={classes.updateControls}
                      >
                        <Text
                          size="xs"
                          c="dimmed"
                        >
                          {subscribed ? 'Subscription' : 'Manual updates'}
                        </Text>
                        {subscription && (
                          <SourceSubscriptionActions
                            key={subscription.id}
                            subscription={subscription}
                            compact
                            manage={!dat}
                            onAccepted={() => {}}
                          />
                        )}
                        {dat && catalog ? (
                          <DatSubscriptionCard
                            dat={dat}
                            catalogId={catalog.catalogId}
                            compact
                          />
                        ) : canUpload && path ? (
                          <Button
                            component={Link}
                            to={`${path}?tab=updates`}
                            state={state}
                            size="compact-sm"
                            variant="light"
                            w="fit-content"
                          >
                            Upload new version
                          </Button>
                        ) : null}
                      </Stack>
                      {dat && path && (
                        <SourceActions
                          dat={dat}
                          path={path}
                          showUpdates={isAdmin && subscribed}
                          offerManualUpload={canUpload && !!catalog}
                        />
                      )}
                    </Group>
                  </article>
                );
              })}
            </div>
          )}
          {search.trim() && !rows.length && (
            <Text
              c="dimmed"
              size="sm"
            >
              No sources match this search.
            </Text>
          )}
        </>
      )}
      {isAdmin && (adding || setupRequested) && system && (
        <AddSourceFlow
          system={system}
          onClose={closeSetup}
        />
      )}
    </Stack>
  );
}
