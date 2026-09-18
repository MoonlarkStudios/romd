import {
  Accordion,
  Alert,
  Button,
  Group,
  Image,
  Modal,
  Select,
  Stack,
  Text,
  Textarea,
  TextInput,
} from '@mantine/core';
import { notifications } from '@mantine/notifications';
import type { CollectionItemDto, CollectionSummary } from '@romd/admin-api-client';
import { IconAlertCircle } from '@tabler/icons-react';
import { useEffect, useMemo, useState } from 'react';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import {
  ApiClientError,
  useCreateCollection,
  useUpdateCollection,
} from '../../hooks/api/useCollectionManagement';
import { usePlatforms } from '../../hooks/api/usePlatforms';

interface CollectionFormModalProps {
  opened: boolean;
  onClose: () => void;
  /** When set, the modal edits this collection; otherwise it creates a new one. */
  collection: CollectionSummary | null;
  onSaved?: (collectionId: string) => void;
  coverChoices?: CollectionItemDto[];
  affectedAudiences?: string;
}

export function CollectionFormModal({
  opened,
  onClose,
  collection,
  onSaved,
  coverChoices = [],
  affectedAudiences,
}: CollectionFormModalProps) {
  const isEdit = collection !== null;
  const { data: platforms } = usePlatforms();
  const createCollection = useCreateCollection();
  const updateCollection = useUpdateCollection();

  const [name, setName] = useState('');
  const [coverMediaId, setCoverMediaId] = useState<string | null>(null);
  const [description, setDescription] = useState('');
  const [systemKey, setPlatformId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  // Reset the form whenever the modal opens for a different target.
  useEffect(() => {
    if (opened) {
      setName(collection?.name ?? '');
      setCoverMediaId(collection?.coverUrl?.match(/^\/media\/([^/?#]+)$/)?.[1] ?? null);
      setDescription(collection?.description ?? '');
      setPlatformId(collection?.systemKey ?? null);
      setError(null);
    }
  }, [
    opened,
    collection,
  ]);

  const platformOptions = useMemo(
    () =>
      (platforms ?? []).map((p) => ({
        value: p.key,
        label: p.name,
      })),
    [
      platforms,
    ],
  );

  const coverOptions = [
    ...new Map(
      coverChoices.flatMap((item) => {
        const id = item.coverUrl?.match(/^\/media\/([^/?#]+)$/)?.[1];
        return id
          ? [
              [
                id,
                {
                  value: id,
                  label: item.titleName,
                },
              ] as const,
            ]
          : [];
      }),
    ).values(),
  ];
  const existingCover = collection?.coverUrl?.match(/^\/media\/([^/?#]+)$/)?.[1];
  if (existingCover && !coverOptions.some((option) => option.value === existingCover))
    coverOptions.unshift({
      value: existingCover,
      label: 'Current artwork',
    });
  const isPending = createCollection.isPending || updateCollection.isPending;
  const trimmedName = name.trim();

  const handleSubmit = async () => {
    if (!trimmedName) {
      setError('Name is required.');
      return;
    }
    setError(null);

    const request = {
      name: trimmedName,
      description: description.trim() || null,
      systemKey,
      ...(isEdit
        ? {
            coverMediaId,
          }
        : {}),
    };

    try {
      if (isEdit && collection) {
        const updated = await updateCollection.mutateAsync({
          collectionId: collection.id,
          request,
        });
        notifications.show({
          message: 'Collection updated.',
          color: 'green',
        });
        onSaved?.(updated.id);
      } else {
        const created = await createCollection.mutateAsync(request);
        notifications.show({
          message: 'Collection created.',
          color: 'green',
        });
        onSaved?.(created.id);
      }
      onClose();
    } catch (err) {
      setError(
        err instanceof ApiClientError ? err.message : 'Something went wrong. Please try again.',
      );
    }
  };

  return (
    <Modal
      opened={opened}
      onClose={() => {
        if (!isPending) onClose();
      }}
      title={isEdit ? 'Collection details & artwork' : 'New collection'}
      centered
    >
      <Stack gap="md">
        {error && (
          <Alert
            icon={<IconAlertCircle size={16} />}
            color="red"
            variant="light"
          >
            {error}
          </Alert>
        )}

        {isEdit && (
          <Text
            size="sm"
            c="dimmed"
          >
            {affectedAudiences
              ? `Changes appear in ${affectedAudiences}.`
              : 'Changes apply everywhere this shared collection is attached.'}
          </Text>
        )}
        <TextInput
          label="Name"
          placeholder="e.g. Essential RPGs"
          required
          value={name}
          onChange={(e) => setName(e.currentTarget.value)}
          data-autofocus
        />

        <Accordion defaultValue={isEdit ? 'details' : null}>
        <Accordion.Item value="details">
        <Accordion.Control>Optional details</Accordion.Control>
        <Accordion.Panel><Stack gap="md">
        <Textarea
          label="Description"
          placeholder="Optional — what ties these titles together?"
          autosize
          minRows={2}
          maxRows={5}
          value={description}
          onChange={(e) => setDescription(e.currentTarget.value)}
        />

        <Select
          label="Platform"
          description="Optional — scope this collection to a single system."
          placeholder="All platforms"
          data={platformOptions}
          value={systemKey}
          onChange={setPlatformId}
          clearable
          searchable
        />
        </Stack></Accordion.Panel>
        </Accordion.Item>
        </Accordion>

        {isEdit && (
          <>
            <Select
              label="Collection artwork"
              description="Use cover art from a game in this collection, or clear it for no artwork."
              placeholder="No artwork"
              clearable
              searchable
              value={coverMediaId}
              onChange={setCoverMediaId}
              data={coverOptions}
            />
            {coverMediaId && (
              <Image
                src={`/media/${coverMediaId}`}
                alt="Selected collection artwork"
                w={90}
                h={120}
                fit="cover"
                radius="md"
              />
            )}
          </>
        )}
        <Group
          justify="flex-end"
          gap="xs"
          mt="xs"
        >
          <Button
            variant="subtle"
            onClick={onClose}
            disabled={isPending}
          >
            Cancel
          </Button>
          <Button
            onClick={handleSubmit}
            loading={isPending}
            disabled={!trimmedName}
            {...workspaceActionProps}
          >
            {isEdit ? 'Save changes' : 'Create collection'}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
