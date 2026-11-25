import {
  ActionIcon,
  Alert,
  Anchor,
  Badge,
  Button,
  Code,
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
  Title,
  Tooltip,
} from '@mantine/core';
import {
  deleteTitleProviderMatch,
  listTitleProviderMatches,
  type ProviderExternalId,
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
  ProviderMatchConflictError,
  storeProviderMatch,
  useProviderMatchDecision,
  useProviderMatches,
} from '../../hooks/api/useProviderMatches';
import { UnsavedChanges } from '../Administration/UnsavedChanges';
import { workspaceActionProps } from '../Workspace/workspaceActions';
import { sourceLabel } from './metadataDraft';
import classes from './ProviderMatches.module.css';
import { ProvisionalMatchPreview } from './ProvisionalMatchPreview';
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

function ProviderIdentifier({
  provider,
  id,
  game,
}: {
  provider: string;
  id: string;
  game?: ProviderGame | null;
}) {
  const record = game?.id === id ? game : undefined;
  return record?.url?.startsWith('https://') ? (
    <Anchor
      href={record.url}
      target="_blank"
      rel="noopener noreferrer"
      size="sm"
      aria-label={`Open ${provider} ID ${id}, ${record.name} (opens in a new tab)`}
    >
      <Group
        component="span"
        gap={4}
        wrap="nowrap"
      >
        <Code>{id}</Code>
        <IconExternalLink
          size={14}
          aria-hidden
        />
      </Group>
    </Anchor>
  ) : (
    <Code>{id}</Code>
  );
}

interface ProviderMatchPickerProps {
  titleId: string;
  titleName: string;
  row: TitleProviderMatch;
  onClose: (saved: boolean) => void;
  inline?: boolean;
}

/** `onClose(true)` follows a saved provider decision. */
export function ProviderMatchPicker(props: ProviderMatchPickerProps) {
  return (
    <ProviderMatchPickerContent
      key={`${props.titleId}:${props.row.providerId}`}
      {...props}
    />
  );
}

function provisionalGame(match: TitleProviderMatch) {
  return (
    match.decision?.candidates.find(({ game }) => game.id === match.decision?.provisionalGameId)
      ?.game ?? null
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
  const [selected, setSelected] = useState(() =>
    row.provisionalReview ? null : provisionalGame(row),
  );
  const [choosingAlternative, setChoosingAlternative] = useState(false);
  const searchInput = useRef<HTMLInputElement>(null);
  const reloadControl = useRef<HTMLButtonElement>(null);
  const replacementControl = useRef<HTMLButtonElement>(null);
  const returnFocus = useRef(false);
  useEffect(() => {
    if (conflicted) {
      reloadControl.current?.focus();
      returnFocus.current = true;
    } else if (choosingAlternative) {
      searchInput.current?.focus();
    } else if (returnFocus.current) {
      (replacementControl.current ?? searchInput.current)?.focus();
      returnFocus.current = false;
    }
  }, [
    choosingAlternative,
    conflicted,
  ]);
  const [showEvidence, setShowEvidence] = useState(true);
  const [rejecting, setRejecting] = useState(false);
  const search = useProviderGameLookup(row.providerId);
  const save = useMutation({
    mutationFn: async (externalId: string | null) => {
      // The revision of the match this picker showed: a stale one is 412, never a silent overwrite.
      const input = {
        path: {
          titleId,
          providerId: row.providerId,
        },
        headers: ifMatch(match.revision),
      };
      const result =
        externalId === null
          ? await deleteTitleProviderMatch(input)
          : await replaceTitleProviderMatch({
              ...input,
              body: {
                externalId,
              },
            });
      if (result.error || !result.data) {
        setConflicted(result.response?.status === 412);
        setRejecting(false);
        throw new Error(
          result.response?.status === 412
            ? 'The provider match changed. Reload the current match before deciding again.'
            : 'The match could not be updated. Check the provider configuration or try again.',
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
      setChoosingAlternative(false);
      setSelected(null);
      setMode('search');
      setText(titleName);
      search.reset();
      setShowEvidence(true);
      save.reset();
      setConflicted(false);
    },
  });
  const [discarding, setDiscarding] = useState(false);
  const [reloading, setReloading] = useState(false);
  const dirty =
    (selected !== null && selected.id !== match.decision?.provisionalGameId) ||
    text !== (mode === 'search' ? titleName : '');
  const busy = save.isPending || reload.isPending;
  const currentProposal = match.decision?.provisionalGameId;
  const expired = match.provisionalReview?.evidenceCurrent === false;
  const evidenceCurrent = !conflicted && !expired;
  const suggestedGame = match.provisionalReview?.evidenceCurrent ? provisionalGame(match) : null;
  const reviewingSuggestion = !!suggestedGame && evidenceCurrent && !choosingAlternative;
  const target = reviewingSuggestion ? suggestedGame : selected;
  const useStoredEvidence = showEvidence && evidenceCurrent;
  const previousGame = match.externalId ?? (evidenceCurrent ? currentProposal : undefined);
  const replacing = selected !== null && previousGame != null && selected.id !== previousGame;
  const switchReview = (findAlternative: boolean) => {
    returnFocus.current = !findAlternative;
    setChoosingAlternative(findAlternative);
    setSelected(null);
    setMode('search');
    setText(titleName);
    setShowEvidence(true);
    save.reset();
    search.reset();
  };
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
  const candidates =
    search.data?.items ??
    (useStoredEvidence
      ? persistedCandidates.filter((game) => !choosingAlternative || game.id !== suggestedGame?.id)
      : []);
  const completeness = search.data?.search?.completeness;
  const local = evidenceCurrent ? match.decision?.localEvidence : undefined;
  const providerPlatforms = suggestedGame?.platforms ?? [];
  const localPlatformKeys = new Set(local?.platforms.map((system) => system.toLowerCase()));
  const proposedPlatforms =
    providerPlatforms.length === 1
      ? providerPlatforms
      : providerPlatforms.filter((system) => localPlatformKeys.has(system.toLowerCase()));
  const lookup = (
    <>
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
            data-autofocus
            ref={searchInput}
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
          {choosingAlternative ? (
            <Button
              {...workspaceActionProps}
              type="submit"
              size="sm"
              variant="light"
              aria-label={mode === 'search' ? 'Search provider games' : 'Look up provider game'}
              disabled={busy || !text.trim()}
              loading={search.isPending}
            >
              {mode === 'search' ? 'Search' : 'Look up'}
            </Button>
          ) : (
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
          )}
        </Group>
      </form>
      {search.error && <Alert color="red">{search.error.message}</Alert>}
      {match.decision && useStoredEvidence && !match.provisionalReview && (
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
      {candidates.length > 0 && (
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
                    {useStoredEvidence &&
                      (!match.provisionalReview || game.id !== currentProposal) &&
                      evidence?.evidence.map((value) => (
                        <Text
                          key={value}
                          size="xs"
                          c="dimmed"
                        >
                          {value}
                        </Text>
                      ))}
                    {useStoredEvidence &&
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
      )}
    </>
  );
  const content = (
    <Stack
      gap="sm"
      className={inline ? undefined : classes.pickerScroll}
    >
      <UnsavedChanges
        dirty={dirty && !save.isSuccess}
        description="Your provider match choice has not been confirmed."
      />
      {conflicted ? (
        <Alert
          color="yellow"
          title="The match changed"
        >
          Reload the current match before deciding again.
        </Alert>
      ) : reviewingSuggestion || expired ? (
        <ProvisionalMatchPreview
          match={match}
          localTitle={local?.name ?? titleName}
          localSystems={platformLabels(local?.platforms)}
          proposedSystems={platformLabels(proposedPlatforms)}
        />
      ) : (
        <Text size="sm">
          {choosingAlternative && suggestedGame ? (
            <>Current suggestion: {suggestedGame.name}</>
          ) : (
            <>
              Local title: {local?.name ?? titleName} ·{' '}
              {local?.platforms.length
                ? platformLabels(local.platforms).join(' / ')
                : 'platform unknown'}{' '}
              · {local?.years.length ? local.years.join(', ') : 'year unknown'}
            </>
          )}
        </Text>
      )}
      {!conflicted && !reviewingSuggestion && lookup}
      {save.isError && !conflicted && <Alert color="red">{save.error.message}</Alert>}
      {reload.isError && <Alert color="red">{reload.error.message}</Alert>}
      {reload.isSuccess && !expired && !reviewingSuggestion && (
        <Alert color="blue">
          Current match: {match.game?.name ?? match.externalId ?? 'No linked game'}. Select a game
          to confirm your choice.
        </Alert>
      )}
      {!reviewingSuggestion && (!conflicted || (dirty && selected)) && (
        <Text
          size="sm"
          role="status"
        >
          {selected
            ? choosingAlternative
              ? `Selected: ${selected.name} · ${match.providerName} #${selected.id}`
              : `Selected: ${selected.name}`
            : 'Select a provider game to confirm this match.'}
        </Text>
      )}
      {replacing && !conflicted && (
        <Text size="sm">
          Replacing the match removes its metadata and automatic artwork. ROMD will fetch data for
          the new match and keep your edits and pinned artwork.
        </Text>
      )}
    </Stack>
  );
  const actions = (
    <Stack
      className={classes.actions}
      data-inline={inline || undefined}
    >
      <Group
        className={classes.pickerButtons}
        justify="space-between"
      >
        <Group gap="xs">
          {reviewingSuggestion && (
            <Button
              ref={replacementControl}
              variant="subtle"
              disabled={busy}
              onClick={() => switchReview(true)}
            >
              Find a replacement
            </Button>
          )}
          {choosingAlternative && !conflicted && (
            <Button
              variant="subtle"
              onClick={() => switchReview(false)}
              disabled={busy}
            >
              Back to suggestion
            </Button>
          )}
          {currentProposal && evidenceCurrent && !choosingAlternative && (
            <Button
              variant="subtle"
              color="red"
              disabled={busy || !match.isAvailable}
              onClick={() => setRejecting(true)}
            >
              Reject provisional match
            </Button>
          )}
        </Group>
        <Group
          gap="xs"
          wrap="wrap"
          justify="end"
        >
          <Button
            variant="default"
            disabled={busy}
            onClick={requestClose}
          >
            Cancel
          </Button>
          {conflicted ? (
            <Button
              {...workspaceActionProps}
              ref={reloadControl}
              loading={reload.isPending}
              onClick={() => (dirty ? setReloading(true) : reload.mutate())}
            >
              Reload latest
            </Button>
          ) : (
            <Button
              {...workspaceActionProps}
              disabled={!target || !match.isAvailable || busy || search.isPending}
              loading={save.isPending && save.variables !== null}
              onClick={() => target && save.mutate(target.id)}
            >
              {choosingAlternative && replacing
                ? 'Replace match'
                : reviewingSuggestion || !target
                  ? 'Confirm match'
                  : `Confirm ${target.name}`}
            </Button>
          )}
        </Group>
      </Group>
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
          {actions}
        </Stack>
      ) : (
        <Modal
          opened
          onClose={requestClose}
          closeOnEscape={!busy && !discarding && !reloading && !rejecting}
          closeOnClickOutside={!busy && !discarding && !reloading && !rejecting}
          withCloseButton={!busy}
          title={`Match ${row.providerName}`}
          size="lg"
          classNames={{
            content: classes.pickerDialog,
            header: classes.pickerHeader,
            body: classes.pickerBody,
          }}
        >
          {content}
          {actions}
        </Modal>
      )}
      {discardDialog}
      {reloadDialog}
      <Modal
        opened={rejecting}
        onClose={() => setRejecting(false)}
        title="Reject the provisional match?"
        closeOnEscape={!save.isPending}
        closeOnClickOutside={!save.isPending}
        withCloseButton={!save.isPending}
        size="sm"
      >
        <Stack>
          <Text size="sm">
            ROMD will withdraw this match’s provisional fields and artwork. User edits and pinned
            artwork are kept. Automatic matching for this provider will stay off until you link a
            source.
          </Text>
          <Group justify="end">
            <Button
              variant="default"
              disabled={save.isPending}
              onClick={() => setRejecting(false)}
            >
              Cancel
            </Button>
            <Button
              {...workspaceActionProps}
              color="red"
              loading={save.isPending}
              onClick={() => save.mutate(null)}
            >
              Reject match
            </Button>
          </Group>
        </Stack>
      </Modal>
    </>
  );
}

export function ProviderMatches({
  titleId,
  titleName,
  externalIds,
  inline = false,
  initialProviderId,
  providerRequestKey,
  disabled = false,
  density = 'comfortable',
  onResolved,
  onDismissed,
}: {
  titleId: string;
  titleName: string;
  externalIds?: readonly ProviderExternalId[];
  inline?: boolean;
  initialProviderId?: string;
  providerRequestKey?: string;
  disabled?: boolean;
} & TitleEditorOutcome) {
  const query = useProviderMatches(titleId);
  // Enabled providers report the current link, even when enrichment still has a cached ID.
  const savedIds = externalIds?.filter(
    (id) => !query.data?.some((row) => row.providerId.toLowerCase() === id.provider.toLowerCase()),
  );
  const [editing, setEditing] = useState<TitleProviderMatch | null>(null);
  const [unlinking, setUnlinking] = useState<TitleProviderMatch | null>(null);
  const openedRequest = useRef<string | null>(null);
  useEffect(() => {
    if (!initialProviderId) {
      openedRequest.current = null;
      return;
    }
    if (disabled) return;
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
    disabled,
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
  const closeUnlink = () => {
    if (mutation.isPending) return;
    setUnlinking(null);
    mutation.reset();
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
        <Title
          order={3}
          className={`romd-card-title ${classes.subheading}`}
        >
          External sources
        </Title>
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
      {mutation.isError && !unlinking && <Alert color="red">{mutation.error.message}</Alert>}
      {query.data?.length === 0 && (
        <Text
          size="sm"
          c="dimmed"
        >
          No external providers are enabled for linking.
        </Text>
      )}
      {query.data?.map((row) => {
        const canContinueWithout =
          !row.externalId &&
          [
            'NeedsMatch',
            'NeedsReview',
            'NoMatchFound',
          ].includes(row.state);
        return (
          <Group
            key={row.providerId}
            className={classes.match}
            role="group"
            aria-label={row.providerName}
            justify="space-between"
            align="center"
          >
            <Stack
              gap={6}
              className={classes.matchBody}
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
                {row.decision?.provisionalGameId && (
                  <Badge
                    size="xs"
                    variant="light"
                    color="yellow"
                  >
                    Provisional match
                  </Badge>
                )}
              </Group>
              {row.externalId && (
                <Group gap="xs">
                  <ProviderIdentifier
                    provider={row.providerName}
                    id={row.externalId}
                    game={row.game}
                  />
                  {row.game?.id === row.externalId && (
                    <Text
                      size="sm"
                      c="dimmed"
                      className={classes.recordName}
                    >
                      {row.game.name}
                    </Text>
                  )}
                </Group>
              )}
              {!row.isAvailable && (
                <Text
                  size="xs"
                  c="dimmed"
                >
                  Provider configuration required
                </Text>
              )}
              {(row.evidence || row.provisionalReview?.evidenceCurrent === false) && (
                <Text
                  size="xs"
                  c="dimmed"
                >
                  {row.provisionalReview?.evidenceCurrent === false
                    ? 'The provisional evidence changed. Review the match again.'
                    : row.evidence}
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
                  Provider metadata needs a refresh
                </Text>
              )}
              {row.state === 'Unlinked' && (
                <Text
                  size="xs"
                  c="dimmed"
                >
                  Automatic matching is off.
                </Text>
              )}
            </Stack>
            <Group
              gap="xs"
              justify="end"
            >
              <Button
                {...workspaceActionProps}
                size="compact-sm"
                variant={row.externalId ? 'subtle' : 'light'}
                disabled={disabled || mutation.isPending || !row.isAvailable}
                onClick={() => setEditing(row)}
              >
                {row.state === 'NeedsReview'
                  ? 'Review match'
                  : row.externalId
                    ? 'Change match'
                    : 'Link source'}
              </Button>
              {row.externalId && row.state !== 'Confirmed' && row.state !== 'NeedsReview' && (
                <Button
                  size="compact-sm"
                  loading={
                    mutation.isPending && mutation.variables?.row.providerId === row.providerId
                  }
                  disabled={disabled || mutation.isPending || !row.isAvailable}
                  onClick={() => decide(row, false)}
                >
                  Confirm match
                </Button>
              )}
              {(row.externalId || canContinueWithout) && (
                <Menu>
                  <Menu.Target>
                    <ActionIcon
                      variant="subtle"
                      aria-label={`${row.providerName} match actions`}
                      disabled={disabled || mutation.isPending || !row.isAvailable}
                    >
                      <IconDots size={18} />
                    </ActionIcon>
                  </Menu.Target>
                  <Menu.Dropdown>
                    <Menu.Item
                      color={row.externalId || row.decision?.provisionalGameId ? 'red' : undefined}
                      leftSection={<IconUnlink size={16} />}
                      onClick={() => {
                        mutation.reset();
                        setUnlinking(row);
                      }}
                    >
                      {row.externalId
                        ? 'Unlink'
                        : row.decision?.provisionalGameId
                          ? 'Reject provisional match'
                          : 'Continue without this provider'}
                    </Menu.Item>
                  </Menu.Dropdown>
                </Menu>
              )}
            </Group>
          </Group>
        );
      })}
      {savedIds?.map((entry) => (
        <Group
          key={`${entry.provider}:${entry.externalId}`}
          className={classes.match}
          gap="xs"
        >
          <Text
            fw={500}
            size="sm"
          >
            {sourceLabel(entry.provider)}
          </Text>
          <ProviderIdentifier
            provider={sourceLabel(entry.provider)}
            id={entry.externalId}
          />
          <Badge
            size="xs"
            variant="light"
            color={entry.isConfirmed ? 'green' : 'gray'}
          >
            {entry.isConfirmed ? 'Confirmed' : 'Unconfirmed'}
          </Badge>
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
        onClose={closeUnlink}
        closeOnEscape={!mutation.isPending}
        closeOnClickOutside={!mutation.isPending}
        withCloseButton={!mutation.isPending}
        title={
          unlinking?.decision?.provisionalGameId
            ? 'Reject the provisional match?'
            : unlinking?.externalId
              ? `Unlink ${unlinking.providerName}`
              : `Continue without ${unlinking?.providerName}?`
        }
        size="sm"
      >
        <Stack>
          <Text size="sm">
            {unlinking?.decision?.provisionalGameId &&
              'ROMD will withdraw contributions from the provisional match and keep your pinned artwork. '}
            {unlinking?.externalId &&
              'ROMD will remove metadata and automatic artwork from this match and keep your pinned artwork. '}
            Automatic matching for this provider will stay off until you link a source.
          </Text>
          {mutation.isError && <Alert color="red">{mutation.error.message}</Alert>}
          <Group justify="end">
            <Button
              variant="default"
              disabled={mutation.isPending}
              onClick={closeUnlink}
            >
              Cancel
            </Button>
            <Button
              {...workspaceActionProps}
              color={
                unlinking?.externalId || unlinking?.decision?.provisionalGameId ? 'red' : undefined
              }
              loading={mutation.isPending}
              disabled={disabled || mutation.error instanceof ProviderMatchConflictError}
              onClick={() => unlinking && decide(unlinking, true, () => setUnlinking(null))}
            >
              {unlinking?.externalId
                ? 'Unlink match'
                : unlinking?.decision?.provisionalGameId
                  ? 'Reject match'
                  : 'Continue without provider'}
            </Button>
          </Group>
        </Stack>
      </Modal>
    </div>
  );
}
