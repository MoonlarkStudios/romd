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
import type { DatFieldChange, DatReplacementPreview } from '@romd/admin-api-client';
import {
  getCatalogSubscriptionChanges,
  getDatReplacementChanges,
  getDatSubscriptionChanges,
} from '@romd/admin-api-client';
import { useQuery } from '@tanstack/react-query';
import { useEffect, useRef, useState } from 'react';

class ReviewPageError extends Error {
  constructor(
    message: string,
    readonly stale: boolean,
  ) {
    super(message);
  }
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
  const [offset, setOffset] = useState(0);
  const [entry, setEntry] = useState<string>();
  const [fileOffset, setFileOffset] = useState(0);
  const position = entry == null ? offset : fileOffset;
  const query = useQuery({
    queryKey: [
      'dat-review-changes',
      datId,
      subscriptionId,
      preview.activeSha256,
      preview.candidateSha256,
      entry,
      position,
      search,
      change,
    ],
    retry: false,
    refetchOnWindowFocus: false,
    queryFn: async ({ signal }) => {
      const body = {
        activeSha256: preview.activeSha256,
        candidateSha256: preview.candidateSha256,
        offset: position,
        entryName: entry,
        search: entry == null ? search : undefined,
        change: entry == null ? change : undefined,
      };
      const response = subscriptionId
        ? await getCatalogSubscriptionChanges({
            path: {
              subscriptionId,
            },
            body,
            signal,
          })
        : file
          ? await getDatReplacementChanges({
              path: {
                datId,
              },
              body: {
                ...body,
                file,
              },
              signal,
            })
          : await getDatSubscriptionChanges({
              path: {
                datId,
              },
              body,
              signal,
            });
      if (!response.data) {
        const error = response.error;
        const message =
          error && 'detail' in error && typeof error.detail === 'string'
            ? error.detail
            : 'Could not load this page. Your installed catalog is unchanged. Try again.';
        throw new ReviewPageError(
          message,
          response.response?.status === 409 || response.response?.status === 404,
        );
      }
      if (
        response.data.activeSha256 !== preview.activeSha256 ||
        response.data.candidateSha256 !== preview.candidateSha256
      )
        throw new ReviewPageError('The reviewed documents changed. Request a new preview.', true);
      return response.data;
    },
  });
  useEffect(() => {
    if (query.error instanceof ReviewPageError && query.error.stale) onStale(query.error.message);
  }, [
    query.error,
    onStale,
  ]);
  const page = query.data;
  return (
    <Stack gap="sm">
      {entry == null ? (
        <>
          <Text fw={600}>Explore all changes</Text>
          <form
            onSubmit={(event) => {
              event.preventDefault();
              setSearch(draft.trim());
              setOffset(0);
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
                  setOffset(0);
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
          <Text fw={600}>Changes in {entry}</Text>
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
      {page && !query.isFetching && !query.error && (
        <>
          {entry != null && page.entryFields.length > 0 && (
            <>
              <Text fw={500}>Entry metadata</Text>
              <Fields fields={page.entryFields} />
            </>
          )}
          {page.total === 0 && (
            <Text>
              {entry == null
                ? 'No changes match these filters.'
                : 'No file changes. Entry metadata may have changed.'}
            </Text>
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
            {entry == null && page.entries.length > 0 && (
              <Table>
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th>Entry</Table.Th>
                    <Table.Th>Change</Table.Th>
                    <Table.Th>Files</Table.Th>
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {page.entries.map((item) => (
                    <Table.Tr key={item.name}>
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
                            setEntry(item.name);
                            setFileOffset(0);
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
              page.files.map((item) => (
                <details key={`${item.kind}:${item.name}`}>
                  <summary>
                    {item.name} · {item.kind} · {item.change}
                    {item.checksumsChanged ? ' · checksum changed' : ''}
                  </summary>
                  <Fields fields={item.fields} />
                </details>
              ))}
          </Box>
          {page.total > 0 && (
            <Group
              justify="space-between"
              aria-label={entry == null ? 'Change pages' : 'File change pages'}
            >
              <Button
                variant="default"
                disabled={position === 0}
                onClick={() => {
                  resetScroll();
                  entry == null
                    ? setOffset(Math.max(0, offset - page.pageSize))
                    : setFileOffset(Math.max(0, fileOffset - page.pageSize));
                }}
              >
                Previous changes
              </Button>
              <Text
                size="sm"
                role="status"
              >
                {Math.min(position + 1, page.total)}–
                {Math.min(position + page.pageSize, page.total)} of {page.total.toLocaleString()}
              </Text>
              <Button
                variant="default"
                disabled={position + page.pageSize >= page.total}
                onClick={() => {
                  resetScroll();
                  entry == null
                    ? setOffset(offset + page.pageSize)
                    : setFileOffset(fileOffset + page.pageSize);
                }}
              >
                Next changes
              </Button>
            </Group>
          )}
        </>
      )}
    </Stack>
  );
}
