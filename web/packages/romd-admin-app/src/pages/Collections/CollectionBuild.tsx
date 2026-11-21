import {
  Alert,
  Badge,
  Button,
  Checkbox,
  Group,
  Loader,
  SegmentedControl,
  Select,
  Stack,
  Switch,
  Text,
  TextInput,
  Title,
} from '@mantine/core';
import { useDebouncedValue } from '@mantine/hooks';
import type { CatalogTitle } from '@romd/admin-api-client';
import { IconPlus, IconSearch } from '@tabler/icons-react';
import { useEffect, useRef, useState } from 'react';
import { gamePosterUrl } from '../../components/artworkPresentation';
import { catalogGameLabel } from '../../components/catalogGameLabel';
import { GameCover } from '../../components/GameCover';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import { useCatalogSearch } from '../../hooks/api/useCatalogSearch';
import type { CollectionDetailView as CollectionDetail } from '../../hooks/api/useCollectionManagement';
import {
  useAddCollectionItem,
  useRemoveCollectionItem,
  useReorderCollectionItems,
  useUpdateCollectionItemNote,
} from '../../hooks/api/useCollectionManagement';
import { usePlatforms } from '../../hooks/api/usePlatforms';
import { CollectionItemRow } from './CollectionItemRow';
import classes from './Collections.module.css';

export function CollectionBuild({
  collection,
  canEdit,
  onBusyChange,
  onAttach,
}: {
  collection: CollectionDetail;
  canEdit: boolean;
  onBusyChange: (busy: boolean) => void;
  onAttach?: () => void;
}) {
  const [search, setSearch] = useState('');
  const [debounced] = useDebouncedValue(search, 300);
  const [platform, setPlatform] = useState<string | null>(null);
  const [localFilesOnly, setLocalFilesOnly] = useState(true);
  const [panel, setPanel] = useState(collection.items.length ? 'collection' : 'catalog');
  const [selected, setSelected] = useState<CatalogTitle[]>([]);
  const [accepted, setAccepted] = useState<{
    collectionId: string;
    titleIds: string[];
  }>({
    collectionId: collection.id,
    titleIds: [],
  });
  const [busy, setBusy] = useState(false);
  const running = useRef(false);
  const [message, setMessage] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const platforms = usePlatforms();
  const query = debounced.trim();
  const catalog = useCatalogSearch(
    {
      query: query || undefined,
      sortBy: query ? 'relevance' : undefined,
      systemKey: collection.systemKey ?? platform ?? undefined,
      localFilesOnly,
    },
    {
      enabled: canEdit,
    },
  );
  const add = useAddCollectionItem();
  const remove = useRemoveCollectionItem();
  const reorder = useReorderCollectionItems();
  const note = useUpdateCollectionItemNote();
  const items = [
    ...collection.items,
  ].sort((a, b) => Number(a.sortOrder ?? 0) - Number(b.sortOrder ?? 0));
  const existing = new Set([
    ...items.map((item) => item.titleId),
    ...(accepted.collectionId === collection.id ? accepted.titleIds : []),
  ]);
  const choices = selected.filter((title) => !existing.has(title.id));
  const titles = catalog.data?.pages.flatMap((page) => page.items) ?? [];
  useEffect(() => {
    if (accepted.collectionId !== collection.id) {
      setAccepted({
        collectionId: collection.id,
        titleIds: [],
      });
      return;
    }
    const titleIds = accepted.titleIds.filter(
      (titleId) => !collection.items.some((item) => item.titleId === titleId),
    );
    if (titleIds.length !== accepted.titleIds.length)
      setAccepted({
        ...accepted,
        titleIds,
      });
  }, [
    collection.id,
    collection.items,
    accepted,
  ]);
  useEffect(() => {
    onBusyChange(busy);
    return () => onBusyChange(false);
  }, [
    busy,
    onBusyChange,
  ]);

  async function perform(action: () => Promise<unknown>, success: string, rethrow = false) {
    if (running.current) return;
    running.current = true;
    setBusy(true);
    setError(null);
    setMessage(null);
    try {
      await action();
      setMessage(success);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not save this change.');
      if (rethrow) throw e;
    } finally {
      running.current = false;
      setBusy(false);
    }
  }
  async function addSelected() {
    if (running.current || choices.length === 0) return;
    running.current = true;
    setBusy(true);
    setError(null);
    setMessage(null);
    const batch = [
      ...choices,
    ];
    let completed = 0;
    try {
      for (const title of batch) {
        await add.mutateAsync({
          collectionId: collection.id,
          request: {
            titleId: title.id,
            note: null,
          },
        });
        completed++;
        setAccepted((current) => ({
          collectionId: collection.id,
          titleIds: [
            ...(current.collectionId === collection.id ? current.titleIds : []),
            title.id,
          ],
        }));
        setSelected((current) => current.filter((t) => t.id !== title.id));
      }
      setMessage(`Added ${completed} game${completed === 1 ? '' : 's'} to the collection.`);
    } catch (e) {
      setError(
        `Added ${completed} of ${batch.length} games. Remaining games stay selected. ${e instanceof Error ? e.message : 'Please try again.'}`,
      );
    } finally {
      running.current = false;
      setBusy(false);
    }
  }
  return (
    <Stack gap="lg">
      {(busy || message) && (
        <Text
          size="sm"
          role="status"
        >
          {busy ? 'Saving…' : message}
        </Text>
      )}
      {error && (
        <Alert
          color="red"
          title="Some changes could not be saved"
        >
          {error}
        </Alert>
      )}
      {canEdit && (
        <SegmentedControl
          className={classes.mobileToggle}
          fullWidth
          value={panel}
          onChange={setPanel}
          data={[
            {
              value: 'catalog',
              label: 'Find games',
            },
            {
              value: 'collection',
              label: `Collection (${items.length})`,
            },
          ]}
        />
      )}
      <div className={canEdit ? classes.build : undefined}>
        {canEdit && (
          <div className={`${classes.panel} ${panel === 'collection' ? classes.mobileHidden : ''}`}>
            <Stack gap="md">
              <Title
                order={2}
                className={classes.sectionHeading}
              >
                Find games
              </Title>
              <TextInput
                label="Search catalog"
                value={search}
                onChange={(e) => setSearch(e.currentTarget.value)}
                leftSection={<IconSearch size={16} />}
              />
              {collection.systemKey ? (
                <Text
                  size="sm"
                  c="dimmed"
                >
                  Scoped to{' '}
                  {platforms.data?.find((p) => p.key === collection.systemKey)?.name ??
                    'this collection’s system'}
                  .
                </Text>
              ) : (
                <Select
                  label="System"
                  placeholder="All systems"
                  clearable
                  searchable
                  value={platform}
                  onChange={setPlatform}
                  data={(platforms.data ?? []).map((p) => ({
                    value: p.key,
                    label: p.name,
                  }))}
                />
              )}
              <Switch
                label="Local files only"
                size="sm"
                checked={localFilesOnly}
                onChange={(event) => setLocalFilesOnly(event.currentTarget.checked)}
              />
              <Group justify="space-between">
                <Text size="sm">{choices.length} selected</Text>
                <Button
                  variant="subtle"
                  disabled={busy || selected.length === 0}
                  onClick={() => setSelected([])}
                >
                  Clear selection
                </Button>
              </Group>
              {choices.length > 0 && (
                <Text
                  size="xs"
                  c="dimmed"
                >
                  {choices.map((t) => t.name).join(' · ')}
                </Text>
              )}
              <Button
                {...workspaceActionProps}
                loading={busy}
                disabled={choices.length === 0 || !canEdit}
                onClick={addSelected}
              >
                {choices.length
                  ? `Add ${choices.length} ${choices.length === 1 ? 'game' : 'games'}`
                  : 'Add selected games'}
              </Button>
              <div className={classes.results}>
                {catalog.isError ? (
                  <Alert color="red">
                    Could not search the catalog.{' '}
                    <Button
                      variant="subtle"
                      onClick={() => catalog.refetch()}
                    >
                      Retry
                    </Button>
                  </Alert>
                ) : catalog.isPending || catalog.isPlaceholderData || debounced !== search ? (
                  <Group p="md">
                    <Loader size="sm" />
                    <Text size="sm">Searching games…</Text>
                  </Group>
                ) : titles.length === 0 ? (
                  <Text
                    c="dimmed"
                    py="lg"
                  >
                    {localFilesOnly
                      ? 'No local games match. Turn off “Local files only” to search the catalog.'
                      : 'No games match this search.'}
                  </Text>
                ) : (
                  titles.map((title) => {
                    const { system, availability } = catalogGameLabel(title, platforms.data);
                    return (
                      <div
                        key={title.id}
                        className={classes.catalogRow}
                      >
                        <Checkbox
                          id={`collection-game-${title.id}`}
                          aria-label={`Select ${title.name} · ${system}`}
                          aria-describedby={`collection-game-status-${title.id}`}
                          checked={existing.has(title.id) || choices.some((t) => t.id === title.id)}
                          disabled={
                            busy ||
                            existing.has(title.id) ||
                            (choices.length >= 24 && !choices.some((t) => t.id === title.id))
                          }
                          onChange={(e) => {
                            const checked = e.currentTarget.checked;
                            setSelected((current) =>
                              checked
                                ? [
                                    ...current.filter((t) => t.id !== title.id),
                                    title,
                                  ]
                                : current.filter((t) => t.id !== title.id),
                            );
                          }}
                        />
                        <GameCover url={gamePosterUrl(title.artwork)} />
                        <label
                          htmlFor={`collection-game-${title.id}`}
                          className={classes.catalogGameLabel}
                        >
                          <Text
                            fw={550}
                            size="sm"
                          >
                            {title.name}
                          </Text>
                          <Text
                            size="xs"
                            c="dimmed"
                          >
                            {system} ·{' '}
                            <span id={`collection-game-status-${title.id}`}>{availability}</span>
                          </Text>
                          {existing.has(title.id) && (
                            <Badge
                              color="gray"
                              size="xs"
                              mt={4}
                            >
                              In collection
                            </Badge>
                          )}
                        </label>
                      </div>
                    );
                  })
                )}
                {catalog.hasNextPage && (
                  <Button
                    fullWidth
                    variant="subtle"
                    mt="md"
                    loading={catalog.isFetchingNextPage}
                    onClick={() => catalog.fetchNextPage()}
                  >
                    More games
                  </Button>
                )}
              </div>
            </Stack>
          </div>
        )}
        <div
          className={`${classes.panel} ${canEdit && panel === 'catalog' ? classes.mobileHidden : ''}`}
        >
          <Group
            justify="space-between"
            mb="lg"
          >
            <Title
              order={2}
              className={classes.sectionHeading}
            >
              The collection
            </Title>
            <Group gap="sm">
              <Badge color="teal">
                {items.length} {items.length === 1 ? 'game' : 'games'}
              </Badge>
              {onAttach && (
                <Button
                  variant="light"
                  {...workspaceActionProps}
                  disabled={busy}
                  onClick={onAttach}
                  leftSection={<IconPlus size={17} />}
                >
                  Add to libraries
                </Button>
              )}
            </Group>
          </Group>
          {items.length === 0 ? (
            <Text
              c="dimmed"
              py="xl"
            >
              {canEdit ? 'Add games from Find games.' : 'No games in this collection.'}
            </Text>
          ) : (
            <Stack gap="sm">
              {items.map((item, index) => (
                <CollectionItemRow
                  key={item.titleId}
                  item={item}
                  index={index}
                  total={items.length}
                  canEdit={canEdit}
                  busy={busy}
                  onMove={(from, direction) => {
                    const next = [
                      ...items,
                    ];
                    const to = from + direction;
                    if (to < 0 || to >= next.length) return;
                    [next[from], next[to]] = [
                      next[to],
                      next[from],
                    ];
                    void perform(
                      () =>
                        reorder.mutateAsync({
                          collectionId: collection.id,
                          titleIds: next.map((t) => t.titleId),
                        }),
                      'Collection order saved.',
                    );
                  }}
                  onRemove={(titleId) => {
                    void perform(
                      () =>
                        remove.mutateAsync({
                          collectionId: collection.id,
                          titleId,
                        }),
                      'Game removed from the collection.',
                    );
                  }}
                  onSaveNote={(text) =>
                    perform(
                      () =>
                        note.mutateAsync({
                          collectionId: collection.id,
                          titleId: item.titleId,
                          note: text,
                        }),
                      'Note saved.',
                      true,
                    )
                  }
                />
              ))}
            </Stack>
          )}
        </div>
      </div>
    </Stack>
  );
}
