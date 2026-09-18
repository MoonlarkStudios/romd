import {
  ActionIcon,
  Alert,
  Badge,
  Box,
  Button,
  Group,
  Modal,
  Paper,
  Pill,
  Popover,
  Select,
  Skeleton,
  Stack,
  Text,
  TextInput,
} from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { getTaxonomyMergeImpact } from '@romd/admin-api-client';
import { IconAlertCircle, IconArrowMerge, IconPlus, IconSearch } from '@tabler/icons-react';
import { useQuery } from '@tanstack/react-query';
import { useEffect, useMemo, useState } from 'react';
import { EmptyState } from '../../components/EmptyState';
import { ApiClientError } from '../../hooks/api/useTaxonomy';

export interface TaxonomyEntry {
  id: string;
  name: string;
  /** Language code (regions have none). */
  secondary?: string | null;
  isAutoCreated: boolean;
  canMerge: boolean;
  sortOrder: number;
  aliases: Array<{ id: string; alias: string; ownership: string }>;
}

interface TaxonomyPanelProps {
  noun: 'region' | 'language';
  entries: TaxonomyEntry[];
  isLoading: boolean;
  isError: boolean;
  onRetry?: () => void;
  canEdit: boolean;
  busy: boolean;
  onAddAlias: (entryId: string, alias: string) => Promise<unknown>;
  onRemoveAlias: (entryId: string, aliasId: string) => Promise<unknown>;
  onMerge: (sourceId: string, targetId: string) => Promise<unknown>;
}

export function TaxonomyPanel({
  noun,
  entries,
  isLoading,
  isError,
  onRetry,
  canEdit,
  busy,
  onAddAlias,
  onRemoveAlias,
  onMerge,
}: TaxonomyPanelProps) {
  const [search, setSearch] = useState('');
  const [mergeSource, setMergeSource] = useState<TaxonomyEntry | null>(null);

  const sorted = useMemo(
    () =>
      [...entries].sort(
        (a, b) => a.sortOrder - b.sortOrder || a.name.localeCompare(b.name),
      ),
    [entries],
  );

  const filtered = useMemo(() => {
    const query = search.trim().toLowerCase();
    if (!query) return sorted;
    return sorted.filter(
      (e) =>
        e.name.toLowerCase().includes(query) ||
        e.aliases.some((a) => a.alias.toLowerCase().includes(query)),
    );
  }, [sorted, search]);

  if (isError && !entries.length) {
    return (
      <Alert icon={<IconAlertCircle size={16} />} color="red" title={`Failed to load ${noun}s`}>
        Please try again.<Button variant="subtle" onClick={onRetry}>Retry</Button>
      </Alert>
    );
  }

  if (isLoading) {
    return (
      <Stack gap="xs">
        {Array.from({ length: 6 }).map((_, i) => (
          // biome-ignore lint/suspicious/noArrayIndexKey: static skeleton placeholders
          <Skeleton key={i} height={64} radius="sm" />
        ))}
      </Stack>
    );
  }

  return (
    <Stack gap="sm">
      <Text size="sm" c="dimmed">Local aliases are retained across imports. ROMD supplies its aliases on each import; matching aliases may reappear after removal. Ownership unknown means the alias predates ownership tracking.</Text>
      {isError && <Alert color="yellow">Refresh failed. Showing the last loaded values.<Button variant="subtle" onClick={onRetry}>Retry</Button></Alert>}
      <TextInput
        placeholder={`Filter ${noun}s or aliases...`}
        leftSection={<IconSearch size={16} />}
        value={search}
        onChange={(e) => setSearch(e.currentTarget.value)}
      />

      {filtered.length === 0 ? (
        <EmptyState
          size="sm"
          title={search ? 'No matches' : `No ${noun}s`}
          description={search ? 'Try a different search.' : `No ${noun}s have been recorded yet.`}
        />
      ) : (
        <Stack gap="xs">
          {filtered.map((entry) => (
            <TaxonomyRow
              key={entry.id}
              entry={entry}
              canEdit={canEdit}
              busy={busy}
              onAddAlias={onAddAlias}
              onRemoveAlias={onRemoveAlias}
              onMerge={() => setMergeSource(entry)}
            />
          ))}
        </Stack>
      )}

      <MergeModal
        noun={noun}
        source={mergeSource}
        candidates={sorted}
        busy={busy}
        onClose={() => setMergeSource(null)}
        onConfirm={onMerge}
      />
    </Stack>
  );
}

interface TaxonomyRowProps {
  entry: TaxonomyEntry;
  canEdit: boolean;
  busy: boolean;
  onAddAlias: (entryId: string, alias: string) => Promise<unknown>;
  onRemoveAlias: (entryId: string, aliasId: string) => Promise<unknown>;
  onMerge: () => void;
}

function TaxonomyRow({ entry, canEdit, busy, onAddAlias, onRemoveAlias, onMerge }: TaxonomyRowProps) {
  const [aliasOpen, setAliasOpen] = useState(false);
  const [aliasDraft, setAliasDraft] = useState('');
  const [saving, setSaving] = useState(false);

  const handleAddAlias = async () => {
    const value = aliasDraft.trim();
    if (!value) return;
    setSaving(true);
    try {
      await onAddAlias(entry.id, value);
      setAliasDraft('');
      setAliasOpen(false);
    } catch (err) {
      const status = err instanceof ApiClientError ? err.status : undefined;
      notifications.show({
        color: 'red',
        message: status === 409 ? 'That alias already exists.' : 'Failed to add the alias.',
      });
    } finally {
      setSaving(false);
    }
  };

  const handleRemoveAlias = async (aliasId: string) => {
    try {
      await onRemoveAlias(entry.id, aliasId);
    } catch {
      notifications.show({ color: 'red', message: 'Failed to remove the alias.' });
    }
  };

  return (
    <Paper withBorder p="sm">
      <Group justify="space-between" align="flex-start" wrap="nowrap" gap="sm">
        <Box style={{ minWidth: 0, flex: 1 }}>
          <Group gap="xs" mb={6}>
            <Text size="sm" fw={600}>
              {entry.name}
            </Text>
            {entry.secondary && (
              <Badge size="xs" variant="default" style={{ fontVariantNumeric: 'tabular-nums' }}>
                {entry.secondary}
              </Badge>
            )}
            {entry.isAutoCreated && (
              <Badge size="xs" variant="light" color="yellow">
                Auto
              </Badge>
            )}
          </Group>

          {!entry.canMerge && <Text size="xs" c="dimmed" mb="xs">Registered reference identity · cannot be merged.</Text>}
          {entry.aliases.length === 0 ? (
            <Text size="xs" c="dimmed">
              No aliases
            </Text>
          ) : (
            <Group gap={6}>
              {entry.aliases.map((alias) => (
                <Pill
                  key={alias.id}
                  withRemoveButton={canEdit && !busy && alias.ownership !== 'Romd'}
                  removeButtonProps={{ 'aria-hidden': false, tabIndex: 0, 'aria-label': `Remove alias ${alias.alias} from ${entry.name}` }}
                  onRemove={() => handleRemoveAlias(alias.id)}
                >
                  {alias.alias}{alias.ownership === 'Romd' ? ' · ROMD' : alias.ownership === 'Unresolved' ? ' · Ownership unknown' : ' · Local'}
                </Pill>
              ))}
            </Group>
          )}
        </Box>

        {canEdit && (
          <Group gap="xs" wrap="nowrap">
            <Popover
              opened={aliasOpen}
              onChange={setAliasOpen}
              width={260}
              position="bottom-end"
              withArrow
            >
              <Popover.Target>
                <Button
                  variant="light"
                  size="xs"
                  leftSection={<IconPlus size={14} />}
                  aria-label={`Add alias to ${entry.name}`}
                  onClick={() => setAliasOpen(true)}
                >
                  Alias
                </Button>
              </Popover.Target>
              <Popover.Dropdown>
                <Stack gap="xs">
                  <TextInput
                    label="New alias"
                    placeholder="e.g. USA"
                    value={aliasDraft}
                    onChange={(e) => setAliasDraft(e.currentTarget.value)}
                    onKeyDown={(e) => {
                      if (e.key === 'Enter') handleAddAlias();
                    }}
                    data-autofocus
                  />
                  <Group gap="xs" justify="flex-end">
                    <Button variant="subtle" size="xs" onClick={() => setAliasOpen(false)}>
                      Cancel
                    </Button>
                    <Button size="xs" loading={saving} onClick={handleAddAlias}>
                      Add
                    </Button>
                  </Group>
                </Stack>
              </Popover.Dropdown>
            </Popover>

            {entry.canMerge && <ActionIcon variant="subtle" color="gray" size={30} onClick={onMerge} title={`Merge ${entry.name} into…`} aria-label={`Merge ${entry.name} into another value`}>
              <IconArrowMerge size={16} />
            </ActionIcon>}
          </Group>
        )}
      </Group>
    </Paper>
  );
}

interface MergeModalProps {
  noun: 'region' | 'language';
  source: TaxonomyEntry | null;
  candidates: TaxonomyEntry[];
  busy: boolean;
  onClose: () => void;
  onConfirm: (sourceId: string, targetId: string) => Promise<unknown>;
}

function MergeModal({ noun, source, candidates, busy, onClose, onConfirm }: MergeModalProps) {
  const [targetId, setTargetId] = useState<string | null>(null);
  const impact = useQuery({ queryKey: ['taxonomy-merge-impact', noun, source?.id, targetId], enabled: !!source && !!targetId, queryFn: async () => {
    const response = await getTaxonomyMergeImpact({ path: { kind: noun === 'region' ? 'regions' : 'languages', sourceId: source!.id, targetId: targetId! } });
    if (response.error || !response.data) throw new Error('Could not load merge impact.');
    return response.data;
  } });

  // Reset the chosen target whenever the modal opens for a different source.
  useEffect(() => {
    setTargetId(null);
  }, [source]);

  const targetOptions = useMemo(
    () =>
      candidates
        .filter((c) => c.id !== source?.id)
        .map((c) => ({ value: c.id, label: c.name })),
    [candidates, source],
  );

  const targetName = candidates.find((c) => c.id === targetId)?.name;

  const handleConfirm = async () => {
    if (!source || !targetId) return;
    try {
      await onConfirm(source.id, targetId);
      notifications.show({ color: 'green', message: `Merged into ${targetName}.` });
      setTargetId(null);
      onClose();
    } catch (error) {
      notifications.show({ color: 'red', message: error instanceof ApiClientError ? error.message : `Failed to merge ${noun}s.` });
    }
  };

  return (
    <Modal
      opened={source !== null}
      onClose={onClose}
      title={`Merge ${noun}`}
      centered
    >
      <Stack gap="md">
        <Select
          label={`Merge "${source?.name}" into`}
          placeholder={`Pick a target ${noun}`}
          data={targetOptions}
          value={targetId}
          onChange={setTargetId}
          searchable
          data-autofocus
        />
        <Alert icon={<IconAlertCircle size={16} />} color="orange" variant="light">
          All aliases and game associations move to{' '}
          <strong>{targetName ?? 'the target'}</strong>, and <strong>{source?.name}</strong> is
          deleted. This can't be undone.
        </Alert>
        <Group gap="xs" justify="flex-end">
          <Button variant="subtle" onClick={onClose}>
            Cancel
          </Button>
          <Button color="red" disabled={!targetId || !impact.isSuccess} loading={busy} onClick={handleConfirm}>
            Merge
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
