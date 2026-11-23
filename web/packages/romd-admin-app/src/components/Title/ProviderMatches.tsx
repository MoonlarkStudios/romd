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
  confirmTitleProviderMatch,
  deleteTitleProviderMatch,
  listTitleProviderMatches,
  type ProviderExternalId,
  type ProviderGame,
  replaceTitleProviderMatch,
  type TitleProviderMatch,
} from '@romd/admin-api-client';
import { useReferenceCatalog } from '@romd/consumer-ui';
import { IconDots, IconExternalLink, IconPhoto, IconSearch, IconUnlink } from '@tabler/icons-react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useEffect, useId, useLayoutEffect, useRef, useState } from 'react';
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
import { DecisionConfirmation } from './DecisionConfirmation';
import { continueWithoutProviderConfirmation } from './enrichmentPresentation';
import { sourceLabel } from './metadataDraft';
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

function platformNameKey(name: string) {
  return name.normalize('NFKC').trim().replace(/\s+/g, ' ').toLowerCase();
}

function releaseYearsOnSystem(game: ProviderGame, matchesSystem: (platform: string) => boolean) {
  const years = new Set<number>();
  for (const { platform, year } of game.releases ?? []) {
    if (platform && year != null && matchesSystem(platform)) years.add(year);
  }
  return [
    ...years,
  ].sort((left, right) => left - right);
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
  onClose: (saved: boolean, match?: TitleProviderMatch) => void;
  inline?: boolean;
  review?: {
    platformName: string;
    thumbnailUrl?: string | null;
    onSkip: () => void;
    loadingNextTitle: boolean;
    canContinueWithout: boolean;
    stale: boolean;
    onShowLatest: () => void;
    onDraftChange: (match: TitleProviderMatch | undefined) => void;
    onBusyChange: (busy: boolean) => void;
  };
}

type ProviderDecision =
  | {
      kind: 'match';
      externalId: string;
    }
  | {
      kind: 'continue';
    };

/** `onClose(true)` follows an accepted decision. */
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
  review,
}: ProviderMatchPickerProps) {
  const platformLabels = usePlatformLabels();
  const candidateEvidence = useId();
  const cache = useQueryClient();
  const mounted = useRef(true);
  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);
  const [match, setMatch] = useState(row);
  const inReview = !!review;
  useLayoutEffect(() => {
    // Catalog holds the displayed revision while a decision is being edited or saved.
    if (inReview) setMatch(row);
  }, [
    inReview,
    row,
  ]);
  const [conflicted, setConflicted] = useState(false);
  const [mode, setMode] = useState<'search' | 'resolve'>('search');
  const [text, setText] = useState(titleName);
  const [selected, setSelected] = useState<ProviderGame | null>(null);
  const [showEvidence, setShowEvidence] = useState(true);
  const lookupDisclosure = useRef<HTMLDetailsElement>(null);
  const search = useProviderGameLookup(row.providerId);
  const [continuingWithout, setContinuingWithout] = useState(false);
  const [submitting, setSubmitting] = useState<ProviderDecision['kind']>();
  const save = useMutation({
    mutationFn: async (decision: ProviderDecision) => {
      // The revision of the match this picker showed: a stale one is 412, never a silent overwrite.
      const input = {
        path: {
          titleId,
          providerId: row.providerId,
        },
        headers: ifMatch(match.revision),
      };
      const result =
        decision.kind === 'continue'
          ? await deleteTitleProviderMatch(input)
          : review && decision.externalId === match.externalId
            ? await confirmTitleProviderMatch(input)
            : await replaceTitleProviderMatch({
                ...input,
                body: {
                  externalId: decision.externalId,
                },
              });
      if (result.error || !result.data) {
        setConflicted(result.response?.status === 412);
        throw new Error(
          result.response?.status === 412
            ? 'The provider match changed. Reload the current match, then select and confirm your choice again.'
            : 'The provider decision could not be saved. Try again.',
        );
      }
      storeProviderMatch(cache, titleId, result.data);
      // A refreshed queue can drop a resolved issue before the picker receives the save result.
      if (review && mounted.current) onClose(true, result.data);
      await invalidateTitle(cache, titleId);
      if (!review && mounted.current) onClose(true, result.data);
    },
  });
  const submit = async (decision: ProviderDecision) => {
    setSubmitting(decision.kind);
    try {
      await save.mutateAsync(decision);
    } finally {
      setSubmitting(undefined);
    }
  };
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
  const saving = submitting !== undefined;
  const busy = saving || reload.isPending;
  const draft = dirty || continuingWithout || busy ? match : undefined;
  const onDraftChange = review?.onDraftChange;
  const onBusyChange = review?.onBusyChange;
  const decisionOpen = busy || continuingWithout || reloading;
  useLayoutEffect(() => {
    onDraftChange?.(draft);
  }, [
    draft,
    onDraftChange,
  ]);
  useLayoutEffect(() => {
    onBusyChange?.(decisionOpen);
    return () => onBusyChange?.(false);
  }, [
    decisionOpen,
    onBusyChange,
  ]);
  const stale = !!review?.stale && !saving;
  const inputsLocked = busy || stale;
  const continueWithout = review?.canContinueWithout
    ? continueWithoutProviderConfirmation(row.providerId)
    : undefined;
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
  const candidates = search.data?.items ?? (showEvidence || review ? persistedCandidates : []);
  const resultCount = search.data?.items.length;
  const completeness = search.data?.search?.completeness;
  const local = match.decision?.localEvidence;
  const localPlatforms = new Set(
    [
      review?.platformName ?? '',
      ...(local?.platforms ?? []),
    ].map(platformNameKey),
  );
  const matchesLocalSystem = (platform: string) =>
    localPlatforms.has(platformNameKey(platform)) ||
    platformLabels([
      platform,
    ]).some((name) => localPlatforms.has(platformNameKey(name)));
  const showSuggestions = () => {
    setMode('search');
    setText(titleName);
    setSelected(null);
    setShowEvidence(true);
    search.reset();
    if (lookupDisclosure.current) {
      lookupDisclosure.current.open = false;
      lookupDisclosure.current.querySelector('summary')?.focus();
    }
  };
  const lookup = (
    <Stack gap="xs">
      <SegmentedControl
        disabled={inputsLocked}
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
          if (!inputsLocked && !search.isPending && text.trim()) {
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
            disabled={inputsLocked}
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
              disabled={inputsLocked || !text.trim()}
            >
              <IconSearch size={18} />
            </ActionIcon>
          </Tooltip>
        </Group>
      </form>
      {search.error && <Alert color="red">{search.error.message}</Alert>}
    </Stack>
  );
  const content = (
    <Stack gap="sm">
      <UnsavedChanges
        dirty={(dirty || (continuingWithout && save.isError)) && !save.isSuccess}
        saving={inReview && saving}
        description="Your provider match choice has not been confirmed."
      />
      {review ? (
        <div className={classes.localGame}>
          <MatchCover url={review.thumbnailUrl} />
          <div>
            <Text
              size="xs"
              c="dimmed"
            >
              Your catalog
            </Text>
            <Text fw={600}>{local?.name ?? titleName}</Text>
            <Text
              size="sm"
              c="dimmed"
            >
              {review.platformName} ·{' '}
              {local?.years.length ? local.years.join(', ') : 'Year unknown'}
            </Text>
          </div>
        </div>
      ) : (
        <>
          <Text size="sm">
            Local title: {local?.name ?? titleName} ·{' '}
            {local?.platforms.length ? local.platforms.join(' / ') : 'platform unknown'} ·{' '}
            {local?.years.length ? local.years.join(', ') : 'year unknown'}
          </Text>
          {lookup}
        </>
      )}
      {!review && match.decision && (
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
      {review && (
        <details
          className={classes.reviewLookup}
          ref={lookupDisclosure}
          open={!persistedCandidates.length || undefined}
        >
          <summary>{persistedCandidates.length ? 'Find another match' : 'Find a match'}</summary>
          {lookup}
        </details>
      )}
      {review && resultCount !== undefined && (
        <Group
          justify="space-between"
          gap="xs"
        >
          <Text
            size="xs"
            c="dimmed"
            role="status"
          >
            {resultCount === 0
              ? 'No matches found'
              : resultCount === 1
                ? '1 match found'
                : `${resultCount} matches found`}
          </Text>
          {persistedCandidates.length > 0 && (
            <Button
              variant="subtle"
              size="compact-sm"
              onClick={showSuggestions}
              disabled={inputsLocked}
            >
              Show original suggestions
            </Button>
          )}
        </Group>
      )}
      {search.data?.search && completeness !== 'Complete' && (
        <Alert color="blue">
          {completeness === 'ProviderLimited'
            ? `Provider search is limited to ${search.data.search.limit} suggestions and may be incomplete.`
            : `The ${search.data.search.limit}-game search limit was reached. More matches may exist.`}{' '}
          Refine your search or enter a game ID or URL.
        </Alert>
      )}
      {!review && resultCount === 0 && (
        <Text
          c="dimmed"
          size="sm"
        >
          {completeness === 'Complete'
            ? 'No suggestions returned for this search.'
            : 'No suggestions returned; more matches may exist.'}
        </Text>
      )}
      {(candidates.length > 0 || !review) && (
        <Radio.Group
          label={review && search.data ? 'Provider search results' : 'Provider candidates'}
          value={selected?.id ?? ''}
          onChange={(id) => setSelected(candidates.find((game) => game.id === id) ?? null)}
        >
          <Stack
            gap={review ? 'xs' : 0}
            className={review ? classes.reviewCandidates : classes.candidates}
          >
            {candidates.map((game) => {
              const evidence = search.data
                ? undefined
                : match.decision?.candidates.find((candidate) => candidate.game.id === game.id);
              if (review) {
                const systemYears = releaseYearsOnSystem(game, matchesLocalSystem);
                const candidatePlatforms = platformLabels(game.platforms);
                return (
                  <div
                    key={game.id}
                    className={classes.reviewCandidate}
                    data-selected={selected?.id === game.id || undefined}
                  >
                    <Radio
                      value={game.id}
                      aria-label={`Select ${game.name}`}
                      aria-describedby={`${candidateEvidence}-${game.id}`}
                      data-provider-candidate
                      disabled={inputsLocked || search.isPending || conflicted}
                      label={
                        <span className={classes.candidateLabel}>
                          <MatchCover url={game.thumbnailUrl} />
                          <span className={classes.candidateText}>
                            <Text
                              component="span"
                              fw={600}
                              className={classes.candidateTitle}
                            >
                              {game.name}
                            </Text>
                            <span
                              id={`${candidateEvidence}-${game.id}`}
                              className={classes.candidateText}
                            >
                              <Text
                                component="span"
                                size="sm"
                                c="dimmed"
                              >
                                {systemYears.length
                                  ? `${review.platformName} release · ${systemYears.join(', ')}`
                                  : `${candidatePlatforms.join(' / ') || 'Platform unknown'} · ${game.year ? `First released ${game.year}` : 'Year unknown'}`}
                              </Text>
                              {systemYears.length > 0 &&
                                game.year != null &&
                                !systemYears.includes(game.year) && (
                                  <Text
                                    component="span"
                                    size="xs"
                                    c="dimmed"
                                  >
                                    First released {game.year}
                                  </Text>
                                )}
                              {evidence?.conflicts.map((value) => (
                                <Text
                                  key={value}
                                  component="span"
                                  size="xs"
                                  c="orange"
                                >
                                  {value}
                                </Text>
                              ))}
                              {evidence?.evidence.map((value) => (
                                <Text
                                  key={value}
                                  component="span"
                                  size="xs"
                                  c="dimmed"
                                >
                                  {value}
                                </Text>
                              ))}
                            </span>
                          </span>
                        </span>
                      }
                    />
                    {game.url && (
                      <Anchor
                        className={classes.candidateLink}
                        href={game.url}
                        target="_blank"
                        rel="noopener noreferrer"
                        aria-label={`Open ${game.name} on ${row.providerName}`}
                      >
                        <IconExternalLink
                          size={16}
                          aria-hidden="true"
                        />
                      </Anchor>
                    )}
                  </div>
                );
              }
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
      {!stale && continueWithout && (
        <Button
          variant="subtle"
          size="compact-sm"
          w="fit-content"
          disabled={busy || conflicted}
          onClick={() => {
            save.reset();
            setContinuingWithout(true);
          }}
        >
          {continueWithout.confirmLabel}
        </Button>
      )}
      <Stack
        gap="xs"
        className={classes.actions}
        data-inline={(inline && !review) || undefined}
        data-review={!!review || undefined}
      >
        {stale && review && (
          <Alert
            color="yellow"
            title="This provider decision changed"
            role="status"
          >
            Show the latest status to clear this draft and review what changed.
          </Alert>
        )}
        {review && submitting === 'match' && (
          <Text
            role="status"
            size="sm"
            c="dimmed"
          >
            Saving provider decision…
          </Text>
        )}
        {!stale && save.isError && !continuingWithout && (
          <Alert color="red">{save.error.message}</Alert>
        )}
        {!stale && conflicted && !continuingWithout && (
          <Button
            {...workspaceActionProps}
            variant="light"
            loading={reload.isPending}
            onClick={() => (selected ? setReloading(true) : reload.mutate())}
          >
            Reload latest
          </Button>
        )}
        {!stale && reload.isError && <Alert color="red">{reload.error.message}</Alert>}
        {!stale && reload.isSuccess && (
          <Alert color="blue">
            Current match: {match.game?.name ?? match.externalId ?? 'No linked game'}. Select a game
            to confirm your choice.
          </Alert>
        )}
        {!review && (
          <Text
            size="sm"
            role="status"
          >
            {selected
              ? `Selected: ${selected.name}`
              : 'Select a provider game to confirm this match.'}
          </Text>
        )}
        <Group
          justify={review ? 'space-between' : 'end'}
          gap="xs"
        >
          <Button
            variant={review ? 'subtle' : 'default'}
            color={review ? 'gray' : undefined}
            className={review ? classes.skip : undefined}
            disabled={busy}
            loading={review?.loadingNextTitle}
            onClick={review?.onSkip ?? requestClose}
          >
            {review ? 'Skip for now' : 'Cancel'}
          </Button>
          {stale && review ? (
            <Button
              {...workspaceActionProps}
              disabled={busy}
              onClick={review.onShowLatest}
            >
              Show latest status
            </Button>
          ) : (
            <Button
              {...workspaceActionProps}
              disabled={!selected || conflicted || !match.isAvailable || busy || search.isPending}
              loading={submitting === 'match'}
              onClick={() => {
                if (selected)
                  void submit({
                    kind: 'match',
                    externalId: selected.id,
                  }).catch(() => undefined);
              }}
            >
              {review || !selected ? 'Confirm match' : `Confirm ${selected.name}`}
            </Button>
          )}
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
      {review ? (
        content
      ) : inline ? (
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
      {continueWithout && (
        <DecisionConfirmation
          copy={
            continuingWithout
              ? stale
                ? {
                    ...continueWithout,
                    message: 'This decision changed. Close this dialog to see the latest status.',
                  }
                : continueWithout
              : undefined
          }
          confirmDisabled={stale || conflicted}
          onCancel={() => setContinuingWithout(false)}
          onConfirm={() =>
            submit({
              kind: 'continue',
            })
          }
          onConfirmed={() => setContinuingWithout(false)}
          returnFocusAfterConfirm={false}
        />
      )}
    </>
  );
}

function MatchCover({ url }: { url?: string | null }) {
  return (
    <span className={classes.matchCover}>
      {url ? (
        <Image
          src={url}
          alt=""
          fit="cover"
          referrerPolicy="no-referrer"
        />
      ) : (
        <IconPhoto
          size={18}
          aria-hidden="true"
        />
      )}
    </span>
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
                      color={row.externalId ? 'red' : undefined}
                      leftSection={<IconUnlink size={16} />}
                      onClick={() => {
                        mutation.reset();
                        setUnlinking(row);
                      }}
                    >
                      {row.externalId ? 'Unlink' : 'Continue without this provider'}
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
          unlinking?.externalId
            ? `Unlink ${unlinking.providerName}`
            : `Continue without ${unlinking?.providerName}?`
        }
        size="sm"
      >
        <Stack>
          <Text size="sm">
            {unlinking?.externalId && 'ROMD retains metadata and artwork from the current match. '}
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
              color={unlinking?.externalId ? 'red' : undefined}
              loading={mutation.isPending}
              disabled={disabled || mutation.error instanceof ProviderMatchConflictError}
              onClick={() => unlinking && decide(unlinking, true, () => setUnlinking(null))}
            >
              {unlinking?.externalId ? 'Unlink match' : 'Continue without provider'}
            </Button>
          </Group>
        </Stack>
      </Modal>
    </div>
  );
}
