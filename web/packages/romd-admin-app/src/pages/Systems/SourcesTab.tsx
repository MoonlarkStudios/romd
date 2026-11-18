import {
  Alert,
  Badge,
  Button,
  Group,
  Skeleton,
  Stack,
  Text,
  TextInput,
  Title,
} from '@mantine/core';
import { IconPlus, IconSearch } from '@tabler/icons-react';
import { useState } from 'react';
import { Link, Navigate, useLocation, useSearchParams } from 'react-router';
import workspace from '../../components/Workspace/Workspace.module.css';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import { useDatsByPlatform } from '../../hooks/api/useDats';
import { useDatSubscriptions, useManagedSystems } from '../../hooks/api/useManagedSystems';
import { usePermissions } from '../../hooks/usePermissions';
import { AddSourceFlow } from './AddSourceFlow';
import { SourceActions, sourcePath } from './SourceActions';
import { SourceHelp } from './SourceHelp';
import { SourceStatusBadge } from './SourceStatusBadge';
import { SourceSubscriptionActions } from './SourceSubscriptionActions';
import classes from './Sources.module.css';

export function SourcesTab({ systemKey }: { systemKey: string }) {
  const dats = useDatsByPlatform(systemKey);
  const systems = useManagedSystems();
  const tracked = useDatSubscriptions();
  const { role } = usePermissions();
  const { state } = useLocation();
  const system = systems.data?.find((s) => s.key === systemKey);
  const [params, setParams] = useSearchParams();
  const [adding, setAdding] = useState(false);
  const [search, setSearch] = useState('');
  const subscriptions = tracked.data?.filter((s) => s.systemKey === systemKey) ?? [];
  const selected = dats.data?.find((d) => d.id === params.get('dat'));
  const pending = subscriptions.filter(
    (s) => !s.activeDatId || !dats.data?.some((d) => d.id === s.activeDatId),
  );
  const setupRequested = params.get('setup') === '1';
  const empty = dats.isSuccess && tracked.isSuccess && !dats.data.length && !pending.length;
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
  const installed =
    dats.data?.filter((d) => d.name.toLowerCase().includes(search.toLowerCase())) ?? [];
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
        {role === 'Admin' && !empty && (
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
      {tracked.isError && (
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
              {role === 'Admin' && (
                <Button
                  {...workspaceActionProps}
                  onClick={() => setAdding(true)}
                  disabled={!system}
                >
                  Set up catalog
                </Button>
              )}
            </Stack>
          )}
          {(dats.data?.length ?? 0) > 5 && (
            <TextInput
              aria-label="Filter sources"
              placeholder="Find a source..."
              leftSection={<IconSearch size={16} />}
              value={search}
              onChange={(e) => setSearch(e.currentTarget.value)}
            />
          )}
          <div className={classes.list}>
            {installed.map((dat) => {
              const subscription = subscriptions.find((s) => s.activeDatId === dat.id);
              const path = sourcePath(systemKey, dat);
              return (
                <article
                  className={classes.row}
                  aria-label={dat.name}
                  key={dat.sourceId ?? dat.id}
                >
                  <Stack gap={5}>
                    <Link
                      className={classes.name}
                      to={path}
                      state={state}
                    >
                      {dat.name}
                    </Link>
                    <Group gap="xs">
                      <Text
                        size="xs"
                        c="dimmed"
                      >
                        {dat.type}
                      </Text>
                      <SourceStatusBadge status={dat.sourceStatus} />
                    </Group>
                  </Stack>
                  <Stack gap={4}>
                    <Text size="sm">{dat.version ?? 'Version not specified'}</Text>
                    <Text
                      size="xs"
                      c="dimmed"
                    >
                      {Number(dat.gameCount ?? 0).toLocaleString()} DAT entries
                    </Text>
                  </Stack>
                  <Group
                    justify="space-between"
                    align="flex-start"
                    wrap="nowrap"
                  >
                    <Stack
                      gap={6}
                      style={{
                        minWidth: 0,
                      }}
                    >
                      <Text
                        size="xs"
                        c="dimmed"
                      >
                        {subscription
                          ? 'Subscription'
                          : tracked.isSuccess
                            ? 'Manual updates'
                            : 'Update method unavailable'}
                      </Text>
                      {subscription && (
                        <SourceSubscriptionActions
                          subscription={subscription}
                          compact
                          onAccepted={() => {}}
                        />
                      )}
                    </Stack>
                    <SourceActions
                      dat={dat}
                      path={path}
                      manual={!subscription && tracked.isSuccess}
                    />
                  </Group>
                </article>
              );
            })}
            {pending
              .filter((s) => s.name.toLowerCase().includes(search.toLowerCase()))
              .map((s) => (
                <article
                  className={classes.row}
                  aria-label={s.name}
                  key={s.id}
                >
                  <Stack gap={4}>
                    <Text
                      fw={600}
                      size="sm"
                    >
                      {s.name}
                    </Text>
                    <Badge
                      color="gray"
                      w="fit-content"
                    >
                      Subscription
                    </Badge>
                  </Stack>
                  <Text
                    size="sm"
                    c="dimmed"
                  >
                    No DAT installed
                  </Text>
                  <SourceSubscriptionActions
                    subscription={s}
                    manage
                    onAccepted={() => {}}
                  />
                </article>
              ))}
          </div>
          {search &&
            !installed.length &&
            !pending.some((s) => s.name.toLowerCase().includes(search.toLowerCase())) && (
              <Text
                c="dimmed"
                size="sm"
              >
                No sources match this search.
              </Text>
            )}
        </>
      )}
      {role === 'Admin' && (adding || setupRequested) && system && (
        <AddSourceFlow
          system={system}
          onClose={closeSetup}
        />
      )}
    </Stack>
  );
}
