import {
  ActionIcon,
  Alert,
  Anchor,
  Badge,
  Button,
  Group,
  Image,
  Menu,
  Modal,
  Radio,
  SegmentedControl,
  Skeleton,
  Stack,
  Text,
  TextInput,
  Tooltip,
} from '@mantine/core';
import {
  listTitleProviderMatches,
  type ProviderGame,
  replaceTitleProviderMatch,
  type TitleProviderMatch,
} from '@romd/admin-api-client';
import { useReferenceCatalog } from '@romd/consumer-ui';
import { IconDots, IconExternalLink, IconSearch, IconUnlink } from '@tabler/icons-react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useEffect, useRef, useState } from 'react';
import { invalidateTitle, providerMatchKeys } from '../../hooks/api/titleCache';
import { ifMatch } from '../../hooks/api/titleConflict';
import { useProviderGameLookup } from '../../hooks/api/useProviderGameLookup';
import {
  storeProviderMatch,
  useProviderMatchDecision,
  useProviderMatches,
} from '../../hooks/api/useProviderMatches';
import { UnsavedChanges } from '../Administration/UnsavedChanges';
import { workspaceActionProps } from '../Workspace/workspaceActions';
import classes from './ProviderMatches.module.css';
import type { TitleEditorOutcome } from './titleWorkbenchModel';

/** Render local mapping keys as readable system names alongside provider labels. */
function usePlatformLabels() {
  const catalog = useReferenceCatalog();
  return (platforms: readonly string[] | null | undefined) => [
    ...new Set(
      (platforms ?? []).map(
        (platform) => catalog?.systems.find((system) => system.key === platform)?.name ?? platform,
      ),
    ),
  ];
}

function GamePreview({ game, showDetails = true }: { game: ProviderGame; showDetails?: boolean }) {
  const platformLabels = usePlatformLabels();
  return (
    <Group
      gap="sm"
      wrap="nowrap"
      align="center"
      style={{
        minWidth: 0,
      }}
    >
      {game.thumbnailUrl && (
        <Image
          src={game.thumbnailUrl}
          alt=""
          w={40}
          h={56}
          fit="contain"
          referrerPolicy="no-referrer"
          style={{
            flexShrink: 0,
          }}
        />
      )}
      <Stack
        gap={2}
        style={{
          minWidth: 0,
        }}
      >
        <Anchor
          href={game.url}
          target="_blank"
          rel="noopener noreferrer"
          size="sm"
          style={{
            overflowWrap: 'anywhere',
          }}
        >
          {game.name}{' '}
          <IconExternalLink
            size={13}
            aria-label="Open provider page"
          />
        </Anchor>
        {showDetails && (
          <Text
            size="xs"
            c="dimmed"
            style={{
              overflowWrap: 'anywhere',
            }}
          >
            {[
              game.year,
              ...platformLabels(game.platforms),
              `#${game.id}`,
            ]
              .filter(Boolean)
              .join(' · ')}
          </Text>
        )}
      </Stack>
    </Group>
  );
}

interface ProviderMatchPickerProps {
  titleId: string;
  titleName: string;
  row: TitleProviderMatch;
  onClose: (saved: boolean) => void;
  inline?: boolean;
}

/** `onClose(true)` follows a confirmed match; any other close leaves the match unchanged. */
export function ProviderMatchPicker(props: ProviderMatchPickerProps) {
  return (
    <ProviderMatchPickerContent
      key={`${props.titleId}:${props.row.providerId}`}
      {...props}
    />
  );
}

function ProviderMatchPickerContent({
  titleId,
  titleName,
  row,
  onClose,
  inline = false,
}: ProviderMatchPickerProps) {
  const platformLabels = usePlatformLabels();
  const cache = useQueryClient();
  const mounted = useRef(true);
  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);
  const [match, setMatch] = useState(row);
  const [conflicted, setConflicted] = useState(false);
  const [mode, setMode] = useState<'search' | 'resolve'>('search');
  const [text, setText] = useState(titleName);
  const [selected, setSelected] = useState<ProviderGame | null>(null);
  const [showEvidence, setShowEvidence] = useState(true);
  const search = useProviderGameLookup(row.providerId);
  const save = useMutation({
    mutationFn: async () => {
      if (!selected) return;
      // The revision of the match this picker showed: a stale one is 412, never a silent overwrite.
      const result = await replaceTitleProviderMatch({
        path: {
          titleId,
          providerId: row.providerId,
        },
        headers: ifMatch(match.revision),
        body: {
          externalId: selected.id,
        },
      });
      if (result.error || !result.data) {
        setConflicted(result.response?.status === 412);
        throw new Error(
          result.response?.status === 412
            ? 'The provider match changed. Reload the current match, then select and confirm your choice again.'
            : 'The match could not be saved. Check the provider configuration or try again.',
        );
      }
      storeProviderMatch(cache, titleId, result.data);
      await invalidateTitle(cache, titleId);
      if (mounted.current) onClose(true);
    },
  });
  const reload = useMutation({
    mutationFn: async () => {
      const result = await listTitleProviderMatches({
        path: {
          titleId,
        },
      });
      if (result.error || !result.data)
        throw new Error('Provider matches could not be reloaded. Try again.');
      const latest = result.data.find((item) => item.providerId === row.providerId);
      if (!latest)
        throw new Error(
          'This provider is no longer enabled. Close the picker and check provider settings.',
        );
      cache.setQueryData(providerMatchKeys.title(titleId), result.data);
      setMatch(latest);
      setSelected(null);
      search.reset();
      setShowEvidence(true);
      save.reset();
      setConflicted(false);
    },
  });
  const [discarding, setDiscarding] = useState(false);
  const [reloading, setReloading] = useState(false);
  const dirty = selected !== null || text !== (mode === 'search' ? titleName : '');
  const busy = save.isPending || reload.isPending;
  const requestClose = () => {
    if (busy) return;
    if (dirty) setDiscarding(true);
    else onClose(false);
  };
  const persistedCandidates = match.decision?.candidates.length
    ? match.decision.candidates.map((candidate) => candidate.game)
    : match.game
      ? [
          match.game,
        ]
      : [];
  const candidates = search.data?.items ?? (showEvidence ? persistedCandidates : []);
  const completeness = search.data?.search?.completeness;
  const local = match.decision?.localEvidence;
  const content = (
    <Stack gap="sm">
      <UnsavedChanges
        dirty={dirty && !save.isSuccess}
        description="Your provider match choice has not been confirmed."
      />
      <Text size="sm">
        Local title: {local?.name ?? titleName} ·{' '}
        {local?.platforms.length ? local.platforms.join(' / ') : 'platform unknown'} ·{' '}
        {local?.years.length ? local.years.join(', ') : 'year unknown'}
      </Text>
      <SegmentedControl
        disabled={busy}
        value={mode}
        onChange={(value) => {
          const nextMode = value === 'resolve' ? 'resolve' : 'search';
          setMode(nextMode);
          setText(nextMode === 'search' ? titleName : '');
          setSelected(null);
          setShowEvidence(false);
          search.reset();
        }}
        data={[
          {
            value: 'search',
            label: 'Search',
          },
          {
            value: 'resolve',
            label: 'ID or URL',
          },
        ]}
      />
      <form
        onSubmit={(event) => {
          event.preventDefault();
          if (!busy && !search.isPending && text.trim()) {
            setSelected(null);
            setShowEvidence(false);
            void search.lookup(text.trim(), mode);
          }
        }}
      >
        <Group
          align="end"
          wrap="nowrap"
        >
          <TextInput
            data-autofocus={inline || undefined}
            disabled={busy}
            label={mode === 'search' ? 'Game name' : 'Provider ID or page URL'}
            value={text}
            onChange={(event) => {
              setText(event.currentTarget.value);
              setSelected(null);
              setShowEvidence(false);
              search.reset();
            }}
            maxLength={500}
            style={{
              flex: 1,
              minWidth: 0,
            }}
          />
          <Tooltip label={mode === 'search' ? 'Search games' : 'Look up game'}>
            <ActionIcon
              {...workspaceActionProps}
              type="submit"
              size="lg"
              variant="filled"
              aria-label={mode === 'search' ? 'Search provider games' : 'Look up provider game'}
              loading={search.isPending}
              disabled={busy || !text.trim()}
            >
              <IconSearch size={18} />
            </ActionIcon>
          </Tooltip>
        </Group>
      </form>
      {search.error && <Alert color="red">{search.error.message}</Alert>}
      {match.decision && (
        <details>
          <summary>Match evidence</summary>
          <Text size="sm">{match.decision.explanation}</Text>
          <Text
            size="xs"
            c="dimmed"
          >
            {match.decision.provenance}
          </Text>
        </details>
      )}
      {search.data?.search && completeness !== 'Complete' && (
        <Alert color="blue">
          {completeness === 'ProviderLimited'
            ? `Provider search is limited to ${search.data.search.limit} suggestions and may be incomplete.`
            : `The ${search.data.search.limit}-game search limit was reached. More matches may exist.`}{' '}
          Refine your search or enter a game ID or URL.
        </Alert>
      )}
      {search.data?.items.length === 0 && (
        <Text
          c="dimmed"
          size="sm"
        >
          {completeness === 'Complete'
            ? 'No suggestions returned for this search.'
            : 'No suggestions returned; more matches may exist.'}
        </Text>
      )}
      <Radio.Group
        label="Provider candidates"
        value={selected?.id ?? ''}
        onChange={(id) => setSelected(candidates.find((game) => game.id === id) ?? null)}
      >
        <Stack
          gap={0}
          className={classes.candidates}
        >
          {candidates.map((game) => {
            const evidence = match.decision?.candidates.find(
              (candidate) => candidate.game.id === game.id,
            );
            return (
              <div
                key={game.id}
                className={classes.candidate}
                data-selected={selected?.id === game.id || undefined}
              >
                <Radio
                  value={game.id}
                  label={`Select ${game.name}`}
                  disabled={busy || search.isPending || conflicted}
                />
                <div className={classes.preview}>
                  <GamePreview
                    game={game}
                    showDetails={false}
                  />
                  <Text
                    size="xs"
                    c="dimmed"
                  >
                    Provider evidence:{' '}
                    {game.platforms?.length
                      ? platformLabels(game.platforms).join(' / ')
                      : 'platform unknown'}{' '}
                    · {game.year ?? 'year unknown'}
                  </Text>
                  {showEvidence &&
                    evidence?.evidence.map((value) => (
                      <Text
                        key={value}
                        size="xs"
                        c="dimmed"
                      >
                        {value}
                      </Text>
                    ))}
                  {showEvidence &&
                    evidence?.conflicts.map((value) => (
                      <Text
                        key={value}
                        size="xs"
                        c="orange"
                      >
                        Conflict: {value}
                      </Text>
                    ))}
                  {selected?.id === game.id && (
                    <Badge
                      size="xs"
                      variant="light"
                      color="teal"
                    >
                      Selected
                    </Badge>
                  )}
                </div>
              </div>
            );
          })}
        </Stack>
      </Radio.Group>
      <Stack
        gap="xs"
        className={classes.actions}
        data-inline={inline || undefined}
      >
        {save.isError && <Alert color="red">{save.error.message}</Alert>}
        {conflicted && (
          <Button
            {...workspaceActionProps}
            variant="light"
            loading={reload.isPending}
            onClick={() => (selected ? setReloading(true) : reload.mutate())}
          >
            Reload latest
          </Button>
        )}
        {reload.isError && <Alert color="red">{reload.error.message}</Alert>}
        {reload.isSuccess && (
          <Alert color="blue">
            Current match: {match.game?.name ?? match.externalId ?? 'No linked game'}. Select a game
            to confirm your choice.
          </Alert>
        )}
        <Text
          size="sm"
          role="status"
        >
          {selected
            ? `Selected: ${selected.name}`
            : 'Select a provider game to confirm this match.'}
        </Text>
        <Group justify="end">
          <Button
            variant="default"
            disabled={busy}
            onClick={requestClose}
          >
            Cancel
          </Button>
          <Button
            {...workspaceActionProps}
            disabled={
              !selected || conflicted || !match.isAvailable || reload.isPending || search.isPending
            }
            loading={save.isPending}
            onClick={() => save.mutate()}
          >
            {selected ? `Confirm ${selected.name}` : 'Confirm match'}
          </Button>
        </Group>
      </Stack>
    </Stack>
  );
  const discardDialog = (
    <Modal
      opened={discarding}
      onClose={() => setDiscarding(false)}
      title="Discard provider match draft?"
      centered
    >
      <Stack>
        <Text>Your provider match choice has not been confirmed.</Text>
        <Group justify="end">
          <Button
            variant="default"
            onClick={() => setDiscarding(false)}
          >
            Keep editing
          </Button>
          <Button
            color="red"
            onClick={() => onClose(false)}
          >
            Discard draft
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
  const reloadDialog = (
    <Modal
      opened={reloading}
      onClose={() => setReloading(false)}
      title="Reload the latest match?"
      centered
    >
      <Stack>
        <Text>Your selected game has not been confirmed and will be cleared.</Text>
        <Group justify="end">
          <Button
            variant="default"
            onClick={() => setReloading(false)}
          >
            Keep editing
          </Button>
          <Button
            color="red"
            onClick={() => {
              setReloading(false);
              reload.mutate();
            }}
          >
            Reload latest
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
  return (
    <>
      {inline ? (
        <Stack>
          <Text className={`romd-card-title ${classes.pickerHeading}`}>
            Match {row.providerName}
          </Text>
          {content}
        </Stack>
      ) : (
        <Modal
          opened
          onClose={requestClose}
          closeOnEscape={!busy && !discarding && !reloading}
          closeOnClickOutside={!busy && !discarding && !reloading}
          withCloseButton={!busy}
          title={`Match ${row.providerName}`}
          size="lg"
        >
          {content}
        </Modal>
      )}
      {discardDialog}
      {reloadDialog}
    </>
  );
}

export function ProviderMatches({
  titleId,
  titleName,
  inline = false,
  initialProviderId,
  providerRequestKey,
  density = 'comfortable',
  onResolved,
  onDismissed,
}: {
  titleId: string;
  titleName: string;
  inline?: boolean;
  initialProviderId?: string;
  providerRequestKey?: string;
} & TitleEditorOutcome) {
  const query = useProviderMatches(titleId);
  const [editing, setEditing] = useState<TitleProviderMatch | null>(null);
  const [unlinking, setUnlinking] = useState<TitleProviderMatch | null>(null);
  const openedRequest = useRef<string | null>(null);
  useEffect(() => {
    if (!initialProviderId) {
      openedRequest.current = null;
      return;
    }
    const request = `${titleId}:${initialProviderId}:${providerRequestKey ?? ''}`;
    if (openedRequest.current === request) return;
    if (editing?.providerId === initialProviderId) {
      openedRequest.current = request;
      return;
    }
    const requested = query.data?.find((row) => row.providerId === initialProviderId);
    if (query.data) {
      openedRequest.current = request;
      setEditing(requested?.isAvailable ? requested : null);
    }
  }, [
    initialProviderId,
    providerRequestKey,
    titleId,
    query.data,
    editing,
  ]);
  const mutation = useProviderMatchDecision(titleId);
  const decide = (row: TitleProviderMatch, unlink: boolean, onSuccess?: () => void) =>
    mutation.mutate(
      {
        row,
        unlink,
      },
      {
        onSuccess: () => {
          onSuccess?.();
          onResolved?.();
        },
      },
    );
  const closePicker = (saved: boolean) => {
    setEditing(null);
    if (saved) onResolved?.();
    else onDismissed?.();
  };
  if (inline && editing)
    return (
      <ProviderMatchPicker
        inline
        key={`${editing.providerId}:${providerRequestKey ?? ''}`}
        titleId={titleId}
        titleName={titleName}
        row={editing}
        onClose={closePicker}
      />
    );
  return (
    <div
      className={classes.matches}
      data-density={density}
    >
      <Group justify="space-between">
        <Text fw={600}>Provider matches</Text>
        <Button
          size="compact-xs"
          variant="subtle"
          loading={query.isFetching}
          onClick={() => {
            mutation.reset();
            void query.refetch();
          }}
        >
          Reload matches
        </Button>
      </Group>
      {query.isPending && <Skeleton height={72} />}
      {query.isError && <Alert color="red">{query.error.message}</Alert>}
      {mutation.isError && <Alert color="red">{mutation.error.message}</Alert>}
      {query.data?.length === 0 && (
        <Text
          size="sm"
          c="dimmed"
        >
          No providers enabled.
        </Text>
      )}
      {query.data?.map((row) => (
        <Group
          key={row.providerId}
          className={classes.match}
          justify="space-between"
          align="center"
        >
          <Stack
            gap={6}
            style={{
              flex: '1 1 220px',
              minWidth: 0,
            }}
          >
            <Group gap="xs">
              <Text
                fw={500}
                size="sm"
              >
                {row.providerName}
              </Text>
              <Badge
                size="xs"
                variant="light"
                color={row.state === 'Confirmed' ? 'green' : 'gray'}
              >
                {(
                  {
                    AutoMatched: 'Auto-matched',
                    NeedsMatch: 'Needs match',
                    NeedsReview: 'Needs review',
                    NoMatchFound: 'No match found',
                  } as Record<string, string>
                )[row.state] ?? row.state}
              </Badge>
            </Group>
            {row.game ? (
              <GamePreview game={row.game} />
            ) : (
              row.externalId && (
                <Text
                  size="sm"
                  ff="monospace"
                >
                  #{row.externalId}
                </Text>
              )
            )}
            {!row.isAvailable && (
              <Text
                size="xs"
                c="dimmed"
              >
                Provider configuration required
              </Text>
            )}
            {row.evidence && (
              <Text
                size="xs"
                c="dimmed"
              >
                {row.evidence}
              </Text>
            )}
            {row.lastError && (
              <Text
                size="xs"
                c="dimmed"
              >
                {row.lastError}
              </Text>
            )}
            {row.metadataNeedsRefresh && (
              <Text
                size="xs"
                c="orange"
              >
                Metadata retained from a previous match
              </Text>
            )}
            {!row.externalId &&
              [
                'NeedsMatch',
                'NeedsReview',
                'NoMatchFound',
              ].includes(row.state) && (
                <Text
                  size="xs"
                  c="dimmed"
                >
                  You can continue without this provider. Automatic matching will stay off until you
                  choose a match.
                </Text>
              )}
            {row.state === 'Unlinked' && (
              <Text
                size="xs"
                c="dimmed"
              >
                Automatic matching is off. Find a match to use this provider again.
              </Text>
            )}
          </Stack>
          <Group
            gap="xs"
            justify="end"
          >
            <Button
              size="compact-sm"
              variant="subtle"
              disabled={!row.isAvailable}
              onClick={() => setEditing(row)}
            >
              {row.externalId ? 'Change' : 'Find match'}
            </Button>
            {!row.externalId &&
              [
                'NeedsMatch',
                'NeedsReview',
                'NoMatchFound',
              ].includes(row.state) && (
                <Button
                  size="compact-sm"
                  variant="default"
                  loading={
                    mutation.isPending && mutation.variables?.row.providerId === row.providerId
                  }
                  disabled={mutation.isPending || !row.isAvailable}
                  onClick={() => decide(row, true)}
                >
                  Continue without this provider
                </Button>
              )}
            {row.externalId && row.state !== 'Confirmed' && row.state !== 'NeedsReview' && (
              <Button
                size="compact-sm"
                loading={
                  mutation.isPending && mutation.variables?.row.providerId === row.providerId
                }
                disabled={mutation.isPending || !row.isAvailable}
                onClick={() => decide(row, false)}
              >
                Confirm match
              </Button>
            )}
            {row.externalId && (
              <Menu>
                <Menu.Target>
                  <ActionIcon
                    variant="subtle"
                    aria-label={`${row.providerName} match actions`}
                  >
                    <IconDots size={18} />
                  </ActionIcon>
                </Menu.Target>
                <Menu.Dropdown>
                  <Menu.Item
                    color="red"
                    leftSection={<IconUnlink size={16} />}
                    disabled={!row.isAvailable}
                    onClick={() => setUnlinking(row)}
                  >
                    Unlink
                  </Menu.Item>
                </Menu.Dropdown>
              </Menu>
            )}
          </Group>
        </Group>
      ))}
      {editing && (
        <ProviderMatchPicker
          key={`${editing.providerId}:${providerRequestKey ?? ''}`}
          titleId={titleId}
          titleName={titleName}
          row={editing}
          onClose={closePicker}
        />
      )}
      <Modal
        opened={!!unlinking}
        onClose={() => setUnlinking(null)}
        title={`Unlink ${unlinking?.providerName ?? ''}`}
        size="sm"
      >
        <Stack>
          <Text size="sm">
            Retained metadata and artwork will remain. Automatic matching for this provider will
            stay off until you choose another match.
          </Text>
          <Button
            color="red"
            loading={mutation.isPending}
            onClick={() => unlinking && decide(unlinking, true, () => setUnlinking(null))}
          >
            Unlink match
          </Button>
        </Stack>
      </Modal>
    </div>
  );
}
