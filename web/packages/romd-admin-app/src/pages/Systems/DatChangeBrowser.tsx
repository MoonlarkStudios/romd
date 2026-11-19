import {
  ActionIcon,
  Alert,
  Anchor,
  Badge,
  Button,
  CloseButton,
  Group,
  Loader,
  Stack,
  Text,
  TextInput,
  Title,
  Tooltip,
} from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import type {
  DatEntryChange,
  DatEntryReviewImpact,
  DatReplacementPreview,
  ProblemDetails,
} from '@romd/admin-api-client';
import {
  getDatReplacementChange,
  getDatSubscriptionChange,
  listDatReplacementChanges,
  listDatSubscriptionChanges,
} from '@romd/admin-api-client';
import {
  IconAlertTriangle,
  IconArrowLeft,
  IconChevronLeft,
  IconChevronRight,
  IconCircleCheck,
  IconInfoCircle,
  IconSearch,
} from '@tabler/icons-react';
import { useQuery } from '@tanstack/react-query';
import { type ReactNode, useEffect, useId, useLayoutEffect, useRef, useState } from 'react';
import { Link } from 'react-router';
import classes from './DatReview.module.css';
import { DatReviewFields, DatReviewFileComparison } from './DatReviewFileComparison';
import {
  countLabel,
  type DatChangeFilter,
  defaultReviewFilter,
  type ReviewRailRow,
  reviewFilterLabels,
  reviewRail,
  reviewVerdict,
  totalChanges,
} from './datReviewSummary';

export type { DatChangeFilter } from './datReviewSummary';

const STALE_REVIEW_CODES = new Set([
  'DatEnrollment.Stale',
  'DatReview.Stale',
]);

class ReviewPageError extends Error {
  constructor(
    message: string,
    readonly stale: boolean,
  ) {
    super(message);
  }
}
function pageError(error: ProblemDetails | undefined): ReviewPageError {
  return new ReviewPageError(
    error?.detail ?? 'Could not load this page. Try again.',
    error != null && STALE_REVIEW_CODES.has(error.errorCode),
  );
}

/** Change type is neutral; warning color is reserved for catalog impact. */
function ChangeBadge({ change }: { change: string }) {
  return (
    <Badge
      size="sm"
      variant="light"
      color={change === 'Added' ? 'teal' : change === 'Changed' ? 'blue' : 'gray'}
    >
      {change}
    </Badge>
  );
}

function ImpactBadges({ impact }: { impact?: DatEntryReviewImpact | null }) {
  if (!impact) return null;
  return (
    <>
      {impact.titleLinkAtRisk && (
        <Badge
          component="span"
          size="sm"
          variant="light"
          color="yellow"
        >
          Unlinks title
        </Badge>
      )}
      {impact.lostRomMatches > 0 ? (
        <Badge
          component="span"
          size="sm"
          variant="light"
          color="yellow"
        >
          Loses ROM match
        </Badge>
      ) : impact.storedRoms > 0 ? (
        <Badge
          component="span"
          size="sm"
          variant="outline"
          color="gray"
        >
          Stored ROM
        </Badge>
      ) : null}
      {impact.sha1ChangedFiles > 0 && (
        <Badge
          component="span"
          size="sm"
          variant="light"
          color={impact.needsReview ? 'yellow' : 'blue'}
        >
          SHA-1 changed
        </Badge>
      )}
      {impact.newRomMatches > 0 && (
        <Badge
          component="span"
          size="sm"
          variant="light"
          color="teal"
        >
          Gains ROM match
        </Badge>
      )}
    </>
  );
}

function fileCountSummary(item: DatEntryChange) {
  if (item.change !== 'Changed') {
    const count = item.change === 'Added' ? item.filesAdded : item.filesRemoved;
    return countLabel(count, 'file');
  }
  return (
    [
      [
        item.filesChanged,
        'changed',
      ],
      [
        item.filesAdded,
        'added',
      ],
      [
        item.filesRemoved,
        'removed',
      ],
    ] as const
  )
    .filter(([count]) => count > 0)
    .map(([count, label]) => `${countLabel(count, 'file')} ${label}`)
    .join(' · ');
}

function PageError({ error, onRetry }: { error: Error; onRetry: () => void }) {
  return (
    <Alert
      color="red"
      title="Changes could not be loaded"
    >
      <Stack gap="xs">
        {error.message}
        <Button
          variant="light"
          size="compact-sm"
          w="fit-content"
          onClick={onRetry}
        >
          Retry page
        </Button>
      </Stack>
    </Alert>
  );
}

function PageLoading() {
  return (
    <Group
      role="status"
      gap="xs"
      p="sm"
    >
      <Loader size="xs" />
      <Text size="sm">Loading changes…</Text>
    </Group>
  );
}

const verdictIcons = {
  review: IconAlertTriangle,
  clear: IconCircleCheck,
  unknown: IconInfoCircle,
  none: IconInfoCircle,
};

function Verdict({ preview }: { preview: DatReplacementPreview }) {
  const verdict = reviewVerdict(preview);
  const Icon = verdictIcons[verdict.tone];
  return (
    <section
      className={classes.verdict}
      data-tone={verdict.tone}
      aria-label="Review verdict"
    >
      <Icon
        size={20}
        className={classes.verdictIcon}
        aria-hidden
      />
      <div>
        <Text
          fw={600}
          className={classes.verdictTitle}
        >
          {verdict.title}
        </Text>
        <Text size="sm">{verdict.detail}</Text>
      </div>
    </section>
  );
}

function RailRow({
  row,
  selected,
  onSelect,
}: {
  row: ReviewRailRow;
  selected?: boolean;
  onSelect: () => void;
}) {
  return (
    <button
      type="button"
      className={classes.railRow}
      data-tone={row.count > 0 ? row.tone : undefined}
      data-nested={row.nested || undefined}
      aria-pressed={selected}
      disabled={row.count === 0}
      onClick={onSelect}
    >
      <span className={classes.railLabel}>{reviewFilterLabels[row.filter]}</span>{' '}
      <span className={classes.railCount}>{row.count ? row.count.toLocaleString() : '—'}</span>
      {selected === undefined && row.count > 0 && (
        <IconChevronRight
          size={15}
          aria-hidden
          className={classes.railChevron}
        />
      )}
    </button>
  );
}

/**
 * Admin sessions are per tab, so the title opens in this tab. Only a review the URL can restore
 * links out; an upload's bytes would be lost by leaving the page.
 */
function TitleImpact({
  impact,
  linkTitle,
}: {
  impact?: DatEntryReviewImpact | null;
  linkTitle: boolean;
}) {
  if (!impact?.titleLink) return null;
  const title =
    impact.titleId && linkTitle ? (
      <Anchor
        component={Link}
        to={`/titles/${impact.titleId}`}
        inherit
        fw={600}
      >
        {impact.titleLink}
      </Anchor>
    ) : (
      <Text
        span
        inherit
        fw={600}
      >
        {impact.titleLink}
      </Text>
    );
  if (!impact.titleLinkAtRisk)
    return (
      <Text
        size="sm"
        c="dimmed"
      >
        Linked to title {title}
      </Text>
    );
  return (
    <div className={classes.titleImpact}>
      <Text size="sm">Unlinks from title {title}</Text>
      <Text
        size="sm"
        c="dimmed"
      >
        {impact.titleOtherSourceLinks
          ? `The title keeps ${countLabel(impact.titleOtherSourceLinks, 'other source link')}.`
          : 'The title has no other sources. ROMD removes a title without sources unless it has your curation, such as edits, artwork, or collection entries.'}
      </Text>
    </div>
  );
}

/** The nearest scrolling ancestor, so Back can restore the reader's place in either a dialog or a page. */
function scrollContainer(element: HTMLElement | null): Element | null {
  let node = element?.parentElement ?? null;
  while (node) {
    const overflow = getComputedStyle(node).overflowY;
    if ((overflow === 'auto' || overflow === 'scroll') && node.scrollHeight > node.clientHeight)
      return node;
    node = node.parentElement;
  }
  return document.scrollingElement;
}

type CursorPosition = {
  cursor: string;
  offset: number;
};

export function DatChangeBrowser({
  datId,
  subscriptionId,
  file,
  preview,
  header,
  footnotes,
  linkTitles = false,
  onStale,
}: {
  datId: string;
  subscriptionId?: string;
  file?: File;
  preview: DatReplacementPreview;
  header?: ReactNode;
  footnotes?: ReactNode;
  /** True only where Back restores the review, so following a title link loses nothing. */
  linkTitles?: boolean;
  onStale: (message: string) => void;
}) {
  const reviewId = useId();
  const compact = useMediaQuery('(max-width: 64em)') ?? false;
  const browser = useRef<HTMLDivElement>(null);
  const resultsHeading = useRef<HTMLHeadingElement>(null);
  const detailHeading = useRef<HTMLHeadingElement>(null);
  const listBody = useRef<HTMLDivElement>(null);
  const selectedButton = useRef<HTMLButtonElement>(null);
  const lastSelectedId = useRef<string>(undefined);
  const listScroll = useRef(0);
  const focusTarget = useRef<'list' | 'entry' | 'restore' | 'summary'>(null);
  const [showList, setShowList] = useState(false);
  const [draft, setDraft] = useState('');
  const [search, setSearch] = useState('');
  const [change, setChange] = useState<DatChangeFilter>(defaultReviewFilter(preview));
  const [entryCursors, setEntryCursors] = useState<CursorPosition[]>([]);
  const [entry, setEntry] = useState<DatEntryChange>();
  const [entryAtPageEdge, setEntryAtPageEdge] = useState<'first' | 'last'>();
  const [fileCursors, setFileCursors] = useState<string[]>([]);
  const summaryVisible = !compact || !showList;
  const listVisible = !compact || (showList && entry == null);
  const detailVisible = !compact || entry != null;
  const active = preview.activeSha256;
  const incoming = preview.candidateSha256;
  const pins = {
    activeSha256: active,
    candidateSha256: incoming,
  };
  const position = entryCursors.at(-1);
  const hasChanges = totalChanges(preview) > 0;
  const rail = reviewRail(preview);

  const entries = useQuery({
    queryKey: [
      'dat-review-changes',
      reviewId,
      datId,
      subscriptionId,
      active,
      incoming,
      position?.cursor,
      search,
      change,
    ],
    enabled: (!compact || showList) && hasChanges,
    retry: false,
    refetchOnWindowFocus: false,
    staleTime: Infinity,
    queryFn: async ({ signal }) => {
      const page = {
        ...pins,
        cursor: position?.cursor,
        query: search || undefined,
        change: change === 'All' ? undefined : change,
      };
      const response = subscriptionId
        ? await listDatSubscriptionChanges({
            path: {
              subscriptionId,
            },
            query: page,
            signal,
          })
        : await listDatReplacementChanges({
            path: {
              datId,
            },
            body: {
              ...page,
              file,
            },
            signal,
          });
      if (!response.data) throw pageError(response.error);
      return response.data;
    },
  });
  const selectedId = entry?.id;
  const files = useQuery({
    queryKey: [
      'dat-review-entry-changes',
      reviewId,
      datId,
      subscriptionId,
      active,
      incoming,
      selectedId,
      fileCursors.at(-1),
    ],
    enabled: selectedId != null,
    retry: false,
    refetchOnWindowFocus: false,
    queryFn: async ({ signal }) => {
      const entryId = selectedId ?? '';
      const page = {
        ...pins,
        cursor: fileCursors.at(-1),
      };
      const response = subscriptionId
        ? await getDatSubscriptionChange({
            path: {
              subscriptionId,
              entryId,
            },
            query: page,
            signal,
          })
        : await getDatReplacementChange({
            path: {
              datId,
              entryId,
            },
            body: {
              ...page,
              file,
            },
            signal,
          });
      if (!response.data) throw pageError(response.error);
      if (response.data.activeSha256 !== active || response.data.candidateSha256 !== incoming)
        throw new ReviewPageError('The reviewed documents changed. Request a new preview.', true);
      return response.data;
    },
  });
  const page = entries.data;
  const detail = files.data;
  const queryError = entries.error ?? files.error;
  useEffect(() => {
    if (queryError instanceof ReviewPageError && queryError.stale) onStale(queryError.message);
  }, [
    queryError,
    onStale,
  ]);

  useEffect(() => {
    if (!page || !entryAtPageEdge) return;
    focusTarget.current = 'entry';
    const next = entryAtPageEdge === 'first' ? page.items[0] : page.items.at(-1);
    lastSelectedId.current = next?.id;
    setEntry(next);
    setFileCursors([]);
    setEntryAtPageEdge(undefined);
  }, [
    page,
    entryAtPageEdge,
  ]);

  // The list remains mounted while inspecting an entry so its query and controls survive Back.
  useLayoutEffect(() => {
    const target = focusTarget.current;
    focusTarget.current = null;
    if (target === 'entry') {
      detailHeading.current?.focus();
    } else if (target === 'summary') {
      browser.current?.focus();
      browser.current?.scrollIntoView?.({
        block: 'start',
      });
    } else if (target === 'restore') {
      selectedButton.current?.focus({
        preventScroll: true,
      });
      const viewport = scrollContainer(browser.current);
      if (viewport) viewport.scrollTop = listScroll.current;
    } else if (target === 'list') {
      resultsHeading.current?.focus();
    }
  });

  function select(item: DatEntryChange) {
    if (!entry) listScroll.current = scrollContainer(browser.current)?.scrollTop ?? 0;
    focusTarget.current = 'entry';
    lastSelectedId.current = item.id;
    setShowList(true);
    setEntry(item);
    setFileCursors([]);
  }

  function filter(next: DatChangeFilter) {
    setChange(next);
    setShowList(true);
    setEntry(undefined);
    setEntryAtPageEdge(undefined);
    setEntryCursors([]);
    focusTarget.current = 'list';
    if (listBody.current) listBody.current.scrollTop = 0;
  }

  function changePage(direction: -1 | 1, keepEntry = false) {
    if (!page) return;
    if (direction === 1) {
      const cursor = page.nextCursor;
      if (!cursor) return;
      setEntryCursors([
        ...entryCursors,
        {
          cursor,
          offset: (position?.offset ?? 0) + page.items.length,
        },
      ]);
    } else {
      if (!position) return;
      setEntryCursors(entryCursors.slice(0, -1));
    }
    if (keepEntry) {
      setEntryAtPageEdge(direction === 1 ? 'first' : 'last');
    } else {
      setEntry(undefined);
      focusTarget.current = 'list';
      if (listBody.current) listBody.current.scrollTop = 0;
    }
  }

  function submitSearch(value: string) {
    setSearch(value.trim());
    setEntry(undefined);
    setEntryCursors([]);
    setEntryAtPageEdge(undefined);
    if (listBody.current) listBody.current.scrollTop = 0;
  }

  const selectedIndex = page?.items.findIndex((item) => item.id === selectedId) ?? -1;
  function adjacent(direction: -1 | 1) {
    const item = page?.items[selectedIndex + direction];
    if (item) select(item);
    else changePage(direction, true);
  }
  const offset = position?.offset ?? 0;
  const filterLabel = reviewFilterLabels[change];
  const paged = !!position || !!page?.hasNextPage;

  return (
    <Stack
      gap="md"
      ref={browser}
      tabIndex={-1}
      className={classes.focusTarget}
    >
      {summaryVisible && (
        <>
          {header}
          <Verdict preview={preview} />
        </>
      )}
      {hasChanges && (
        <div className={classes.inspector}>
          {summaryVisible && (
            <nav
              className={classes.rail}
              aria-label="Review filters"
            >
              {rail.map((group) => (
                <section
                  key={group.label}
                  className={classes.railGroup}
                  aria-label={group.label}
                >
                  <p
                    className={classes.railHeading}
                    aria-hidden
                  >
                    {group.label}
                  </p>
                  {group.rows.map((row) => (
                    <RailRow
                      key={row.filter}
                      row={row}
                      selected={compact ? undefined : row.filter === change}
                      onSelect={() => filter(row.filter)}
                    />
                  ))}
                </section>
              ))}
            </nav>
          )}
          {(!compact || showList) && (
            <>
              <section
                className={classes.listPane}
                hidden={!listVisible}
                aria-label="Changed entries"
              >
                <div className={classes.listControls}>
                  {compact && (
                    <Button
                      variant="subtle"
                      size="compact-sm"
                      px={0}
                      w="fit-content"
                      leftSection={<IconArrowLeft size={15} />}
                      onClick={() => {
                        focusTarget.current = 'summary';
                        setShowList(false);
                      }}
                    >
                      Back to summary
                    </Button>
                  )}
                  <Group
                    justify="space-between"
                    align="baseline"
                    gap="xs"
                    wrap="nowrap"
                  >
                    <Title
                      order={3}
                      size="h5"
                      tabIndex={-1}
                      ref={resultsHeading}
                      className={classes.focusTarget}
                    >
                      {filterLabel}
                    </Title>
                    {page && (
                      <Text
                        size="xs"
                        c="dimmed"
                      >
                        {countLabel(
                          page.total,
                          search ? 'match' : 'entry',
                          search ? 'matches' : 'entries',
                        )}
                      </Text>
                    )}
                  </Group>
                  <form
                    onSubmit={(event) => {
                      event.preventDefault();
                      submitSearch(draft);
                    }}
                  >
                    <TextInput
                      aria-label="Search changed entries"
                      placeholder="Search entries, then press Enter"
                      value={draft}
                      onChange={(event) => {
                        setDraft(event.currentTarget.value);
                        if (!event.currentTarget.value && search) submitSearch('');
                      }}
                      maxLength={200}
                      leftSection={<IconSearch size={15} />}
                      rightSection={
                        draft ? (
                          <CloseButton
                            size="sm"
                            aria-label="Clear search"
                            onClick={() => {
                              setDraft('');
                              if (search) submitSearch('');
                            }}
                          />
                        ) : null
                      }
                    />
                  </form>
                </div>
                <div
                  className={classes.listBody}
                  ref={listBody}
                >
                  {entries.isFetching && <PageLoading />}
                  {entries.error && (
                    <PageError
                      error={entries.error}
                      onRetry={() => void entries.refetch()}
                    />
                  )}
                  {page?.items.length === 0 && (
                    <Text
                      size="sm"
                      c="dimmed"
                      p="sm"
                    >
                      No changes match these filters.
                    </Text>
                  )}
                  <ul className={classes.changeList}>
                    {page?.items.map((item) => (
                      <li key={item.id}>
                        <button
                          type="button"
                          className={classes.entryButton}
                          aria-pressed={item.id === selectedId}
                          ref={item.id === lastSelectedId.current ? selectedButton : undefined}
                          onClick={() => select(item)}
                        >
                          <Text
                            span
                            fw={600}
                            size="sm"
                            className={classes.fieldValue}
                          >
                            {item.name}
                          </Text>
                          <Group
                            component="span"
                            gap={6}
                            wrap="wrap"
                          >
                            <ChangeBadge change={item.change} />
                            <ImpactBadges impact={item.impact} />
                            <Text
                              span
                              size="xs"
                              c="dimmed"
                            >
                              {fileCountSummary(item)}
                            </Text>
                          </Group>
                        </button>
                      </li>
                    ))}
                  </ul>
                </div>
                {page && paged && (
                  <Group
                    justify="space-between"
                    className={classes.paging}
                    aria-label="Change pages"
                  >
                    <Text
                      size="xs"
                      c="dimmed"
                      role="status"
                    >
                      {(offset + 1).toLocaleString()}–
                      {(offset + page.items.length).toLocaleString()} of{' '}
                      {page.total.toLocaleString()}
                    </Text>
                    <Group gap={6}>
                      <Tooltip label="Previous page">
                        <ActionIcon
                          aria-label="Previous changes"
                          variant="default"
                          disabled={!position}
                          onClick={() => changePage(-1)}
                        >
                          <IconChevronLeft size={16} />
                        </ActionIcon>
                      </Tooltip>
                      <Tooltip label="Next page">
                        <ActionIcon
                          aria-label="Next changes"
                          variant="default"
                          disabled={!page.hasNextPage}
                          onClick={() => changePage(1)}
                        >
                          <IconChevronRight size={16} />
                        </ActionIcon>
                      </Tooltip>
                    </Group>
                  </Group>
                )}
              </section>
              <section
                className={classes.detailPane}
                hidden={!detailVisible}
                aria-label="Entry changes"
              >
                {entry ? (
                  <Stack gap="sm">
                    {compact && (
                      <Button
                        variant="subtle"
                        size="compact-sm"
                        w="fit-content"
                        px={0}
                        leftSection={<IconArrowLeft size={15} />}
                        onClick={() => {
                          focusTarget.current = 'restore';
                          setEntry(undefined);
                          setEntryAtPageEdge(undefined);
                        }}
                      >
                        Back to {change === 'All' ? 'all changes' : filterLabel.toLowerCase()}
                      </Button>
                    )}
                    <div>
                      <Group gap={6}>
                        <ChangeBadge change={entry.change} />
                        {compact && page && page.total > 1 && selectedIndex >= 0 && (
                          <Text
                            size="xs"
                            c="dimmed"
                          >
                            {(offset + selectedIndex + 1).toLocaleString()} of{' '}
                            {page.total.toLocaleString()}
                          </Text>
                        )}
                      </Group>
                      <Title
                        order={3}
                        size="h5"
                        tabIndex={-1}
                        mt={6}
                        ref={detailHeading}
                        className={`${classes.fieldValue} ${classes.focusTarget}`}
                      >
                        {entry.name}
                      </Title>
                    </div>
                    <TitleImpact
                      impact={detail?.impact ?? entry.impact}
                      linkTitle={linkTitles}
                    />
                    {entries.error && entryAtPageEdge && (
                      <PageError
                        error={entries.error}
                        onRetry={() => void entries.refetch()}
                      />
                    )}
                    {(files.isFetching || (entryAtPageEdge && entries.isFetching)) && (
                      <PageLoading />
                    )}
                    {files.error && (
                      <PageError
                        error={files.error}
                        onRetry={() => void files.refetch()}
                      />
                    )}
                    {detail && (
                      <>
                        {detail.files.items.length ? (
                          <ul
                            className={classes.fileList}
                            aria-label="Changed files"
                          >
                            {detail.files.items.map((item) => (
                              <DatReviewFileComparison
                                key={`${item.kind}:${item.name}`}
                                file={item}
                              />
                            ))}
                          </ul>
                        ) : (
                          <Text
                            size="sm"
                            c="dimmed"
                          >
                            No file changes.
                          </Text>
                        )}
                        {(fileCursors.length > 0 || detail.files.hasNextPage) && (
                          <Group
                            justify="space-between"
                            aria-label="File change pages"
                          >
                            <Button
                              size="compact-sm"
                              variant="default"
                              disabled={!fileCursors.length}
                              onClick={() => setFileCursors(fileCursors.slice(0, -1))}
                            >
                              Previous files
                            </Button>
                            <Text
                              size="xs"
                              c="dimmed"
                            >
                              Files · page {fileCursors.length + 1}
                            </Text>
                            <Button
                              size="compact-sm"
                              variant="default"
                              disabled={!detail.files.hasNextPage}
                              onClick={() => {
                                const cursor = detail.files.nextCursor;
                                if (cursor)
                                  setFileCursors([
                                    ...fileCursors,
                                    cursor,
                                  ]);
                              }}
                            >
                              Next files
                            </Button>
                          </Group>
                        )}
                        {detail.fields.length > 0 && (
                          <details className={classes.disclosure}>
                            <summary>Entry metadata</summary>
                            <DatReviewFields fields={detail.fields} />
                          </details>
                        )}
                      </>
                    )}
                    {page && page.total > 1 && (
                      <Group
                        justify="space-between"
                        aria-label="Navigate entries"
                        className={classes.entryNavigation}
                      >
                        <Button
                          variant="default"
                          size="compact-sm"
                          leftSection={<IconChevronLeft size={15} />}
                          disabled={entryAtPageEdge != null || (selectedIndex <= 0 && !position)}
                          onClick={() => adjacent(-1)}
                        >
                          Previous entry
                        </Button>
                        <Button
                          variant="default"
                          size="compact-sm"
                          rightSection={<IconChevronRight size={15} />}
                          disabled={
                            entryAtPageEdge != null ||
                            (selectedIndex >= (page.items.length ?? 1) - 1 && !page.hasNextPage)
                          }
                          onClick={() => adjacent(1)}
                        >
                          Next entry
                        </Button>
                      </Group>
                    )}
                  </Stack>
                ) : (
                  <Text
                    size="sm"
                    c="dimmed"
                    className={classes.detailEmpty}
                  >
                    Select an entry to see its files, checksums, stored ROM matches, and title link.
                  </Text>
                )}
              </section>
            </>
          )}
        </div>
      )}
      {summaryVisible && footnotes}
    </Stack>
  );
}
