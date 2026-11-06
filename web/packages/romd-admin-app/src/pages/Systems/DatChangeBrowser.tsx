import {
  Alert,
  Badge,
  Box,
  Button,
  Group,
  Loader,
  NativeSelect,
  Stack,
  Table,
  Text,
  TextInput,
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
import { useEffect, useRef, useState } from 'react';

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
    <Table withTableBorder>
      <Table.Thead>
        <Table.Tr>
          <Table.Th>Field</Table.Th>
          <Table.Th>Before</Table.Th>
          <Table.Th>After</Table.Th>
        </Table.Tr>
      </Table.Thead>
      <Table.Tbody>
        {fields.map((field) => (
          <Table.Tr key={field.field}>
            <Table.Td>{field.field}</Table.Td>
            <Table.Td
              style={{
                overflowWrap: 'anywhere',
              }}
            >
              {field.before ?? '—'}
            </Table.Td>
            <Table.Td
              style={{
                overflowWrap: 'anywhere',
              }}
            >
              {field.after ?? '—'}
            </Table.Td>
          </Table.Tr>
        ))}
      </Table.Tbody>
    </Table>
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
        variant="default"
        disabled={!hasNextPage}
        onClick={onNext}
      >
        Next changes
      </Button>
    </Group>
  );
}
export function DatChangeBrowser({
  datId,
  subscriptionId,
  file,
  preview,
  onStale,
}: {
  datId: string;
  subscriptionId?: string;
  file?: File;
  preview: DatReplacementPreview;
  onStale: (message: string) => void;
}) {
  const viewport = useRef<HTMLDivElement>(null);
  const resetScroll = () => {
    if (viewport.current) viewport.current.scrollTop = 0;
  };
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
  return (
    <Stack gap="sm">
      {entry == null ? (
        <>
          <Text fw={600}>Explore all changes</Text>
          <form
            onSubmit={(event) => {
              event.preventDefault();
              setSearch(draft.trim());
              setEntryCursors([]);
              resetScroll();
            }}
          >
            <Group align="flex-end">
              <TextInput
                label="Search changed entries"
                value={draft}
                onChange={(event) => setDraft(event.currentTarget.value)}
                maxLength={200}
                style={{
                  flex: 1,
                }}
              />
              <Button type="submit">Search</Button>
              <NativeSelect
                label="Change type"
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
            </Group>
          </form>
        </>
      ) : (
        <>
          <Button
            variant="subtle"
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
          <Box
            ref={viewport}
            role="region"
            aria-label={entry == null ? 'Changed entries' : 'Changed files'}
            tabIndex={0}
            style={{
              maxHeight: '35vh',
              overflowY: 'auto',
              overscrollBehavior: 'contain',
            }}
          >
            {entry == null && page && page.items.length > 0 && (
              <Table>
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th>Entry</Table.Th>
                    <Table.Th>Change</Table.Th>
                    <Table.Th>Files</Table.Th>
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {page.items.map((item) => (
                    <Table.Tr key={item.id}>
                      <Table.Td>
                        <Button
                          variant="subtle"
                          styles={{
                            root: {
                              height: 'auto',
                            },
                            label: {
                              whiteSpace: 'normal',
                              textAlign: 'left',
                              overflowWrap: 'anywhere',
                            },
                          }}
                          onClick={() => {
                            setEntry(item);
                            setFileCursors([]);
                            resetScroll();
                          }}
                        >
                          {item.name}
                        </Button>
                      </Table.Td>
                      <Table.Td>
                        <Badge
                          w={90}
                          color={item.change === 'Removed' ? 'orange' : 'blue'}
                        >
                          {item.change}
                        </Badge>
                      </Table.Td>
                      <Table.Td>
                        {item.filesAdded} added · {item.filesRemoved} removed · {item.filesChanged}{' '}
                        changed
                      </Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            )}
            {entry != null &&
              detail?.files.items.map((item) => (
                <details key={`${item.kind}:${item.name}`}>
                  <summary>
                    {item.name} · {item.kind} · {item.change}
                    {item.checksumsChanged ? ' · checksum changed' : ''}
                  </summary>
                  <Fields fields={item.fields} />
                </details>
              ))}
          </Box>
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
