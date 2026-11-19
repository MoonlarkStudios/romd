import {
  Alert,
  Button,
  Checkbox,
  Group,
  Skeleton,
  Stack,
  Text,
  TextInput,
  Title,
  UnstyledButton,
} from '@mantine/core';
import { useDebouncedValue, useElementSize } from '@mantine/hooks';
import { IconPlus, IconSearch, IconUpload } from '@tabler/icons-react';
import { useQueryClient } from '@tanstack/react-query';
import { type MouseEvent, useEffect, useLayoutEffect, useRef, useState } from 'react';
import { Link, useLocation, useNavigate, useSearchParams } from 'react-router';
import workspace from '../../components/Workspace/Workspace.module.css';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import { datKeys } from '../../hooks/api/useDats';
import {
  managedSystemKeys,
  useDatSubscriptions,
  useManagedSystems,
} from '../../hooks/api/useManagedSystems';
import { usePermissions } from '../../hooks/usePermissions';
import { AddSystemFlow } from './AddSystemFlow';
import { SystemHelp } from './SystemHelp';
import { SystemInspector, systemInspectorId } from './SystemInspector';
import { SystemLogo, useSystemIcons } from './SystemLogo';
import classes from './Systems.module.css';
import { DatImportDate, datCountLabel } from './systemMetrics';
import { systemStateColors } from './systemState';
import { rememberSystemsListScroll, restoreSystemsListScroll } from './systemsListNavigation';
import {
  catalogLabel,
  groupSystemSubscriptions,
  systemNeedsAttention,
  systemSubscriptionStatus,
  systemUpdateLabels,
} from './systemsListStatus';

// Measure the workspace after the navigation rail and its gutters.
const inspectorMinWidth = 880;

export function Systems() {
  const { ref, width } = useElementSize<HTMLDivElement>();
  const [params, setParams] = useSearchParams();
  const location = useLocation();
  const navigate = useNavigate();
  const search = params.get('q') ?? '';
  const [searched] = useDebouncedValue(search.trim(), 180);
  const attentionOnly = params.get('attention') === '1';
  const selectedKey = params.get('system');
  const systems = useManagedSystems();
  const matches = useManagedSystems(searched, {
    enabled: !!searched,
  });
  const canSetUp = usePermissions().hasRole('Admin');
  const subscriptions = useDatSubscriptions({
    enabled: canSetUp,
    pollApplying: true,
  });
  const client = useQueryClient();
  const applyingIds = useRef<string[]>([]);
  const icons = useSystemIcons();
  const [setup, setSetup] = useState<'system' | 'upload'>();

  const bySystem = groupSystemSubscriptions(canSetUp ? (subscriptions.data ?? []) : []);
  const enabled = (systems.data ?? []).filter((system) => system.enabled);
  const findStatus = (key: string) => systemSubscriptionStatus(bySystem.get(key) ?? []);
  const attentionCount = enabled.filter((system) =>
    systemNeedsAttention(system, findStatus(system.key)),
  ).length;
  const matching = search.trim()
    ? (matches.data ?? []).filter((system) => system.enabled)
    : enabled;
  const rows = matching
    .map((system) => ({
      system,
      status: findStatus(system.key),
    }))
    .filter(({ system, status }) => !attentionOnly || systemNeedsAttention(system, status));
  const searching = search.trim() !== searched || (!!searched && matches.isPending);
  const searchFailed = !!searched && matches.isError;
  const subscriptionState = !canSetUp
    ? 'restricted'
    : subscriptions.isError
      ? 'unavailable'
      : subscriptions.isPending
        ? 'pending'
        : 'available';
  const showInspector = width >= inspectorMinWidth;
  const inlineCatalog = width > 0 && width < 520;
  const showImportDate = width >= 1100;
  const selected = showInspector
    ? (rows.find(({ system }) => system.key === selectedKey) ?? rows[0])
    : undefined;
  const returnPath = `${location.pathname}${location.search}`;
  const returnState = {
    systemsReturn: returnPath,
  };
  const listLoaded = !!systems.data && width > 0 && !searching && !searchFailed;

  useLayoutEffect(() => {
    if (listLoaded) restoreSystemsListScroll(returnPath);
  }, [
    listLoaded,
    returnPath,
  ]);

  useEffect(() => {
    if (!subscriptions.isSuccess) return;
    const next = new Set(
      subscriptions.data
        .filter((subscription) => subscription.state === 'Applying')
        .map(({ id }) => id),
    );
    if (applyingIds.current.some((id) => !next.has(id))) {
      void client.invalidateQueries({
        queryKey: managedSystemKeys.all,
      });
      void client.invalidateQueries({
        queryKey: datKeys.all,
      });
    }
    applyingIds.current = [
      ...next,
    ];
  }, [
    client,
    subscriptions.data,
    subscriptions.isSuccess,
  ]);

  const changeFilter = (name: 'q' | 'attention' | 'system', value: string) => {
    setParams(
      (previous) => {
        const next = new URLSearchParams(previous);
        if (value) next.set(name, value);
        else next.delete(name);
        return next;
      },
      {
        replace: true,
      },
    );
  };

  const rememberPosition = (event: MouseEvent<HTMLAnchorElement>) => {
    if (
      event.defaultPrevented ||
      event.button !== 0 ||
      event.altKey ||
      event.ctrlKey ||
      event.metaKey ||
      event.shiftKey
    )
      return;
    rememberSystemsListScroll(returnPath);
  };

  return (
    <div className={workspace.page}>
      <div
        className={classes.workspace}
        ref={ref}
      >
        <Stack gap="md">
          <Group
            justify="space-between"
            className={workspace.hero}
          >
            <Group gap="xs">
              <Title
                order={1}
                className={workspace.heading}
              >
                Systems
              </Title>
              <SystemHelp />
            </Group>
            {canSetUp && (
              <Group gap="xs">
                <Button
                  variant="default"
                  leftSection={<IconUpload size={16} />}
                  onClick={() => setSetup('upload')}
                >
                  Upload a DAT
                </Button>
                <Button
                  {...workspaceActionProps}
                  leftSection={<IconPlus size={16} />}
                  onClick={() => setSetup('system')}
                >
                  Add system
                </Button>
              </Group>
            )}
          </Group>
          {systems.isPending ? (
            <Skeleton h={180} />
          ) : systems.isError ? (
            <Alert
              color="red"
              title="Could not load systems"
            >
              <Button
                variant="subtle"
                onClick={() => void systems.refetch()}
              >
                Retry
              </Button>
            </Alert>
          ) : !enabled.length ? (
            <Stack
              align="flex-start"
              py="xl"
            >
              <Title
                order={2}
                className={workspace.sectionHeading}
              >
                No systems added
              </Title>
              {canSetUp && (
                <Button
                  {...workspaceActionProps}
                  onClick={() => setSetup('system')}
                >
                  Choose your first system
                </Button>
              )}
            </Stack>
          ) : (
            <div
              className={classes.layout}
              data-layout={selected && !searching && !searchFailed ? 'split' : 'compact'}
            >
              <Stack
                gap="sm"
                className={classes.directory}
              >
                <Group
                  gap="sm"
                  className={classes.filters}
                >
                  <TextInput
                    className={classes.search}
                    aria-label="Filter your systems"
                    placeholder="Find a system or alias"
                    leftSection={<IconSearch size={16} />}
                    value={search}
                    onChange={(event) => changeFilter('q', event.currentTarget.value)}
                  />
                  <Checkbox
                    label={`Needs attention${attentionCount > 0 ? ` (${attentionCount.toLocaleString()})` : ''}`}
                    checked={attentionOnly}
                    onChange={(event) =>
                      changeFilter('attention', event.currentTarget.checked ? '1' : '')
                    }
                  />
                </Group>
                {canSetUp && subscriptions.isError && (
                  <Alert
                    color="yellow"
                    title="Subscription status is unavailable"
                  >
                    The attention filter includes known catalog issues and any last reported source
                    issues.
                    <Button
                      variant="subtle"
                      onClick={() => void subscriptions.refetch()}
                    >
                      Retry status
                    </Button>
                  </Alert>
                )}
                {searching ? (
                  <Skeleton h={100} />
                ) : searchFailed ? (
                  <Alert
                    color="red"
                    title="System search failed"
                  >
                    <Button
                      variant="subtle"
                      onClick={() => void matches.refetch()}
                    >
                      Retry search
                    </Button>
                  </Alert>
                ) : !rows.length ? (
                  <Stack
                    align="flex-start"
                    py="xl"
                  >
                    <Text>
                      {attentionOnly
                        ? 'No matching systems with known issues.'
                        : 'No matching systems.'}
                    </Text>
                    <Button
                      variant="subtle"
                      onClick={() =>
                        setParams(
                          (previous) => {
                            const next = new URLSearchParams(previous);
                            next.delete('q');
                            next.delete('attention');
                            return next;
                          },
                          {
                            replace: true,
                          },
                        )
                      }
                    >
                      Clear filters
                    </Button>
                  </Stack>
                ) : (
                  <table
                    className={classes.table}
                    data-catalog={inlineCatalog ? 'inline' : 'column'}
                    data-row-action={showInspector ? 'preview' : 'open'}
                    data-import-date={showImportDate || undefined}
                    aria-label="Systems"
                  >
                    <thead>
                      <tr>
                        <th
                          scope="col"
                          className={classes.nameColumn}
                        >
                          System
                        </th>
                        {!inlineCatalog && (
                          <>
                            <th
                              scope="col"
                              className={classes.titleCountColumn}
                            >
                              Known titles
                            </th>
                            <th
                              scope="col"
                              className={classes.datCountColumn}
                            >
                              DATs
                            </th>
                          </>
                        )}
                        {showImportDate && (
                          <th
                            scope="col"
                            className={classes.importDateColumn}
                          >
                            Last DAT import
                          </th>
                        )}
                        {!inlineCatalog && (
                          <th
                            scope="col"
                            className={classes.catalogColumn}
                          >
                            Catalog
                          </th>
                        )}
                        <th
                          scope="col"
                          className={classes.updatesColumn}
                        >
                          Source updates
                        </th>
                      </tr>
                    </thead>
                    <tbody>
                      {rows.map(({ system, status }) => {
                        const isSelected = selected?.system.key === system.key;
                        const statusLabel = catalogLabel(system.state);
                        const availability = (
                          <Text
                            size="sm"
                            c={systemStateColors[system.state] ?? 'dimmed'}
                            className={classes.catalogStatus}
                          >
                            {statusLabel}
                          </Text>
                        );
                        return (
                          <tr
                            key={system.key}
                            data-selected={isSelected || undefined}
                          >
                            <td>
                              <div className={classes.systemCell}>
                                <SystemLogo
                                  icon={icons.get(system.key)}
                                  size="sm"
                                />
                                <div className={classes.systemLabels}>
                                  <Text
                                    component={Link}
                                    size="sm"
                                    className={`romd-card-title ${classes.systemName}`}
                                    to={`/systems/${system.key}`}
                                    state={returnState}
                                    onClick={rememberPosition}
                                  >
                                    {system.name}
                                  </Text>
                                  <Text
                                    component="span"
                                    size="xs"
                                    c="dimmed"
                                  >
                                    {inlineCatalog
                                      ? `${(system.knownTitles ?? 0).toLocaleString()} known titles · ${datCountLabel(system.catalogCount)}`
                                      : system.shortName !== system.name && system.shortName}
                                  </Text>
                                </div>
                                {showInspector && (
                                  <UnstyledButton
                                    className={classes.previewAction}
                                    aria-label={`Preview ${system.name} in sidebar`}
                                    aria-pressed={isSelected}
                                    aria-controls={systemInspectorId}
                                    onClick={() => changeFilter('system', system.key)}
                                  >
                                    <span className={classes.previewLabel}>Preview</span>
                                  </UnstyledButton>
                                )}
                              </div>
                              {inlineCatalog && (
                                <div className={classes.inlineCatalog}>{availability}</div>
                              )}
                            </td>
                            {!inlineCatalog && (
                              <>
                                <td className={classes.numberCell}>
                                  {(system.knownTitles ?? 0).toLocaleString()}
                                </td>
                                <td className={classes.numberCell}>
                                  {system.catalogCount.toLocaleString()}
                                </td>
                              </>
                            )}
                            {showImportDate && (
                              <td className={classes.importDateCell}>
                                <DatImportDate value={system.lastDatImportedAt} />
                              </td>
                            )}
                            {!inlineCatalog && <td>{availability}</td>}
                            <td>
                              {systemUpdateLabels(system, status, subscriptionState).map(
                                ({ label, color }) => (
                                  <Text
                                    key={label}
                                    size="sm"
                                    c={color}
                                  >
                                    {label}
                                  </Text>
                                ),
                              )}
                            </td>
                          </tr>
                        );
                      })}
                    </tbody>
                  </table>
                )}
              </Stack>
              {selected && !searching && !searchFailed && (
                <SystemInspector
                  key={selected.system.key}
                  system={selected.system}
                  icon={icons.get(selected.system.key)}
                  status={selected.status}
                  subscriptions={bySystem.get(selected.system.key) ?? []}
                  subscriptionState={subscriptionState}
                  canSetUp={canSetUp}
                  returnPath={returnPath}
                  onOpen={rememberPosition}
                />
              )}
            </div>
          )}
          {setup && (
            <AddSystemFlow
              startWithUpload={setup === 'upload'}
              onClose={() => setSetup(undefined)}
              onSystemOpen={(systemKey, tab) => {
                rememberSystemsListScroll(returnPath);
                navigate(`/systems/${systemKey}${tab ? `?tab=${tab}` : ''}`, {
                  state: returnState,
                });
              }}
            />
          )}
        </Stack>
      </div>
    </div>
  );
}
