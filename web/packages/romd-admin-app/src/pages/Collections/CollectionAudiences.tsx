import { ActionIcon, Alert, Badge, Button, Group, Select, Stack, Text } from '@mantine/core';
import { getCollectionLibraryPlacements, getLibraryAttachments } from '@romd/admin-api-client';
import { IconUnlink } from '@tabler/icons-react';
import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { Link } from 'react-router';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import {
  useLibraryAttachments,
  useSetLibraryAttachments,
} from '../../hooks/api/useLibraryExperience';
import {
  libraryManagementKeys,
  useLibraries,
  useLibrary,
} from '../../hooks/api/useLibraryManagement';

export function useCollectionPlacements(collectionId: string, enabled = true) {
  return useQuery({
    enabled,
    queryKey: [
      ...libraryManagementKeys.all,
      'collection-placements',
      collectionId,
    ],
    queryFn: async () => {
      const response = await getCollectionLibraryPlacements({
        path: {
          collectionId,
        },
      });
      if (response.error || !response.data) throw new Error('Could not load attached audiences.');
      return response.data;
    },
  });
}

export function CollectionAudiences({ collectionId }: { collectionId: string }) {
  const libraries = useLibraries();
  const placements = useCollectionPlacements(collectionId);
  const [selected, setSelected] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const save = useSetLibraryAttachments(selected ?? '');
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
      <Select
        label="Attach to another library"
        placeholder="Choose an audience"
        disabled={busy}
        value={selected}
        onChange={setSelected}
        data={(libraries.data ?? [])
          .filter((l) => !placements.data?.some((p) => p.libraryId === l.id))
          .map((l) => ({
            value: l.id,
            label: l.name,
          }))}
      />
      {error && <Alert color="red">{error}</Alert>}
      <Button
        variant="light"
        {...workspaceActionProps}
        disabled={busy || !selected || !placements.isSuccess}
        loading={busy}
        onClick={async () => {
          if (!selected) return;
          setBusy(true);
          setError(null);
          try {
            const current = await getLibraryAttachments({
              path: {
                libraryId: selected,
              },
            });
            if (current.error || !current.data)
              throw new Error('Could not read the library’s current collections.');
            await save.mutateAsync([
              ...current.data
                .filter((a) => a.collectionId !== collectionId)
                .map((a) => ({
                  collectionId: a.collectionId,
                  isFeatured: a.isFeatured,
                })),
              {
                collectionId,
                isFeatured: true,
              },
            ]);
            setSelected(null);
          } catch (e) {
            setError(e instanceof Error ? e.message : 'Could not attach collection.');
          } finally {
            setBusy(false);
          }
        }}
      >
        Attach collection
      </Button>
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
          : library.data?.needsMaterialization
            ? 'Updating games…'
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
          const current = await getLibraryAttachments({
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
