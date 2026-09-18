import {
  ActionIcon,
  Alert,
  Badge,
  Button,
  Group,
  Modal,
  Select,
  Skeleton,
  Stack,
  Switch,
  Text,
  Title,
} from '@mantine/core';
import {
  IconArrowDown,
  IconArrowUp,
  IconExternalLink,
  IconLink,
  IconPlus,
  IconStack2,
  IconUnlink,
} from '@tabler/icons-react';
import { useState } from 'react';
import { Link } from 'react-router';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import { useCollections } from '../../hooks/api/useCollectionManagement';
import {
  useLibraryAttachments,
  useSetLibraryAttachments,
} from '../../hooks/api/useLibraryExperience';
import { CollectionFormModal } from '../Collections/CollectionFormModal';
import classes from './Libraries.module.css';

export function LibraryCollections({ libraryId }: { libraryId: string }) {
  const query = useLibraryAttachments(libraryId);
  const all = useCollections();
  const save = useSetLibraryAttachments(libraryId);
  const [attaching, setAttaching] = useState(false);
  const [creating, setCreating] = useState(false);
  const [selected, setSelected] = useState<string | null>(null);
  const rows = query.data ?? [];
  const items = rows.map((r) => ({
    collectionId: r.collectionId,
    isFeatured: r.isFeatured,
  }));
  const options = (all.data ?? [])
    .filter((c) => !rows.some((r) => r.collectionId === c.id))
    .map((c) => ({
      value: c.id,
      label: c.name,
    }));
  const attach = async (id: string) => {
    try {
      await save.mutateAsync([
        ...items,
        {
          collectionId: id,
          isFeatured: true,
        },
      ]);
      setAttaching(false);
      setSelected(null);
    } catch {
      /* Visible mutation error. */
    }
  };
  const move = (index: number, direction: number) => {
    const next = [
      ...items,
    ];
    [next[index], next[index + direction]] = [
      next[index + direction],
      next[index],
    ];
    save.mutate(next);
  };
  return (
    <Stack gap="lg">
      <Group
        justify="space-between"
        align="flex-start"
      >
        <div>
          <Title
            order={2}
            className={classes.sectionHeading}
          >
            Collections for this audience
          </Title>
          <Text
            c="dimmed"
            mt="xs"
          >
            Shared curation, arranged just for them.
          </Text>
        </div>
        <Group>
          <Button
            variant="subtle"
            {...workspaceActionProps}
            leftSection={<IconPlus size={16} />}
            disabled={query.isPending || query.isError || save.isPending}
            onClick={() => setCreating(true)}
          >
            Create & attach
          </Button>
          <Button
            {...workspaceActionProps}
            leftSection={<IconLink size={16} />}
            disabled={query.isPending || query.isError || save.isPending}
            onClick={() => setAttaching(true)}
          >
            Attach collection
          </Button>
        </Group>
      </Group>
      {query.isError && (
        <Alert color="red">
          Could not load attached collections.{' '}
          <Button
            variant="subtle"
            onClick={() => query.refetch()}
          >
            Retry
          </Button>
        </Alert>
      )}
      {save.isError && (
        <Alert color="red">{save.error.message} Your last saved placement is unchanged.</Alert>
      )}
      {query.isPending ? (
        <Skeleton
          h={180}
          radius="lg"
        />
      ) : !query.isError && rows.length === 0 ? (
        <div className={classes.empty}>
          <IconStack2 size={36} />
          <Title
            order={3}
            mt="md"
          >
            Give them something to discover
          </Title>
          <Text
            c="dimmed"
            mt="sm"
          >
            Attach a shared collection, or curate something new. Your audience’s access rules always
            apply.
          </Text>
        </div>
      ) : (
        rows.map((row, index) => (
          <div
            key={row.collectionId}
            className={classes.shelf}
          >
            <Group
              justify="space-between"
              align="flex-start"
            >
              <Group
                wrap="nowrap"
                align="flex-start"
              >
                {row.coverUrl ? (
                  <img
                    className={classes.shelfArt}
                    src={row.coverUrl}
                    alt=""
                  />
                ) : (
                  <div className={classes.shelfArt}>
                    <IconStack2 size={28} />
                  </div>
                )}
                <Stack gap={5}>
                  <Group gap="xs">
                    <Text
                      fw={650}
                      size="lg"
                    >
                      {row.name}
                    </Text>
                    {row.libraryCount > 1 && (
                      <Badge
                        variant="light"
                        color="teal"
                      >
                        Shared · {row.libraryCount} libraries
                      </Badge>
                    )}
                  </Group>
                  <Text
                    size="sm"
                    c="dimmed"
                  >
                    {row.description || 'A curated collection of games.'}
                  </Text>
                  <Group gap="xs">
                    <Text
                      size="sm"
                      fw={500}
                    >
                      {row.visibleCount} of {row.totalCount} games visible
                    </Text>
                    {row.visibleCount === 0 && <Badge color="yellow">Hidden from audience</Badge>}
                  </Group>
                </Stack>
              </Group>
              <Group gap={4}>
                <ActionIcon
                  variant="subtle"
                  aria-label={`Move ${row.name} up`}
                  disabled={index === 0 || save.isPending}
                  onClick={() => move(index, -1)}
                >
                  <IconArrowUp size={17} />
                </ActionIcon>
                <ActionIcon
                  variant="subtle"
                  aria-label={`Move ${row.name} down`}
                  disabled={index === rows.length - 1 || save.isPending}
                  onClick={() => move(index, 1)}
                >
                  <IconArrowDown size={17} />
                </ActionIcon>
                <ActionIcon
                  variant="subtle"
                  color="gray"
                  aria-label={`Detach ${row.name}`}
                  disabled={save.isPending}
                  onClick={() =>
                    save.mutate(items.filter((i) => i.collectionId !== row.collectionId))
                  }
                >
                  <IconUnlink size={17} />
                </ActionIcon>
              </Group>
            </Group>
            <Group
              justify="space-between"
              mt="lg"
            >
              <Switch
                color="teal"
                label="Featured shelf"
                checked={row.isFeatured}
                disabled={save.isPending}
                onChange={(e) => {
                  const featured = e.currentTarget.checked;
                  save.mutate(
                    items.map((i) =>
                      i.collectionId === row.collectionId
                        ? {
                            ...i,
                            isFeatured: featured,
                          }
                        : i,
                    ),
                  );
                }}
              />
              <Button
                component={Link}
                to={`/collections/${row.collectionId}`}
                variant="subtle"
                color="gray"
                size="xs"
                rightSection={<IconExternalLink size={14} />}
              >
                Edit shared collection
              </Button>
            </Group>
          </div>
        ))
      )}
      <Text
        c="dimmed"
        size="sm"
      >
        Order applies to this library. Ottercade shows the first five nonempty featured collections.
        Detaching keeps the shared collection intact.
      </Text>
      <Modal
        opened={attaching}
        onClose={() => setAttaching(false)}
        title="Attach a shared collection"
        radius="lg"
        centered
      >
        <Stack>
          <Text
            size="sm"
            c="dimmed"
          >
            The collection remains shared. Only games allowed for this audience will appear.
          </Text>
          {all.isError ? (
            <Alert color="red">Could not load available collections.</Alert>
          ) : (
            <Select
              label="Collection"
              placeholder={all.isPending ? 'Loading collections…' : 'Choose an existing collection'}
              searchable
              data={options}
              value={selected}
              onChange={setSelected}
            />
          )}
          <Button
            {...workspaceActionProps}
            disabled={!selected}
            loading={save.isPending}
            onClick={() => {
              if (selected) void attach(selected);
            }}
          >
            Attach collection
          </Button>
        </Stack>
      </Modal>
      <CollectionFormModal
        opened={creating}
        onClose={() => setCreating(false)}
        collection={null}
        onSaved={(id) => {
          setCreating(false);
          void attach(id);
        }}
      />
    </Stack>
  );
}
