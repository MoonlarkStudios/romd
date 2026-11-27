import { Button, Group, SimpleGrid, Stack, Text, Title } from '@mantine/core';
import type { ConsumerPlatformDetail } from '@romd/consumer-api-client';
import { IconArrowLeft } from '@tabler/icons-react';
import { Link, useParams } from 'react-router';
import { PosterCard } from '../components/library/PosterCard';
import { DestinationNavigationName } from '../components/navigation/titleNavigation';
import { StatusState } from '../components/status/StatusState';
import { useCatalogSearch, usePlatformDetail } from '../hooks/useConsumerLibrary';
import { ConsoleLogo, useAvailableArtwork } from './ConsoleArtwork';
import { ConsoleCard } from './ConsoleCard';
import classes from './Consoles.module.css';

function ConsoleHeader({ system }: { system: ConsumerPlatformDetail }) {
  const controller = useAvailableArtwork(system.controllerArtwork);
  const facts = [
    {
      label: 'Manufacturer',
      value: system.manufacturer,
    },
    {
      label: 'First released',
      value: system.hardware?.releaseYear,
    },
    {
      label: 'Generation',
      value: system.hardware?.generation,
    },
    {
      label: 'Hardware',
      value: system.hardware?.category?.replaceAll('-', ' '),
    },
  ];

  return (
    <header
      className={classes.header}
      data-has-artwork={!!controller.src || undefined}
    >
      <Stack
        gap="md"
        className={classes.headerCopy}
      >
        {controller.src && <ConsoleLogo icon={system.icon} />}
        <Title
          order={1}
          className="romd-page-heading"
        >
          {system.name}
        </Title>
        {system.description && <Text className={classes.description}>{system.description}</Text>}
        {facts.some(({ value }) => value != null) && (
          <dl className={classes.facts}>
            {facts.map(
              ({ label, value }) =>
                value != null && (
                  <div key={label}>
                    <dt>{label}</dt>
                    <dd>{value}</dd>
                  </div>
                ),
            )}
          </dl>
        )}
      </Stack>
      <div className={classes.artwork}>
        {controller.src ? (
          <img
            {...controller}
            alt={`${system.name} controller illustration`}
            decoding="async"
            className={classes.portrait}
          />
        ) : (
          <ConsoleLogo
            icon={system.icon}
            fallback
          />
        )}
      </div>
    </header>
  );
}

export function ConsoleDetail() {
  const { systemKey } = useParams();
  const detail = usePlatformDetail(systemKey);
  const system = detail.data;
  const games = useCatalogSearch(
    {
      systemKey: system?.key,
    },
    {
      enabled: !!system,
    },
  );
  const titles = games.data?.pages.flatMap((page) => page.items) ?? [];
  const eraPeers = system?.eraPeers ?? [];

  return (
    <Stack gap="var(--romd-page-gap)">
      <Group>
        <Button
          component={Link}
          to="/systems"
          state={{
            restoreConsoleList: true,
          }}
          variant="subtle"
          color="gray"
          leftSection={<IconArrowLeft size={16} />}
        >
          Consoles
        </Button>
      </Group>
      {detail.isLoading ? (
        <StatusState
          kind="loading"
          label="Loading console"
        />
      ) : detail.isError ? (
        <StatusState
          kind="error"
          message="This console could not be loaded."
          onRetry={() => void detail.refetch()}
          retrying={detail.isRefetching}
        />
      ) : !system ? (
        <StatusState
          kind="empty"
          title="Console unavailable"
          message="There are no games for this console in your library."
        />
      ) : (
        <DestinationNavigationName value={system.shortName}>
          <ConsoleHeader system={system} />
          <Stack
            component="section"
            gap="lg"
            aria-labelledby="console-games"
          >
            <Group
              gap="sm"
              align="baseline"
            >
              <Title
                order={2}
                id="console-games"
              >
                Games
              </Title>
              <Text
                size="sm"
                c="dimmed"
              >
                {system.titleCount}
              </Text>
            </Group>
            {titles.length > 0 && (
              <SimpleGrid
                cols={{
                  base: 2,
                  xs: 3,
                  md: 4,
                  xl: 6,
                }}
                spacing="lg"
              >
                {titles.map((title) => (
                  <PosterCard
                    key={title.id}
                    title={title}
                  />
                ))}
              </SimpleGrid>
            )}
            {games.isLoading && (
              <StatusState
                kind="loading"
                label="Loading games"
              />
            )}
            {games.isError && !games.isFetchNextPageError && (
              <StatusState
                kind="error"
                message="Games for this console could not be loaded."
                onRetry={() => void games.refetch()}
                retrying={games.isRefetching}
              />
            )}
            {games.isSuccess && titles.length === 0 && (
              <StatusState
                kind="empty"
                title="No games available"
              />
            )}
            {games.hasNextPage && (
              <Group justify="center">
                <Button
                  variant="light"
                  onClick={() => void games.fetchNextPage()}
                  loading={games.isFetchingNextPage}
                >
                  {games.isFetchNextPageError ? 'Retry loading games' : 'Load more games'}
                </Button>
              </Group>
            )}
          </Stack>
          {eraPeers.length > 0 && (
            <Stack
              component="section"
              gap="md"
              aria-labelledby="console-era-peers"
            >
              <Title
                order={2}
                id="console-era-peers"
              >
                Also from this era
              </Title>
              <SimpleGrid
                cols={{
                  base: 1,
                  xs: 2,
                  lg: 3,
                }}
                spacing="lg"
              >
                {eraPeers.map((peer) => (
                  <ConsoleCard
                    key={peer.key}
                    system={peer}
                    headingLevel={3}
                    showReleaseYear
                  />
                ))}
              </SimpleGrid>
            </Stack>
          )}
        </DestinationNavigationName>
      )}
    </Stack>
  );
}
