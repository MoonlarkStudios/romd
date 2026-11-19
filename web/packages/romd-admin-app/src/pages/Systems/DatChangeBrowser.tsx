import {
  Alert,
  Badge,
  Box,
  Button,
  Group,
  Loader,
  NativeSelect,
  Stack,
  Text,
  TextInput,
  Title,
} from '@mantine/core';
import type {
  DatEntryChange,
  DatFieldChange,
  DatReplacementPreview,
  ProblemDetails,
} from '@romd/admin-api-client';
import {
  getDatReplacementChange,
  getDatSubscriptionChange,
  listDatReplacementChanges,
  listDatSubscriptionChanges,
} from '@romd/admin-api-client';
import { useQuery } from '@tanstack/react-query';
import { type ReactNode, useEffect, useRef, useState } from 'react';
import classes from './DatReview.module.css';

/** Only a stale snapshot binding invalidates the reviewed pair; other conflicts keep their own message. */
const STALE_REVIEW_CODES: ReadonlySet<string> = new Set([
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
    error?.detail ?? 'Could not load this page. Your installed catalog is unchanged. Try again.',
    error !== undefined && STALE_REVIEW_CODES.has(error.errorCode),
  );
}
function Fields({ fields }: { fields: DatFieldChange[] }) {
  return (
    <ul className={classes.fieldList}>
      {fields.map((field) => (
        <li
          key={field.field}
          className={classes.fieldRow}
        >
          <Text
            size="sm"
            fw={600}
          >
            {field.field}
          </Text>
          <div>
            <Text
              size="xs"
              c="dimmed"
            >
              Before
            </Text>
            <Text
              size="sm"
              className={classes.fieldValue}
            >
              {field.before ?? '—'}
            </Text>
          </div>
          <div>
            <Text
              size="xs"
              c="dimmed"
            >
              After
            </Text>
            <Text
              size="sm"
              className={classes.fieldValue}
            >
              {field.after ?? '—'}
            </Text>
          </div>
        </li>
      ))}
    </ul>
  );
}
function Pager({
  label,
  page,
  hasNextPage,
  onPrevious,
  onNext,
}: {
  label: string;
  page: number;
  hasNextPage: boolean;
  onPrevious: () => void;
  onNext: () => void;
}) {
  if (page === 0 && !hasNextPage) return null;
  return (
    <Group
      justify="space-between"
      aria-label={label}
    >
      <Button
        size="compact-sm"
        variant="default"
        disabled={page === 0}
        onClick={onPrevious}
      >
        Previous changes
      </Button>
      <Text
        size="sm"
        role="status"
      >
        Page {page + 1}
      </Text>
      <Button
        size="compact-sm"
        variant="default"
        disabled={!hasNextPage}
        onClick={onNext}
      >
        Next changes
      </Button>
    </Group>
  );
}
export type DatChangeFilter = 'Added' | 'Removed' | 'Changed';

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
  const resultsHeading = useRef<HTMLHeadingElement>(null);
  const resetScroll = () =>
    resultsHeading.current?.scrollIntoView?.({
      block: 'nearest',
    });
  const [draft, setDraft] = useState('');
  const [search, setSearch] = useState('');
  const [change, setChange] = useState('');
  // Keyset pages: each stack holds the cursors that reached the current page, so Previous pops one.
  const [entryCursors, setEntryCursors] = useState<string[]>([]);
  const [entry, setEntry] = useState<DatEntryChange>();
  const [fileCursors, setFileCursors] = useState<string[]>([]);
  const pins = {
    activeSha256: preview.activeSha256,
    candidateSha256: preview.candidateSha256,
  };
  const entries = useQuery({
    queryKey: [
      'dat-review-changes',
      datId,
      subscriptionId,
      preview.activeSha256,
      preview.candidateSha256,
      entryCursors.at(-1),
      search,
      change,
    ],
    enabled: entry == null,
    retry: false,
    refetchOnWindowFocus: false,
    queryFn: async ({ signal }) => {
      const page = {
        ...pins,
        cursor: entryCursors.at(-1),
        query: search || undefined,
        change: change || undefined,
      };
      // A saved subscription reads its retained candidate; a manual review resends its file.
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
  const files = useQuery({
    queryKey: [
      'dat-review-entry-changes',
      datId,
      subscriptionId,
      preview.activeSha256,
      preview.candidateSha256,
      entry?.id,
      fileCursors.at(-1),
    ],
    enabled: entry != null,
    retry: false,
    refetchOnWindowFocus: false,
    queryFn: async ({ signal }) => {
      const entryId = entry?.id ?? '';
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
      if (
        response.data.activeSha256 !== preview.activeSha256 ||
        response.data.candidateSha256 !== preview.candidateSha256
      )
        throw new ReviewPageError('The reviewed documents changed. Request a new preview.', true);
      return response.data;
    },
  });
  const query = entry == null ? entries : files;
  useEffect(() => {
    if (query.error instanceof ReviewPageError && query.error.stale) onStale(query.error.message);
  }, [
    query.error,
    onStale,
  ]);
  const page = entries.data;
  const detail = files.data;
  const viewChanges = (filter: DatChangeFilter) => {
    setChange(filter);
    setDraft('');
    setSearch('');
    setEntry(undefined);
    setEntryCursors([]);
    resultsHeading.current?.focus();
  };
  return (
    <Stack gap="sm">
      {renderSummary?.(viewChanges)}
      <Title
        order={3}
        size="h5"
        tabIndex={-1}
        ref={resultsHeading}
      >
        Explore all changes
      </Title>
      {entry == null ? (
        <form
          onSubmit={(event) => {
            event.preventDefault();
            setSearch(draft.trim());
            setEntryCursors([]);
            resetScroll();
          }}
        >
          <div className={classes.searchControls}>
            <TextInput
              label="Search changed entries"
              value={draft}
              onChange={(event) => setDraft(event.currentTarget.value)}
              maxLength={200}
            />
            <Button type="submit">Search</Button>
            <NativeSelect
              label="Change type"
              className={classes.changeFilter}
              value={change}
              onChange={(event) => {
                setChange(event.currentTarget.value);
                setEntryCursors([]);
                resetScroll();
              }}
              data={[
                {
                  value: '',
                  label: 'All changes',
                },
                {
                  value: 'Added',
                  label: 'Added',
                },
                {
                  value: 'Removed',
                  label: 'Removed',
                },
                {
                  value: 'Changed',
                  label: 'Changed',
                },
              ]}
            />
          </div>
        </form>
      ) : (
        <>
          <Button
            variant="subtle"
            w="fit-content"
            onClick={() => {
              setEntry(undefined);
              resetScroll();
            }}
          >
            Back to changed entries
          </Button>
          <Text fw={600}>Changes in {entry.name}</Text>
        </>
      )}
      {query.isFetching && (
        <Group role="status">
          <Loader size="sm" />
          <Text>Loading verified changes…</Text>
        </Group>
      )}
      {query.error && (
        <Alert
          color="red"
          title="Review needs attention"
        >
          {query.error.message}
          <Button
            variant="light"
            onClick={() => void query.refetch()}
          >
            Retry page
          </Button>
        </Alert>
      )}
      {!query.isFetching && !query.error && (
        <>
          {entry != null && detail && detail.fields.length > 0 && (
            <>
              <Text fw={500}>Entry metadata</Text>
              <Fields fields={detail.fields} />
            </>
          )}
          {entry == null && page?.items.length === 0 && (
            <Text>No changes match these filters.</Text>
          )}
          {entry != null && detail?.files.items.length === 0 && (
            <Text>No file changes. Entry metadata may have changed.</Text>
          )}
          {entry == null && page && page.items.length > 0 && (
            <Box
              component="ul"
              className={classes.changeList}
              aria-label="Changed entries"
            >
              {page.items.map((item) => (
                <li
                  key={item.id}
                  className={classes.changeRow}
                >
                  <Group
                    justify="space-between"
                    gap="xs"
                    align="flex-start"
                  >
                    <Button
                      variant="subtle"
                      classNames={{
                        root: classes.entryButton,
                        label: classes.entryLabel,
                      }}
                      onClick={() => {
                        setEntry(item);
                        setFileCursors([]);
                        resultsHeading.current?.focus();
                      }}
                    >
                      {item.name}
                    </Button>
                    <Badge color={item.change === 'Removed' ? 'orange' : 'blue'}>
                      {item.change}
                    </Badge>
                  </Group>
                  <Text
                    size="xs"
                    c="dimmed"
                  >
                    {item.filesAdded.toLocaleString()} files added ·{' '}
                    {item.filesRemoved.toLocaleString()} removed ·{' '}
                    {item.filesChanged.toLocaleString()} changed
                  </Text>
                </li>
              ))}
            </Box>
          )}
          {entry != null && detail && detail.files.items.length > 0 && (
            <Box
              component="ul"
              className={classes.changeList}
              aria-label="Changed files"
            >
              {detail.files.items.map((item) => (
                <li
                  key={`${item.kind}:${item.name}`}
                  className={classes.changeRow}
                >
                  <details>
                    <summary className={classes.fileSummary}>
                      <Text
                        span
                        fw={600}
                        size="sm"
                      >
                        {item.name}
                      </Text>
                      <Text
                        span
                        size="xs"
                        c="dimmed"
                      >
                        {item.kind} · {item.change}
                      </Text>
                      {item.checksumsChanged && (
                        <Badge
                          color="yellow"
                          variant="light"
                        >
                          Checksum changed
                        </Badge>
                      )}
                    </summary>
                    <Fields fields={item.fields} />
                  </details>
                </li>
              ))}
            </Box>
          )}
          {entry == null && page && (
            <Pager
              label="Change pages"
              page={entryCursors.length}
              hasNextPage={page.hasNextPage}
              onPrevious={() => {
                resetScroll();
                setEntryCursors((cursors) => cursors.slice(0, -1));
              }}
              onNext={() => {
                resetScroll();
                const next = page.nextCursor;
                if (next)
                  setEntryCursors((cursors) => [
                    ...cursors,
                    next,
                  ]);
              }}
            />
          )}
          {entry != null && detail && (
            <Pager
              label="File change pages"
              page={fileCursors.length}
              hasNextPage={detail.files.hasNextPage}
              onPrevious={() => {
                resetScroll();
                setFileCursors((cursors) => cursors.slice(0, -1));
              }}
              onNext={() => {
                resetScroll();
                const next = detail.files.nextCursor;
                if (next)
                  setFileCursors((cursors) => [
                    ...cursors,
                    next,
                  ]);
              }}
            />
          )}
        </>
      )}
    </Stack>
  );
}
