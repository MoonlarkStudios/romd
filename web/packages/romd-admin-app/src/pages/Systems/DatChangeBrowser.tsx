import {
  ActionIcon,
  Alert,
  Badge,
  Button,
  Group,
  Loader,
  Stack,
  Text,
  TextInput,
  Title,
  Tooltip,
} from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import type { DatEntryChange, DatReplacementPreview, ProblemDetails } from '@romd/admin-api-client';
import {
  getDatReplacementChange,
  getDatSubscriptionChange,
  listDatReplacementChanges,
  listDatSubscriptionChanges,
} from '@romd/admin-api-client';
import { IconArrowLeft, IconChevronLeft, IconChevronRight, IconSearch } from '@tabler/icons-react';
import { useQuery } from '@tanstack/react-query';
import { type ReactNode, useEffect, useId, useLayoutEffect, useRef, useState } from 'react';
import classes from './DatReview.module.css';
import { DatReviewFields, DatReviewFileComparison } from './DatReviewFileComparison';

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

function ChangeBadge({ change }: { change: string }) {
  return (
    <Badge
      size="sm"
      color={change === 'Removed' ? 'yellow' : change === 'Added' ? 'teal' : 'blue'}
    >
      {change}
    </Badge>
  );
}

const filterLabels = {
  NeedsReview: 'Needs review',
  Removed: 'Removed',
  RemovedStored: 'Removals with stored ROMs',
  RemovedLinked: 'Removals with title links',
  Sha1Changed: 'SHA-1 changed',
  LostMatches: 'Losing ROM matches',
  NewMatches: 'Gaining ROM matches',
  Added: 'Added',
  OtherChecksumOnly: 'CRC/MD5 only',
  MetadataOnly: 'Metadata only',
  Changed: 'Changed',
  All: 'All',
} as const;

export type DatChangeFilter = keyof typeof filterLabels;
type CursorPosition = {
  cursor: string;
  offset: number;
};

function filterChips(preview: DatReplacementPreview, filter: DatChangeFilter) {
  const impact = preview.impact;
  const all = preview.entriesAdded + preview.entriesRemoved + preview.entriesChanged;
  const filters: [
    DatChangeFilter,
    number | undefined,
  ][] = [
    [
      'NeedsReview',
      impact?.needsReviewEntries,
    ],
    [
      'Removed',
      preview.entriesRemoved,
    ],
    [
      'Sha1Changed',
      impact?.sha1ChangedEntries,
    ],
    [
      'Added',
      preview.entriesAdded,
    ],
    [
      'All',
      all,
    ],
  ];
  if (filter !== 'All' && !filters.some(([key]) => key === filter)) {
    const counts: Partial<Record<DatChangeFilter, number>> = {
      RemovedStored: impact?.removedWithStoredRoms,
      RemovedLinked: impact?.removedWithTitleLinks,
      OtherChecksumOnly: impact?.otherChecksumOnlyEntries,
      MetadataOnly: impact?.metadataOnlyEntries,
      Changed: preview.entriesChanged,
    };
    filters.unshift([
      filter,
      counts[filter],
    ]);
  }
  return filters.filter(([key, count]) => key === filter || count == null || count > 0);
}

function fileCountSummary(item: DatEntryChange) {
  const counts = [
    [
      item.filesAdded,
      'added',
    ],
    [
      item.filesRemoved,
      'removed',
    ],
    [
      item.filesChanged,
      'changed',
    ],
  ] as const;
  return counts
    .filter(([count]) => count > 0)
    .map(([count, label]) => `${count.toLocaleString()} ${count === 1 ? 'file' : 'files'} ${label}`)
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
    >
      <Loader size="xs" />
      <Text size="sm">Loading changes…</Text>
    </Group>
  );
}

export function DatChangeBrowser({
  datId,
  subscriptionId,
  file,
  preview,
  renderSummary,
  onStale,
}: {
  datId: string;
  subscriptionId?: string;
  file?: File;
  preview: DatReplacementPreview;
  renderSummary?: (onViewChanges: (change: DatChangeFilter) => void) => ReactNode;
  onStale: (message: string) => void;
}) {
  const reviewId = useId();
  const compact = useMediaQuery('(max-width: 64em)');
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
  const [change, setChange] = useState<DatChangeFilter>(
    preview.impact?.needsReviewEntries ? 'NeedsReview' : 'All',
  );
  const [entryCursors, setEntryCursors] = useState<CursorPosition[]>([]);
  const [entry, setEntry] = useState<DatEntryChange>();
  const [entryAtPageEdge, setEntryAtPageEdge] = useState<'first' | 'last'>();
  const [fileCursors, setFileCursors] = useState<string[]>([]);
  const listVisible = !compact || (showList && entry == null);
  const detailVisible = !compact || entry != null;
  const active = preview.activeSha256;
  const incoming = preview.candidateSha256;
  const pins = {
    activeSha256: active,
    candidateSha256: incoming,
  };
  const position = entryCursors.at(-1);
  const hasChanges = preview.entriesAdded + preview.entriesRemoved + preview.entriesChanged > 0;

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
      const viewport = browser.current?.closest(`.${classes.content}`);
      if (viewport) viewport.scrollTop = listScroll.current;
    } else if (target === 'list') {
      resultsHeading.current?.focus();
    }
  });

  function select(item: DatEntryChange) {
    if (!entry)
      listScroll.current = browser.current?.closest(`.${classes.content}`)?.scrollTop ?? 0;
    focusTarget.current = 'entry';
    lastSelectedId.current = item.id;
    setShowList(true);
    setEntry(item);
    setFileCursors([]);
  }

  function filter(next: DatChangeFilter, fromSummary = false) {
    if (fromSummary) {
      setDraft('');
      setSearch('');
    }
    setChange(next);
    setShowList(true);
    setEntry(undefined);
    setEntryAtPageEdge(undefined);
    setEntryCursors([]);
    focusTarget.current = 'list';
    resultsHeading.current?.focus();
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
      resultsHeading.current?.focus();
      if (listBody.current) listBody.current.scrollTop = 0;
    }
  }

  const selectedIndex = page?.items.findIndex((item) => item.id === selectedId) ?? -1;
  function adjacent(direction: -1 | 1) {
    const item = page?.items[selectedIndex + direction];
    if (item) select(item);
    else changePage(direction, true);
  }
  const offset = position?.offset ?? 0;

  return (
    <Stack
      gap="md"
      ref={browser}
      tabIndex={-1}
    >
      {(!compact || !showList) && renderSummary?.((next) => filter(next, true))}
      {hasChanges && (
        <div
          className={classes.inspector}
          hidden={compact && !showList}
        >
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
                  leftSection={<IconArrowLeft size={15} />}
                  onClick={() => {
                    focusTarget.current = 'summary';
                    setShowList(false);
                  }}
                >
                  Back to summary
                </Button>
              )}
              <Title
                order={3}
                size="h5"
                tabIndex={-1}
                ref={resultsHeading}
              >
                {change === 'All' ? 'Explore all changes' : filterLabels[change]}
              </Title>
              <form
                className={classes.searchControls}
                onSubmit={(event) => {
                  event.preventDefault();
                  setSearch(draft.trim());
                  setEntry(undefined);
                  setEntryCursors([]);
                  setEntryAtPageEdge(undefined);
                  if (listBody.current) listBody.current.scrollTop = 0;
                }}
              >
                <TextInput
                  aria-label="Search changed entries"
                  placeholder="Search entries…"
                  value={draft}
                  onChange={(event) => setDraft(event.currentTarget.value)}
                  maxLength={200}
                  leftSection={<IconSearch size={15} />}
                />
                <Button
                  type="submit"
                  variant="default"
                >
                  Search
                </Button>
              </form>
              <section
                className={classes.filters}
                aria-label="Filter changed entries"
              >
                {filterChips(preview, change).map(([key, count]) => (
                  <Button
                    key={key}
                    size="compact-sm"
                    variant={key === change ? 'light' : 'default'}
                    aria-pressed={key === change}
                    onClick={() => filter(key)}
                  >
                    {filterLabels[key]}
                    {count == null ? '' : ` ${count.toLocaleString()}`}
                  </Button>
                ))}
              </section>
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
                        {item.impact?.storedRoms ? (
                          <Badge
                            component="span"
                            size="sm"
                            color="gray"
                          >
                            Stored
                          </Badge>
                        ) : null}
                        {item.impact?.sha1ChangedFiles ? (
                          <Badge
                            component="span"
                            size="sm"
                            color="yellow"
                          >
                            SHA-1 changed
                          </Badge>
                        ) : null}
                        {item.impact?.titleLinkAtRisk && (
                          <Badge
                            component="span"
                            size="sm"
                            color="yellow"
                          >
                            Title link at risk
                          </Badge>
                        )}
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
            {page && page.total > 0 && (
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
                  {(offset + 1).toLocaleString()}–{(offset + page.items.length).toLocaleString()} of{' '}
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
                    Back to {change === 'All' ? 'all changes' : filterLabels[change].toLowerCase()}
                  </Button>
                )}
                <div>
                  <ChangeBadge change={entry.change} />
                  <Title
                    order={3}
                    size="h5"
                    tabIndex={-1}
                    mt={6}
                    ref={detailHeading}
                    className={classes.fieldValue}
                  >
                    {entry.name}
                  </Title>
                </div>
                {detail?.impact?.titleLink && (
                  <Text
                    size="sm"
                    className={detail.impact.titleLinkAtRisk ? classes.priorityText : undefined}
                  >
                    Title link · {detail.impact.titleLink}
                    {detail.impact.titleLinkAtRisk ? ' · may be removed' : ''}
                  </Text>
                )}
                {entries.error && entryAtPageEdge && (
                  <PageError
                    error={entries.error}
                    onRetry={() => void entries.refetch()}
                  />
                )}
                {(files.isFetching || (entryAtPageEdge && entries.isFetching)) && <PageLoading />}
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
                <Group
                  justify="space-between"
                  aria-label="Navigate entries"
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
                      (selectedIndex >= (page?.items.length ?? 1) - 1 && !page?.hasNextPage)
                    }
                    onClick={() => adjacent(1)}
                  >
                    Next entry
                  </Button>
                </Group>
              </Stack>
            ) : (
              <Text
                size="sm"
                c="dimmed"
              >
                Select an entry to inspect its files, checksums, and title link.
              </Text>
            )}
          </section>
        </div>
      )}
    </Stack>
  );
}
