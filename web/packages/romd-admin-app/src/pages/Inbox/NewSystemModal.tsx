import { Anchor, Button, Group, Modal, Select, Stack, Text, TextInput } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { listAdminCompanies, type UnroutedDat } from '@romd/admin-api-client';
import { useQuery } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router';
import { useAssignDatPlatform } from '../../hooks/api/useDats';
import { useAddPlatformAlias, useCreatePlatform } from '../../hooks/api/usePlatformManagement';

export interface NewSystemModalProps {
  /** The unrouted DAT that prompted system creation; null = closed */
  target: UnroutedDat | null;
  /** Whether to save the DAT header name as an alias after assignment */
  rememberAlias: boolean;
  onClose: () => void;
}

/** Derives a URL-safe short name from a display name. */
export function deriveShortName(name: string): string {
  return `local-${name
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '').slice(0, 44).replace(/-+$/g, '')}`;
}

/**
 * Creates a system and routes the prompting DAT to it in one step.
 * The DAT header name seeds the system name and short name.
 */
export function NewSystemModal({ target, rememberAlias, onClose }: NewSystemModalProps) {
  const navigate = useNavigate();
  const [name, setName] = useState('');
  const [shortName, setShortName] = useState('');
  const [shortNameEdited, setShortNameEdited] = useState(false);
  const [manufacturer, setManufacturer] = useState<string | null>(null);
  const companies = useQuery({
    queryKey: ['reference-companies'],
    enabled: target !== null,
    queryFn: async () => (await listAdminCompanies({ throwOnError: true })).data,
  });

  const createMutation = useCreatePlatform();
  const assignMutation = useAssignDatPlatform();
  const addAliasMutation = useAddPlatformAlias();

  const isWorking = createMutation.isPending || assignMutation.isPending || addAliasMutation.isPending;

  // Seed the form from the DAT header each time the modal opens
  useEffect(() => {
    if (target) {
      setName(target.dat.name);
      setShortName(deriveShortName(target.dat.name));
      setShortNameEdited(false);
      setManufacturer(null);
    }
  }, [target]);

  const handleNameChange = (value: string) => {
    setName(value);
    if (!shortNameEdited) {
      setShortName(deriveShortName(value));
    }
  };

  const handleCreate = async () => {
    if (!target || !name.trim() || !shortName.trim()) return;

    try {
      const platform = await createMutation.mutateAsync({
        name: name.trim(),
        shortName: shortName.trim(),
        manufacturerKey: manufacturer ?? undefined,
      });

      await assignMutation.mutateAsync({ datId: target.dat.id, systemKey: platform.key });

      // The new system's name may differ from the DAT header — remember the
      // header as an alias so the next DAT like this routes itself
      if (rememberAlias && platform.name !== target.dat.name) {
        try {
          await addAliasMutation.mutateAsync({
            systemKey: platform.key,
            type: 'name',
            value: target.dat.name,
          });
        } catch (error) {
          notifications.show({
            title: 'Alias not saved',
            message: error instanceof Error ? error.message : 'Could not save the alias.',
            color: 'yellow',
          });
        }
      }

      const toastId = `created-${platform.key}`;
      notifications.show({
        id: toastId,
        title: `Created ${platform.name}`,
        message: (
          <Stack gap={4}>
            <Text size="sm">{target.dat.name} routed to the new system.</Text>
            <Anchor
              component="button"
              type="button"
              size="sm"
              onClick={() => {
                notifications.hide(toastId);
                navigate(`/systems/${platform.key}`);
              }}
            >
              View {platform.name} →
            </Anchor>
          </Stack>
        ),
        color: 'green',
      });
      onClose();
    } catch (error) {
      notifications.show({
        title: 'Could not create system',
        message: error instanceof Error ? error.message : 'Please try again.',
        color: 'red',
      });
    }
  };

  return (
    <Modal
      opened={target !== null}
      onClose={onClose}
      title="New System"
      centered
      closeOnClickOutside={!isWorking}
      closeOnEscape={!isWorking}
    >
      <Stack gap="md">
        <Text size="sm" c="dimmed">
          Creates a system and routes <Text component="span" fw={600}>{target?.dat.name}</Text> to it.
        </Text>
        <TextInput
          label="Name"
          value={name}
          onChange={(e) => handleNameChange(e.currentTarget.value)}
          placeholder="Super Nintendo Entertainment System"
          required
          data-autofocus
        />
        <TextInput
          label="System key"
          description="Permanent identifier using lowercase letters, numbers, and hyphens"
          value={shortName}
          onChange={(e) => {
            setShortName(e.currentTarget.value);
            setShortNameEdited(true);
          }}
          placeholder="local-my-system"
          required
        />
        <Select
          label="Manufacturer"
          value={manufacturer}
          onChange={setManufacturer}
          data={(companies.data ?? []).map((company) => ({ value: company.key, label: company.name }))}
          placeholder="Select a company (optional)"
          searchable
          clearable
        />
        <Group justify="flex-end" gap="sm">
          <Button variant="subtle" onClick={onClose} disabled={isWorking}>
            Cancel
          </Button>
          <Button onClick={handleCreate} loading={isWorking} disabled={!name.trim() || !/^local-[a-z0-9]+(?:[-_][a-z0-9]+)*$/.test(shortName) || shortName.length > 50}>
            Create & Assign
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
