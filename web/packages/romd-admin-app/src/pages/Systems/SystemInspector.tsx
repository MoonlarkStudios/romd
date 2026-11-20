import { Alert, Badge, Button, Group, Skeleton, Stack, Text, Title } from '@mantine/core';
import type { DatCatalogSubscription, ManagedSystem, SystemResource } from '@romd/admin-api-client';
import type { MouseEventHandler } from 'react';
import { Link } from 'react-router';
import { InspectorPanel } from '../../components/Workspace/InspectorPanel';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import { useDatsByPlatform } from '../../hooks/api/useDats';
import { usePlatform } from '../../hooks/api/usePlatforms';
import { SourceStatusBadge } from './SourceStatusBadge';
import { SourceSubscriptionActions } from './SourceSubscriptionActions';
import { SystemLogo } from './SystemLogo';
import classes from './Systems.module.css';
import { DatImportDate, datCountLabel } from './systemMetrics';
import { systemStateColors } from './systemState';
import { catalogLabel, type systemSubscriptionStatus } from './systemsListStatus';

export const systemInspectorId = 'systems-inspector';

const shownSources = 3;

function subscriptionActionStatus(subscription: DatCatalogSubscription): string {
  switch (subscription.state) {
    case 'CheckFailed':
      return 'Check failed';
    case 'NotChecked':
      return 'Not checked';
    case 'Applying':
      return subscription.activeDatId ? 'Applying update' : 'Importing DAT';
    default:
      return subscription.activeDatId ? 'Update to review' : 'First DAT to review';
  }
}

function InstalledSources({
  system,
  subscriptions,
  subscriptionState,
}: {
  system: ManagedSystem;
  subscriptions: DatCatalogSubscription[];
  subscriptionState: 'available' | 'pending' | 'unavailable' | 'restricted';
}) {
  const dats = useDatsByPlatform(system.catalogCount > 0 ? system.key : undefined);
  if (system.catalogCount === 0)
    return (
      <Text
        size="sm"
        c="dimmed"
      >
        No DAT installed
      </Text>
    );
  if (dats.isPending) return <Skeleton height={52} />;
  if (dats.isError)
    return (
      <Alert
        color="yellow"
        title="Installed sources could not be loaded"
      >
        <Button
          variant="subtle"
          size="compact-sm"
          onClick={() => void dats.refetch()}
        >
          Retry sources
        </Button>
      </Alert>
    );

  const installed = dats.data ?? [];
  if (installed.length === 0)
    return (
      <Text
        size="sm"
        c="dimmed"
      >
        No DAT installed
      </Text>
    );
  return (
    <>
      <ul className={classes.sources}>
        {installed.slice(0, shownSources).map((dat) => {
          const subscribed =
            dat.hasSubscription ||
            subscriptions.some((subscription) => subscription.activeDatId === dat.id);
          const metadata = [
            dat.version,
            subscribed
              ? 'Subscription'
              : dat.hasSubscription === false || subscriptionState === 'available'
                ? 'Manual updates'
                : null,
          ]
            .filter(Boolean)
            .join(' · ');
          return (
            <li key={dat.id}>
              <Text
                size="sm"
                className="romd-card-title"
              >
                {dat.name}
              </Text>
              {metadata && (
                <Text
                  size="xs"
                  c="dimmed"
                >
                  {metadata}
                </Text>
              )}
              {dat.sourceStatus && dat.sourceStatus !== 'Active' && (
                <SourceStatusBadge status={dat.sourceStatus} />
              )}
            </li>
          );
        })}
      </ul>
      {installed.length > shownSources && (
        <Text
          size="xs"
          c="dimmed"
        >
          {installed.length - shownSources} more in Catalog sources
        </Text>
      )}
    </>
  );
}

export function SystemInspector({
  system,
  icon,
  subscriptions,
  subscriptionState,
  status,
  canSetUp,
  returnPath,
  onOpen,
  onClose,
  navigation,
}: {
  system: ManagedSystem;
  icon?: SystemResource['icon'];
  subscriptions: DatCatalogSubscription[];
  subscriptionState: 'available' | 'pending' | 'unavailable' | 'restricted';
  status: ReturnType<typeof systemSubscriptionStatus>;
  canSetUp: boolean;
  returnPath: string;
  onOpen: MouseEventHandler<HTMLAnchorElement>;
  onClose: () => void;
  navigation: {
    onPrevious?: () => void;
    onNext?: () => void;
  };
}) {
  const systemPath = `/systems/${system.key}`;
  const platform = usePlatform(system.key);
  const sourcesPath = `${systemPath}?tab=sources`;
  const state = {
    systemsReturn: returnPath,
  };
  const next = canSetUp
    ? ([
        status.firstDats,
        status.updates,
        status.failed,
        status.unchecked,
        status.applying.filter((item) => item.jobId),
      ].find((group) => group.length > 0) ?? [])
    : [];
  const attention = system.state === 'NeedsAttention' && system.catalogCount === 0;
  const needsCatalog =
    canSetUp &&
    system.state === 'NeedsCatalog' &&
    subscriptionState === 'available' &&
    !subscriptions.length;
  const hasAction = next.length === 1 || needsCatalog || attention;

  return (
    <InspectorPanel
      id={systemInspectorId}
      title={system.name}
      leading={
        <SystemLogo
          icon={icon}
          size="lg"
        />
      }
      onClose={onClose}
      navigation={navigation}
    >
      <Stack gap="md">
        <Text
          size="xs"
          c="dimmed"
        >
          {[
            system.manufacturer,
            system.shortName,
          ]
            .filter(Boolean)
            .join(' · ')}
        </Text>
        {platform.data?.description && (
          <Text
            size="sm"
            className={classes.inspectorIdentity}
          >
            {platform.data.description}
          </Text>
        )}
        <Stack gap="xs">
          <Group
            wrap="wrap"
            justify="space-between"
          >
            <Title
              order={3}
              size="h6"
            >
              Catalog sources
            </Title>
            <Badge
              variant="light"
              color={systemStateColors[system.state] ?? 'gray'}
            >
              {catalogLabel(system.state)}
            </Badge>
          </Group>
          <Text
            size="xs"
            c="dimmed"
          >
            {datCountLabel(system.catalogCount)}
            {system.lastDatImportedAt && (
              <>
                {' '}
                · Last DAT import <DatImportDate value={system.lastDatImportedAt} />
              </>
            )}
          </Text>
          <InstalledSources
            system={system}
            subscriptions={subscriptions}
            subscriptionState={subscriptionState}
          />
          {system.message && (
            <Text
              size="sm"
              c="dimmed"
            >
              {system.message}
            </Text>
          )}
        </Stack>
        <Text size="xs">
          <Link
            to={`${systemPath}?tab=titles&view=all`}
            state={state}
            onClick={onOpen}
          >
            {(system.knownTitles ?? 0).toLocaleString()} known titles
          </Link>
          {' · '}
          <Text
            component="span"
            size="xs"
            c="dimmed"
          >
            {system.ownedTitles.toLocaleString()} with files
          </Text>
        </Text>
        {next.length === 1 && (
          <Text
            size="xs"
            c="dimmed"
          >
            {next[0].name} · {subscriptionActionStatus(next[0])}
          </Text>
        )}
        <Group
          gap="xs"
          align="flex-start"
        >
          {next.length === 1 && (
            <SourceSubscriptionActions
              key={next[0].id}
              primaryOnly
              subscription={next[0]}
              onAccepted={() => {}}
            />
          )}
          {!next.length && needsCatalog && (
            <Button
              {...workspaceActionProps}
              component={Link}
              to={`${sourcesPath}&setup=1`}
              state={state}
              onClick={onOpen}
            >
              Set up catalog
            </Button>
          )}
          {!next.length && attention && (
            <Button
              component={Link}
              to="/jobs"
              variant="light"
              onClick={onOpen}
            >
              View jobs
            </Button>
          )}
          <Button
            {...workspaceActionProps}
            component={Link}
            to={systemPath}
            state={state}
            onClick={onOpen}
            variant={hasAction ? 'light' : 'filled'}
          >
            Open system
          </Button>
        </Group>
      </Stack>
    </InspectorPanel>
  );
}
