import { Button, Group, SimpleGrid, Stack, Text, Title } from '@mantine/core';
import { IconArrowLeft, IconRefresh } from '@tabler/icons-react';
import { Link, useParams } from 'react-router';
import { PosterCard } from '../components/library/PosterCard';
import { CollectionNavigationName } from '../components/navigation/titleNavigation';
import { StatusState } from '../components/status/StatusState';
import { useCollectionDetail, useCollectionTitles } from '../hooks/useConsumerLibrary';

export function CollectionDetail() {
  const { collectionId } = useParams();
  const collectionQuery = useCollectionDetail(collectionId);
  const titlesQuery = useCollectionTitles(collectionId, {
    limit: 24,
  });

  const titles = titlesQuery.data?.pages.flatMap((page) => page.items) ?? [];

  if (collectionQuery.isLoading) {
    return <StatusState kind="loading" />;
  }

  if (collectionQuery.isError || !collectionQuery.data) {
    return (
      <StatusState
        kind="error"
        message="This collection could not be loaded."
        onRetry={() => void collectionQuery.refetch()}
        retrying={collectionQuery.isRefetching}
      />
    );
  }

  return (
    <CollectionNavigationName value={collectionQuery.data.name}>
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

      <Stack gap={4}>
        <Text className="romd-eyebrow">Shelf</Text>
        <Title
          order={1}
          className="romd-page-heading"
        >
          {collectionQuery.data.name}
        </Title>
        <Text
          size="sm"
          c="dimmed"
        >
          {collectionQuery.data.description ?? collectionQuery.data.system?.name ?? 'Collection'}
        </Text>
      </Stack>

      {titlesQuery.isError ? (
        <StatusState
          kind="error"
          message="Collection titles could not be loaded."
          onRetry={() => void titlesQuery.refetch()}
          retrying={titlesQuery.isRefetching}
        />
      ) : titles.length === 0 && titlesQuery.isLoading ? (
        <StatusState kind="loading" />
      ) : titles.length === 0 ? (
        <StatusState
          kind="empty"
          title="This shelf is empty"
          message="No titles are listed in this collection."
        />
      ) : (
        <SimpleGrid
          cols={{ base: 2, xs: 3, md: 4, xl: 6 }}
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

      {titlesQuery.hasNextPage && (
        <Group justify="center">
          <Button
            leftSection={<IconRefresh size={16} />}
            onClick={() => titlesQuery.fetchNextPage()}
            loading={titlesQuery.isFetchingNextPage}
            variant="light"
          >
            Load more
          </Button>
        </Group>
      )}
    </Stack>
    </CollectionNavigationName>
  );
}
