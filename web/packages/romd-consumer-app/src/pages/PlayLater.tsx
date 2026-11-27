import { Button, Group, SimpleGrid, Stack, Title } from '@mantine/core';
import { IconArrowLeft } from '@tabler/icons-react';
import { Link } from 'react-router';
import { PosterCard } from '../components/library/PosterCard';
import { StatusState } from '../components/status/StatusState';
import { usePlayLaterTitles } from '../hooks/usePlayLater';

export function PlayLater() {
  const saved = usePlayLaterTitles();
  const titles = [
    ...new Map(
      saved.data?.pages.flatMap((page) =>
        page.items.map(
          (title) =>
            [
              title.id,
              title,
            ] as const,
        ),
      ),
    ).values(),
  ];

  return (
    <Stack gap="var(--romd-page-gap)">
      <Group>
        <Button
          variant="subtle"
          color="gray"
          leftSection={<IconArrowLeft size={16} />}
          component={Link}
          to="/"
        >
          Home
        </Button>
      </Group>
      <Title
        order={1}
        className="romd-page-heading"
      >
        Play Later
      </Title>
      {saved.isError ? (
        <StatusState
          kind="error"
          message="Play Later could not be loaded."
          onRetry={() => void saved.refetch()}
          retrying={saved.isRefetching}
        />
      ) : saved.isLoading ? (
        <StatusState
          kind="loading"
          label="Loading Play Later"
        />
      ) : titles.length === 0 ? (
        <StatusState
          kind="empty"
          title="No games saved for later"
          message="Save games from Browse or a game page."
          action={
            <Button
              component={Link}
              to="/library"
              variant="light"
            >
              Browse games
            </Button>
          }
        />
      ) : (
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
              showPlayAction={false}
              showTitle
            />
          ))}
        </SimpleGrid>
      )}
      {saved.hasNextPage && !saved.isError && (
        <Group justify="center">
          <Button
            onClick={() => void saved.fetchNextPage()}
            loading={saved.isFetchingNextPage}
            variant="light"
          >
            Load more
          </Button>
        </Group>
      )}
    </Stack>
  );
}
