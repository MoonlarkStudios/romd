import {
  ActionIcon,
  Alert,
  Badge,
  Button,
  Checkbox,
  Group,
  Modal,
  ScrollArea,
  Stack,
  Text,
  TextInput,
} from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { listCollectionLibraryPlacements, listLibraryAttachments } from '@romd/admin-api-client';
import { IconSearch, IconUnlink } from '@tabler/icons-react';
import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { Link } from 'react-router';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import {
  useAttachCollectionToLibraries,
  useLibraryAttachments,
  useSetLibraryAttachments,
} from '../../hooks/api/useLibraryExperience';
import {
  libraryManagementKeys,
  useLibraries,
  useLibrary,
} from '../../hooks/api/useLibraryManagement';
import { collectPages } from '../../utils/collectPages';
import { awaitsFirstPublication } from '../Libraries/libraryRefresh';

export function useCollectionPlacements(collectionId: string, enabled = true) {
  return useQuery({
    enabled,
    queryKey: [
      ...libraryManagementKeys.all,
      'collection-placements',
      collectionId,
    ],
    queryFn: async () => {
      return collectPages(async (cursor) => {
        const response = await listCollectionLibraryPlacements({
          path: {
            collectionId,
          },
          query: {
            cursor,
            limit: 100,
          },
        });
        if (response.error || !response.data) throw new Error('Could not load attached audiences.');
        return response.data;
      });
    },
  });
}

export function CollectionAudiences({ collectionId }: { collectionId: string }) {
  const placements = useCollectionPlacements(collectionId);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  return (
    <Stack gap="sm">
      <Group justify="space-between">
        <Text fw={600}>Attached audiences</Text>
        <Badge color="teal">{placements.data?.length ?? '—'} libraries</Badge>
      </Group>
      <Text
        size="sm"
        c="dimmed"
      >
        Edits to this shared collection update every attached library. Each audience’s access rules
        still apply.
      </Text>
      {placements.isError ? (
        <Alert color="red">{placements.error.message}</Alert>
      ) : (
        placements.data?.map((p) => (
          <Group
            key={p.libraryId}
            justify="space-between"
          >
            <Text
              component={Link}
              to={`/libraries/${p.libraryId}?tab=collections`}
              size="sm"
            >
              {p.name}
            </Text>
            <PlacementCount
              libraryId={p.libraryId}
              collectionId={collectionId}
            />
            <Group gap="sm">
              {p.isFeatured && (
                <Badge
                  color="gray"
                  size="xs"
                >
                  Featured
                </Badge>
              )}
              <DetachAudience
                libraryId={p.libraryId}
                name={p.name}
                collectionId={collectionId}
                busy={busy}
                setBusy={setBusy}
                setError={setError}
              />
            </Group>
          </Group>
        ))
      )}
      {placements.data?.length === 0 && (
        <Text
          c="dimmed"
          size="sm"
        >
          Not attached to an audience yet.
        </Text>
      )}
      {error && <Alert color="red">{error}</Alert>}
      <CollectionAttachmentPicker
        collectionId={collectionId}
        disabled={busy}
        onBusyChange={setBusy}
      />
    </Stack>
  );
}

export function CollectionAttachmentDialog({
  collectionId,
  opened,
  onClose,
}: {
  collectionId: string;
  opened: boolean;
  onClose: () => void;
}) {
  const [busy, setBusy] = useState(false);
  return (
    <Modal
      opened={opened}
      onClose={() => {
        if (!busy) onClose();
      }}
      title="Add collection to libraries"
      centered
    >
      <CollectionAttachmentPicker
        collectionId={collectionId}
        onBusyChange={setBusy}
        onAttached={(message) => {
          notifications.show({
            title: 'Collection added',
            message,
            color: 'teal',
          });
          onClose();
        }}
      />
    </Modal>
  );
}

function CollectionAttachmentPicker({
  collectionId,
  disabled = false,
  onBusyChange,
  onAttached,
}: {
  collectionId: string;
  disabled?: boolean;
  onBusyChange: (busy: boolean) => void;
  onAttached?: (message: string) => void;
}) {
  const libraries = useLibraries();
  const placements = useCollectionPlacements(collectionId);
  const attach = useAttachCollectionToLibraries(collectionId);
  const [selected, setSelected] = useState<string[]>([]);
  const [search, setSearch] = useState('');
  const [failures, setFailures] = useState<
    {
      libraryId: string;
      message: string;
    }[]
  >([]);
  const [message, setMessage] = useState('');
  const options = (libraries.data ?? []).filter(
    (library) => !placements.data?.some((placement) => placement.libraryId === library.id),
  );
  const availableIds = selected.filter((id) => options.some((option) => option.id === id));
  const matching = options.filter((library) =>
    library.name.toLocaleLowerCase().includes(search.trim().toLocaleLowerCase()),
  );

  if (libraries.isError || placements.isError) {
    return (
      <Alert
        color="red"
        title="Could not load libraries"
      >
        <Button
          variant="subtle"
          onClick={() => {
            void libraries.refetch();
            void placements.refetch();
          }}
        >
          Try again
        </Button>
      </Alert>
    );
  }

  return (
    <Stack gap="sm">
      {libraries.isPending || placements.isPending ? (
        <Text
          size="sm"
          c="dimmed"
          role="status"
        >
          Loading libraries…
        </Text>
      ) : options.length === 0 ? (
        <Text
          size="sm"
          c="dimmed"
        >
          {libraries.data.length
            ? 'This collection is in every library.'
            : 'Create a library before adding this collection.'}
        </Text>
      ) : (
        <>
          {onAttached && (
            <Text
              size="sm"
              c="dimmed"
            >
              Each library’s rules determine which games its audience sees.
            </Text>
          )}
          <TextInput
            label="Find libraries"
            placeholder="Search by name"
            data-autofocus={onAttached ? true : undefined}
            disabled={disabled || attach.isPending}
            leftSection={<IconSearch size={16} />}
            value={search}
            onChange={(event) => setSearch(event.currentTarget.value)}
          />
          {matching.length ? (
            <ScrollArea.Autosize
              mah={224}
              type="auto"
            >
              <Stack
                gap="sm"
                p="xs"
                role="group"
                aria-label="Libraries to add"
              >
                {matching.map((library) => (
                  <Checkbox
                    key={library.id}
                    label={library.name}
                    description={!library.lastMaterializedAt ? 'Draft' : undefined}
                    disabled={disabled || attach.isPending}
                    checked={availableIds.includes(library.id)}
                    onChange={(event) => {
                      const checked = event.currentTarget.checked;
                      setSelected((current) =>
                        checked
                          ? [
                              ...current,
                              library.id,
                            ]
                          : current.filter((id) => id !== library.id),
                      );
                    }}
                  />
                ))}
              </Stack>
            </ScrollArea.Autosize>
          ) : (
            <Text
              size="sm"
              c="dimmed"
            >
              No libraries match this search.
            </Text>
          )}
          <Group justify="flex-end">
            <Button
              {...workspaceActionProps}
              disabled={disabled || availableIds.length === 0}
              loading={attach.isPending}
              onClick={async () => {
                setFailures([]);
                setMessage('');
                onBusyChange(true);
                try {
                  const failed = await attach.mutateAsync(availableIds);
                  const count = availableIds.length - failed.length;
                  setSelected(failed.map(({ libraryId }) => libraryId));
                  setFailures(failed);
                  const confirmation = count
                    ? `Added to ${count} ${count === 1 ? 'library' : 'libraries'}.`
                    : '';
                  setMessage(confirmation);
                  if (!failed.length) onAttached?.(confirmation);
                } finally {
                  onBusyChange(false);
                }
              }}
            >
              {availableIds.length
                ? `Add to ${availableIds.length} ${availableIds.length === 1 ? 'library' : 'libraries'}`
                : 'Add to libraries'}
            </Button>
          </Group>
        </>
      )}
      {message && (
        <Text
          size="sm"
          role="status"
        >
          {message}
        </Text>
      )}
      {failures.length > 0 && (
        <Alert
          color="red"
          title="Some libraries could not be updated"
        >
          {failures.map(({ libraryId, message }) => (
            <Text
              key={libraryId}
              size="sm"
            >
              {libraries.data?.find(({ id }) => id === libraryId)?.name ?? libraryId} · {message}
            </Text>
          ))}
        </Alert>
      )}
    </Stack>
  );
}

function PlacementCount({ libraryId, collectionId }: { libraryId: string; collectionId: string }) {
  const library = useLibrary(libraryId);
  const attachments = useLibraryAttachments(libraryId);
  const row = attachments.data?.find((a) => a.collectionId === collectionId);
  return (
    <Text
      size="sm"
      c="dimmed"
    >
      {library.isPending || attachments.isPending
        ? 'Loading count…'
        : library.isError || attachments.isError
          ? 'Count unavailable'
          : library.data && awaitsFirstPublication(library.data)
            ? 'Not published yet'
            : library.data?.configurationState !== 'Valid'
              ? 'Review library rules'
              : row
                ? `${row.visibleCount} of ${row.totalCount} games visible`
                : 'Loading count…'}
    </Text>
  );
}

function DetachAudience({
  libraryId,
  name,
  collectionId,
  busy,
  setBusy,
  setError,
}: {
  libraryId: string;
  name: string;
  collectionId: string;
  busy: boolean;
  setBusy: (busy: boolean) => void;
  setError: (error: string | null) => void;
}) {
  const save = useSetLibraryAttachments(libraryId);
  return (
    <ActionIcon
      variant="subtle"
      color="gray"
      aria-label={`Detach ${name}`}
      title={`Detach ${name}`}
      disabled={busy}
      onClick={async () => {
        setBusy(true);
        setError(null);
        try {
          const current = await listLibraryAttachments({
            path: {
              libraryId,
            },
          });
          if (current.error || !current.data)
            throw new Error('Could not read the library’s current collections.');
          await save.mutateAsync(
            current.data
              .filter((a) => a.collectionId !== collectionId)
              .map((a) => ({
                collectionId: a.collectionId,
                isFeatured: a.isFeatured,
              })),
          );
        } catch (e) {
          setError(e instanceof Error ? e.message : 'Could not detach collection.');
        } finally {
          setBusy(false);
        }
      }}
    >
      <IconUnlink size={17} />
    </ActionIcon>
  );
}
