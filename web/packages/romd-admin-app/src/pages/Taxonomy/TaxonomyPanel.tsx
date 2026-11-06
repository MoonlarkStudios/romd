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
import { IconAlertCircle, IconArrowMerge, IconPlus, IconSearch } from '@tabler/icons-react';
import { useInfiniteQuery, useQuery } from '@tanstack/react-query';
import { useEffect, useMemo, useRef, useState } from 'react';
import { EmptyState } from '../../components/EmptyState';
import { ApiClientError } from '../../hooks/api/useTaxonomy';

export interface TaxonomyAlias {
  id: string;
  alias: string;
  ownership: string;
}

/** A page of a row's aliases in id order; a row embeds its first page. */
export interface TaxonomyAliasPage {
  items: TaxonomyAlias[];
  nextCursor?: string | null;
  hasNextPage: boolean;
}

export interface TaxonomyEntry {
  /** The natural key: a built-in key, or a dat- key minted for a row a DAT token created. */
  key: string;
  name: string;
  /** Language code (regions have none). */
  secondary?: string | null;
  isAutoCreated: boolean;
  canMerge: boolean;
  sortOrder: number;
  aliases: TaxonomyAliasPage;
}

interface TaxonomyPanelProps {
  noun: 'region' | 'language';
  /** The rows the search matches, or every row without one. */
  entries: TaxonomyEntry[];
  /** Every row, whatever the search: the merge target choices. */
  candidates: TaxonomyEntry[];
  search: string;
  onSearchChange: (search: string) => void;
  isLoading: boolean;
  isError: boolean;
  errorMessage?: string;
  onRetry?: () => void;
  canEdit: boolean;
  busy: boolean;
  /** The query key under which a row's further alias pages are cached. */
  aliasesKey: (entryKey: string) => readonly unknown[];
  onLoadAliases: (entryKey: string, cursor: string) => Promise<TaxonomyAliasPage>;
  onAddAlias: (entryKey: string, alias: string) => Promise<unknown>;
  onRemoveAlias: (entryKey: string, aliasId: string) => Promise<unknown>;
  onMerge: (sourceKey: string, targetKey: string) => Promise<unknown>;
  onPreviewMerge: (sourceKey: string, targetKey: string) => Promise<unknown>;
}

const bySortOrderAndName = (a: TaxonomyEntry, b: TaxonomyEntry) =>
  a.sortOrder - b.sortOrder || a.name.localeCompare(b.name);

export function TaxonomyPanel({
  noun,
  entries,
  candidates,
  search,
  onSearchChange,
  isLoading,
  isError,
  errorMessage,
  onRetry,
  canEdit,
  busy,
  aliasesKey,
  onLoadAliases,
  onAddAlias,
  onRemoveAlias,
  onMerge,
  onPreviewMerge,
}: TaxonomyPanelProps) {
  const [mergeSource, setMergeSource] = useState<TaxonomyEntry | null>(null);

  const sorted = useMemo(
    () =>
      [
        ...entries,
      ].sort(bySortOrderAndName),
    [
      entries,
    ],
  );

  const sortedCandidates = useMemo(
    () =>
      [
        ...candidates,
      ].sort(bySortOrderAndName),
    [
      candidates,
    ],
  );

  const content =
    isError && !entries.length ? (
      <Alert
        icon={<IconAlertCircle size={16} />}
        color="red"
        title={`Failed to load ${noun}s`}
      >
        {errorMessage ?? 'Please try again.'}
        <Button
          variant="subtle"
          onClick={onRetry}
        >
          Retry
        </Button>
      </Alert>
    ) : isLoading ? (
      <Stack gap="xs">
        {[
          'first',
          'second',
          'third',
          'fourth',
          'fifth',
          'sixth',
        ].map((placeholder) => (
          <Skeleton
            key={placeholder}
            height={64}
            radius="sm"
          />
        ))}
      </Stack>
    ) : sorted.length === 0 ? (
      <EmptyState
        size="sm"
        title={search ? 'No matches' : `No ${noun}s`}
        description={search ? 'Try a different search.' : `No ${noun}s have been recorded yet.`}
      />
    ) : (
      <Stack gap="xs">
        {sorted.map((entry) => (
          <TaxonomyRow
            key={entry.key}
            entry={entry}
            canEdit={canEdit}
            busy={busy}
            aliasesKey={aliasesKey}
            onLoadAliases={onLoadAliases}
            onAddAlias={onAddAlias}
            onRemoveAlias={onRemoveAlias}
            onMerge={() => setMergeSource(entry)}
          />
        ))}
      </Stack>
    );

  return (
    <Stack gap="sm">
      <Text
        size="sm"
        c="dimmed"
      >
        Local aliases are retained across imports. ROMD supplies its aliases on each import;
        matching aliases may reappear after removal. Ownership unknown means the alias predates
        ownership tracking.
      </Text>
      {isError && entries.length > 0 && (
        <Alert color="yellow">
          Refresh failed. Showing the last loaded values.
          <Button
            variant="subtle"
            onClick={onRetry}
          >
            Retry
          </Button>
        </Alert>
      )}
      <TextInput
        placeholder={`Filter ${noun}s or aliases...`}
        leftSection={<IconSearch size={16} />}
        value={search}
        onChange={(e) => onSearchChange(e.currentTarget.value)}
      />

      {content}

      <MergeModal
        noun={noun}
        source={mergeSource}
        candidates={sortedCandidates}
        busy={busy}
        onClose={() => setMergeSource(null)}
        onConfirm={onMerge}
        onPreview={onPreviewMerge}
      />
    </Stack>
  );
}

interface TaxonomyRowProps {
  entry: TaxonomyEntry;
  canEdit: boolean;
  busy: boolean;
  aliasesKey: (entryKey: string) => readonly unknown[];
  onLoadAliases: (entryKey: string, cursor: string) => Promise<TaxonomyAliasPage>;
  onAddAlias: (entryKey: string, alias: string) => Promise<unknown>;
  onRemoveAlias: (entryKey: string, aliasId: string) => Promise<unknown>;
  onMerge: () => void;
}

function TaxonomyRow({
  entry,
  canEdit,
  busy,
  aliasesKey,
  onLoadAliases,
  onAddAlias,
  onRemoveAlias,
  onMerge,
}: TaxonomyRowProps) {
  const [aliasOpen, setAliasOpen] = useState(false);
  const [aliasDraft, setAliasDraft] = useState('');
  const [saving, setSaving] = useState(false);

  const handleAddAlias = async () => {
    const value = aliasDraft.trim();
    if (!value) return;
    setSaving(true);
    try {
      await onAddAlias(entry.key, value);
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
      await onRemoveAlias(entry.key, aliasId);
    } catch {
      notifications.show({
        color: 'red',
        message: 'Failed to remove the alias.',
      });
    }
  };

  return (
    <Paper
      withBorder
      p="sm"
    >
      <Group
        justify="space-between"
        align="flex-start"
        wrap="nowrap"
        gap="sm"
      >
        <Box
          style={{
            minWidth: 0,
            flex: 1,
          }}
        >
          <Group
            gap="xs"
            mb={6}
          >
            <Text
              size="sm"
              fw={600}
            >
              {entry.name}
            </Text>
            {entry.secondary && (
              <Badge
                size="xs"
                variant="default"
                style={{
                  fontVariantNumeric: 'tabular-nums',
                }}
              >
                {entry.secondary}
              </Badge>
            )}
            {entry.isAutoCreated && (
              <Badge
                size="xs"
                variant="light"
                color="yellow"
              >
                Auto
              </Badge>
            )}
          </Group>

          {!entry.canMerge && (
            <Text
              size="xs"
              c="dimmed"
              mb="xs"
            >
              Registered reference identity · cannot be merged.
            </Text>
          )}
          {entry.aliases.items.length === 0 ? (
            <Text
              size="xs"
              c="dimmed"
            >
              No aliases
            </Text>
          ) : (
            <Group gap={6}>
              {entry.aliases.items.map((alias) => (
                <AliasPill
                  key={alias.id}
                  entryName={entry.name}
                  alias={alias}
                  removable={canEdit && !busy}
                  onRemove={handleRemoveAlias}
                />
              ))}
              {entry.aliases.hasNextPage && entry.aliases.nextCursor && (
                <MoreAliases
                  entryKey={entry.key}
                  entryName={entry.name}
                  after={entry.aliases.nextCursor}
                  removable={canEdit && !busy}
                  aliasesKey={aliasesKey}
                  onLoadAliases={onLoadAliases}
                  onRemove={handleRemoveAlias}
                />
              )}
            </Group>
          )}
        </Box>

        {canEdit && (
          <Group
            gap="xs"
            wrap="nowrap"
          >
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
                  <Group
                    gap="xs"
                    justify="flex-end"
                  >
                    <Button
                      variant="subtle"
                      size="xs"
                      onClick={() => setAliasOpen(false)}
                    >
                      Cancel
                    </Button>
                    <Button
                      size="xs"
                      loading={saving}
                      onClick={handleAddAlias}
                    >
                      Add
                    </Button>
                  </Group>
                </Stack>
              </Popover.Dropdown>
            </Popover>

            {entry.canMerge && (
              <ActionIcon
                variant="subtle"
                color="gray"
                size={30}
                onClick={onMerge}
                title={`Merge ${entry.name} into…`}
                aria-label={`Merge ${entry.name} into another value`}
              >
                <IconArrowMerge size={16} />
              </ActionIcon>
            )}
          </Group>
        )}
      </Group>
    </Paper>
  );
}

interface AliasPillProps {
  entryName: string;
  alias: TaxonomyAlias;
  /** Whether the viewer may remove aliases now; ROMD's own aliases are never removable. */
  removable: boolean;
  onRemove: (aliasId: string) => void;
}

function AliasPill({ entryName, alias, removable, onRemove }: AliasPillProps) {
  return (
    <Pill
      withRemoveButton={removable && alias.ownership !== 'Romd'}
      removeButtonProps={{
        'aria-hidden': false,
        tabIndex: 0,
        'aria-label': `Remove alias ${alias.alias} from ${entryName}`,
      }}
      onRemove={() => onRemove(alias.id)}
    >
      {alias.alias}
      {alias.ownership === 'Romd'
        ? ' · ROMD'
        : alias.ownership === 'Unresolved'
          ? ' · Ownership unknown'
          : ' · Local'}
    </Pill>
  );
}

interface MoreAliasesProps {
  entryKey: string;
  entryName: string;
  /** The cursor the row's embedded first page ends with. */
  after: string;
  removable: boolean;
  aliasesKey: (entryKey: string) => readonly unknown[];
  onLoadAliases: (entryKey: string, cursor: string) => Promise<TaxonomyAliasPage>;
  onRemove: (aliasId: string) => void;
}

/** The aliases after a row's embedded first page, read a page at a time on request. */
function MoreAliases({
  entryKey,
  entryName,
  after,
  removable,
  aliasesKey,
  onLoadAliases,
  onRemove,
}: MoreAliasesProps) {
  const [requested, setRequested] = useState(false);
  // Keyed by the embedded page's cursor, so a changed first page starts the continuation again.
  const pages = useInfiniteQuery({
    queryKey: [
      ...aliasesKey(entryKey),
      after,
    ],
    queryFn: ({ pageParam }) => onLoadAliases(entryKey, pageParam),
    initialPageParam: after,
    getNextPageParam: (last) => (last.hasNextPage ? (last.nextCursor ?? undefined) : undefined),
    enabled: requested,
  });

  const showMore = () => {
    if (!requested) setRequested(true);
    else if (pages.isError) void pages.refetch();
    else void pages.fetchNextPage();
  };

  return (
    <>
      {pages.data?.pages
        .flatMap((page) => page.items)
        .map((alias) => (
          <AliasPill
            key={alias.id}
            entryName={entryName}
            alias={alias}
            removable={removable}
            onRemove={onRemove}
          />
        ))}
      {pages.isError && (
        <Text
          size="xs"
          c="red"
        >
          Could not load more aliases.
        </Text>
      )}
      {(!requested || pages.hasNextPage || pages.isError) && (
        <Button
          variant="subtle"
          size="compact-xs"
          loading={pages.isFetching}
          aria-label={`Show more aliases of ${entryName}`}
          onClick={showMore}
        >
          Show more
        </Button>
      )}
    </>
  );
}

interface MergeModalProps {
  noun: 'region' | 'language';
  source: TaxonomyEntry | null;
  candidates: TaxonomyEntry[];
  busy: boolean;
  onClose: () => void;
  onConfirm: (sourceKey: string, targetKey: string) => Promise<unknown>;
  onPreview: (sourceKey: string, targetKey: string) => Promise<unknown>;
}

function MergeModal({
  noun,
  source,
  candidates,
  busy,
  onClose,
  onConfirm,
  onPreview,
}: MergeModalProps) {
  const [targetKey, setTargetKey] = useState<string | null>(null);
  const impact = useQuery({
    queryKey: [
      'taxonomy-merge-impact',
      noun,
      source?.key,
      targetKey,
    ],
    enabled: !!source && !!targetKey,
    queryFn: () => {
      if (!source || !targetKey)
        throw new Error('Choose a source and target before loading merge impact.');
      return onPreview(source.key, targetKey);
    },
  });

  // Reset the chosen target whenever the modal opens for a different source.
  const previousResetKey = useRef(source);
  useEffect(() => {
    if (previousResetKey.current === source) return;
    previousResetKey.current = source;
    setTargetKey(null);
  }, [
    source,
  ]);

  const targetOptions = useMemo(
    () =>
      candidates
        .filter((c) => c.key !== source?.key)
        .map((c) => ({
          value: c.key,
          label: c.name,
        })),
    [
      candidates,
      source,
    ],
  );

  const targetName = candidates.find((c) => c.key === targetKey)?.name;

  const handleConfirm = async () => {
    if (!source || !targetKey) return;
    try {
      await onConfirm(source.key, targetKey);
      notifications.show({
        color: 'green',
        message: `Merged into ${targetName}.`,
      });
      setTargetKey(null);
      onClose();
    } catch (error) {
      notifications.show({
        color: 'red',
        message: error instanceof ApiClientError ? error.message : `Failed to merge ${noun}s.`,
      });
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
          value={targetKey}
          onChange={setTargetKey}
          searchable
          data-autofocus
        />
        <Alert
          icon={<IconAlertCircle size={16} />}
          color="orange"
          variant="light"
        >
          All aliases and game associations move to <strong>{targetName ?? 'the target'}</strong>,
          and <strong>{source?.name}</strong> is deleted. This can't be undone.
        </Alert>
        <Group
          gap="xs"
          justify="flex-end"
        >
          <Button
            variant="subtle"
            onClick={onClose}
          >
            Cancel
          </Button>
          <Button
            color="red"
            disabled={!targetKey || !impact.isSuccess}
            loading={busy}
            onClick={handleConfirm}
          >
            Merge
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
