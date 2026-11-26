import { Button, Group, SegmentedControl, Skeleton, Stack, Tabs, Text, Title } from '@mantine/core';
import { IconArrowLeft, IconUpload } from '@tabler/icons-react';
import { Link, useLocation, useNavigate, useParams, useSearchParams } from 'react-router';
import { EmptyState } from '../../components/EmptyState';
import workspace from '../../components/Workspace/Workspace.module.css';
import { usePlatform } from '../../hooks/api/usePlatforms';
import { usePermissions } from '../../hooks/usePermissions';
import { TitlesLens } from '../Catalog/TitlesLens';
import { BiosTab } from './BiosTab';
import { SourcesTab } from './SourcesTab';
import { SystemArtwork } from './SystemArtwork';
import { SystemHelp } from './SystemHelp';
import { SystemLogo } from './SystemLogo';
import { SystemMetadata } from './SystemMetadata';
import { SystemSettings } from './SystemSettings';
import { SystemSetupSummary } from './SystemSetupSummary';
import classes from './Systems.module.css';
import { systemsListReturnPath } from './systemsListNavigation';

const HUB_TABS = [
  'metadata',
  'artwork',
  'titles',
  'sources',
  'bios',
  'settings',
] as const;

export function SystemHub() {
  const { systemKey } = useParams<{
    systemKey: string;
  }>();
  const navigate = useNavigate();
  const { state } = useLocation();
  const backToSystems = systemsListReturnPath(state);
  const [params, setParams] = useSearchParams();
  const platformQuery = usePlatform(systemKey);
  const { canUploadRoms } = usePermissions();
  const active =
    HUB_TABS.find((tab) => tab === params.get('tab')) ??
    (params.has('dat') || params.has('setup')
      ? 'sources'
      : params.has('view') || params.has('tracked')
        ? 'titles'
        : 'metadata');
  const view =
    params.get('view') === 'all' || (!params.has('view') && params.get('tracked') === 'untracked')
      ? 'all'
      : 'tracked';
  const setView = (value: string) =>
    setParams(
      (previous) => {
        const next = new URLSearchParams(previous);
        next.set('view', value);
        next.delete('tracked');
        return next;
      },
      {
        state,
      },
    );
  if (platformQuery.isLoading) return <Skeleton height={240} />;
  const platform = platformQuery.data;
  if (platformQuery.isError || !platform || !systemKey)
    return (
      <EmptyState
        title="System not found"
        description="It may have been deleted or merged into another system."
        action={{
          label: 'Back to Systems',
          onClick: () => navigate(backToSystems),
        }}
      />
    );
  const allTitlesParams = new URLSearchParams(params);
  allTitlesParams.set('tab', 'titles');
  allTitlesParams.set('view', 'all');
  allTitlesParams.delete('tracked');
  allTitlesParams.delete('setup');
  return (
    <div className={workspace.page}>
      <Stack gap="md">
        <Button
          component={Link}
          to={backToSystems}
          variant="subtle"
          color="gray"
          p={0}
          w="fit-content"
          leftSection={<IconArrowLeft size={15} />}
        >
          All systems
        </Button>
        <Group
          justify="space-between"
          className={workspace.hero}
          data-testid="system-hub-header"
        >
          <Group
            gap="sm"
            wrap="nowrap"
            className={classes.hubIdentity}
          >
            <SystemLogo
              icon={platform.icon}
              size="lg"
            />
            <Stack
              gap={3}
              miw={0}
            >
              <Group gap="xs">
                <Title
                  order={1}
                  className={workspace.heading}
                  style={{
                    overflowWrap: 'anywhere',
                  }}
                >
                  {platform.name}
                </Title>
                <SystemHelp />
              </Group>
              {platform.compactLabel && platform.compactLabel !== platform.name && (
                <Text
                  size="sm"
                  c="dimmed"
                >
                  {platform.compactLabel}
                </Text>
              )}
            </Stack>
          </Group>
          {canUploadRoms && (
            <Button
              component={Link}
              to="/import"
              variant="default"
              leftSection={<IconUpload size={16} />}
            >
              Import ROMs
            </Button>
          )}
        </Group>
        <SystemSetupSummary
          systemKey={systemKey}
          viewingSources={active === 'sources'}
        />
        <Tabs
          value={active}
          onChange={(value) => {
            if (!value) return;
            const next = new URLSearchParams(params);
            next.set('tab', value);
            next.delete('dat');
            next.delete('sourceTitle');
            next.delete('setup');
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
            <Tabs.Tab value="metadata">Metadata</Tabs.Tab>
            <Tabs.Tab value="artwork">Artwork</Tabs.Tab>
            <Tabs.Tab value="sources">Catalog sources</Tabs.Tab>
            <Tabs.Tab value="titles">Titles</Tabs.Tab>
            <Tabs.Tab value="bios">BIOS</Tabs.Tab>
            <Tabs.Tab value="settings">Settings</Tabs.Tab>
          </Tabs.List>
          <Tabs.Panel value="metadata">
            <SystemMetadata
              key={systemKey}
              systemKey={systemKey}
            />
          </Tabs.Panel>
          <Tabs.Panel value="artwork">
            <SystemArtwork
              key={systemKey}
              system={platform}
            />
          </Tabs.Panel>
          <Tabs.Panel value="titles">
            <Stack gap="md">
              <Group justify="space-between">
                <SegmentedControl
                  aria-label="Title scope"
                  value={view}
                  onChange={setView}
                  data={[
                    {
                      value: 'tracked',
                      label: 'Tracked titles',
                    },
                    {
                      value: 'all',
                      label: 'All titles',
                    },
                  ]}
                />
              </Group>
              <TitlesLens
                key={`${systemKey}-${view}`}
                showHeader={false}
                lockedFilters={{
                  systemKey,
                  ...(view === 'tracked'
                    ? {
                        tracked: 'tracked' as const,
                      }
                    : {}),
                }}
                lockedFilterLabels={{
                  systemKey: platform.compactLabel || platform.name,
                }}
                browseAllHref={`/systems/${systemKey}?${allTitlesParams}`}
              />
            </Stack>
          </Tabs.Panel>
          <Tabs.Panel value="sources">
            <SourcesTab systemKey={systemKey} />
          </Tabs.Panel>
          <Tabs.Panel value="bios">
            <BiosTab systemKey={systemKey} />
          </Tabs.Panel>
          <Tabs.Panel value="settings">
            <SystemSettings systemKey={systemKey} />
          </Tabs.Panel>
        </Tabs>
      </Stack>
    </div>
  );
}
