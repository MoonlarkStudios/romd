import {
  Stack,
  Title,
  VisuallyHidden,
} from '@mantine/core';
import type { ConsumerCollectionDto } from '@romd/consumer-api-client';
import { SpotlightCarousel } from '../components/library/SpotlightCarousel';
import { TitleRail } from '../components/library/TitleRail';
import { StatusState } from '../components/status/StatusState';
import { useCatalogSearch, useCollections, useCollectionTitles, useCurrentLibrary } from '../hooks/useConsumerLibrary';
import { useRecentlyPlayed } from '../hooks/usePlayActivity';

export function Shelves() {
  const libraryQuery = useCurrentLibrary();
  const collectionsQuery = useCollections();
  const readyToPlayQuery = useCatalogSearch(
    {
      completeness: 'complete',
    },
    {
      limit: 18,
    },
  );
  const topRatedQuery = useCatalogSearch(
    {
      sortBy: 'rating',
    },
    {
      limit: 18,
    },
  );
  const recentlyPlayedQuery = useRecentlyPlayed();

  const library = libraryQuery.data;
  const collections = (collectionsQuery.data ?? []).slice(0, 10);
  const readyToPlay = readyToPlayQuery.data?.pages.flatMap((page) => page.items) ?? [];
  const topRated = topRatedQuery.data?.pages.flatMap((page) => page.items) ?? [];
  const recentlyPlayedIds = new Set((recentlyPlayedQuery.data ?? []).map(title => title.id));
  const spotlights = topRated.filter(title => title.rating != null && Number.isFinite(Number(title.rating))).slice(0, 3);
  const spotlightIds = new Set(spotlights.map(title => title.id));
  const distinctTopRated = topRated.filter(title => !readyToPlay.some(ready => ready.id === title.id) && !recentlyPlayedIds.has(title.id) && !spotlightIds.has(title.id));
  const exploreTitles = readyToPlay.filter(title => !recentlyPlayedIds.has(title.id) && !spotlightIds.has(title.id));

  if (libraryQuery.isLoading || readyToPlayQuery.isLoading || topRatedQuery.isLoading) {
    return <StatusState kind="loading" />;
  }

  if (libraryQuery.isError) {
    return (
      <StatusState
        kind="error"
        message="Your library could not be loaded. Sign out and sign in again if the session expired."
        onRetry={() => {
          void libraryQuery.refetch();
          void readyToPlayQuery.refetch();
          void topRatedQuery.refetch();
        }}
      />
    );
  }

  return (
    <Stack gap="var(--romd-page-gap)" className="romd-home">
      {spotlights.length > 0 ? <SpotlightCarousel key={spotlights.map(title => title.id).join(':')} titles={spotlights} /> : <VisuallyHidden><Title order={1}>Your games</Title></VisuallyHidden>}

      {(recentlyPlayedQuery.data?.length ?? 0) > 0 && (
        <TitleRail
          showPlayAction={false}
          title="Recently Played"
          seeAllTo="/activity"
          titles={recentlyPlayedQuery.data ?? []}
        />
      )}

      {recentlyPlayedQuery.isError && <StatusState kind="error" message="Your recently played games could not be loaded." onRetry={() => void recentlyPlayedQuery.refetch()} />}
      {readyToPlayQuery.isError && <StatusState kind="error" message="Your games could not be loaded." onRetry={() => void readyToPlayQuery.refetch()} />}
      {library?.counts.ownedTitleCount === 0 && <StatusState kind="empty" title="Your next adventure starts here" message="Games will appear here when they are added to your library." />}
      {exploreTitles.length > 0 && (
        <TitleRail
          showPlayAction={false}
          title="Explore your games"
          seeAllTo="/library?completeness=complete"
          titles={exploreTitles}
        />
      )}

      {topRatedQuery.isError && <StatusState kind="error" message="Top rated games could not be loaded." onRetry={() => void topRatedQuery.refetch()} />}

      {distinctTopRated.length > 0 && (
        <TitleRail
          showPlayAction={false}
          title="Top Rated"
          seeAllTo="/library?sortBy=rating"
          titles={distinctTopRated}
        />
      )}

      {collectionsQuery.isError && <StatusState kind="error" message="Your shelves could not be loaded." onRetry={() => void collectionsQuery.refetch()} />}

      {collections.length > 0 && (
        <Stack gap="xl">
          {(collectionsQuery.data ?? []).filter(collection => collection.isFeatured !== false).slice(0, 5).map((collection) => (
            <CollectionTitleRail
              key={collection.id}
              collection={collection}
            />
          ))}
        </Stack>
      )}
    </Stack>
  );
}

interface CollectionTitleRailProps {
  collection: ConsumerCollectionDto;
}

function CollectionTitleRail({ collection }: CollectionTitleRailProps) {
  const titlesQuery = useCollectionTitles(collection.id);
  const titles = titlesQuery.data?.pages.flatMap((page) => page.items) ?? [];

  if (titles.length === 0) {
    return null;
  }

  return (
    <TitleRail
      showPlayAction={false}
      title={collection.name}
      subtitle={collection.description ?? null}
      seeAllTo={`/collections/${collection.id}`}
      titles={titles}
    />
  );
}
