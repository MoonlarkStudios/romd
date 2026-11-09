import { Alert, Button, Checkbox, Group, Stack, Text } from '@mantine/core';
import { getLibraryDiscSetOptions, type LibraryDiscSetOption } from '@romd/admin-api-client';
import { useInfiniteQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { libraryManagementKeys } from '../../hooks/api/useLibraryManagement';

export function LibraryDiscSets({
  libraryId,
  value,
  onChange,
}: {
  libraryId: string;
  value: string[];
  onChange: (ids: string[]) => void;
}) {
  const [expanded, setExpanded] = useState(false);
  const query = useInfiniteQuery({
    queryKey: libraryManagementKeys.discSetOptionsFor(libraryId),
    initialPageParam: undefined as string | undefined,
    enabled: expanded,
    queryFn: async ({ pageParam }) => {
      const response = await getLibraryDiscSetOptions({
        path: {
          libraryId,
        },
        query: {
          cursor: pageParam,
        },
      });
      if (response.error || !response.data) throw new Error('Could not load disc-set choices.');
      return response.data;
    },
    getNextPageParam: (page) =>
      page.page.hasNextPage ? (page.page.nextCursor ?? undefined) : undefined,
  });
  const choices = new Map<string, LibraryDiscSetOption>();
  for (const page of query.data?.pages ?? [])
    for (const item of [
      ...page.selected,
      ...page.page.items,
    ])
      choices.set(item.id, item);
  const toggle = (id: string, checked: boolean) =>
    onChange(
      checked
        ? [
            ...new Set([
              ...value,
              id,
            ]),
          ]
        : value.filter((item) => item !== id),
    );
  return (
    <Stack gap="sm">
      <Text
        component="h3"
        className="romd-section-heading"
        m={0}
      >
        Disc sets
      </Text>
      <Text size="sm">
        {value.length} selected sets. Each member remains subject to this Library’s rules. Save
        these choices, then refresh the Library to publish its ordered membership.
      </Text>
      <Button
        variant="light"
        onClick={() => setExpanded(!expanded)}
      >
        {expanded ? 'Close disc-set choices' : 'Choose disc sets'}
      </Button>
      {expanded && (
        <>
          {query.isPending && <Text>Loading disc-set choices…</Text>}
          {query.isError && (
            <Alert
              color="red"
              title="Could not load disc sets"
            >
              <Button onClick={() => void query.refetch()}>Retry disc-set choices</Button>
            </Alert>
          )}
          {query.isSuccess && choices.size === 0 && value.length === 0 && (
            <Text>
              No eligible accepted disc sets. Review proposals in the title’s Discs tab first.
            </Text>
          )}
          {[
            ...choices.values(),
          ].map((item) => (
            <Stack
              key={item.id}
              gap="xs"
            >
              <Checkbox
                label={`${item.name} · ${item.systemKey ?? 'System unavailable'}`}
                checked={value.includes(item.id)}
                disabled={!item.isEligible && !value.includes(item.id)}
                onChange={(event) => toggle(item.id, event.currentTarget.checked)}
              />
              <Text
                size="sm"
                c="dimmed"
              >
                {item.memberCount} known discs · Expected total:{' '}
                {item.expectedDiscCount ?? 'unknown'}
              </Text>
              {!item.isEligible && (
                <Alert
                  color="yellow"
                  title="Selection needs review"
                >
                  {item.issue ?? 'This set no longer satisfies the current Library rules.'} The
                  selection is retained until you remove or repair it.
                </Alert>
              )}
            </Stack>
          ))}
          {value
            .filter((id) => !choices.has(id))
            .map((id) => (
              <Group key={id}>
                <Text size="sm">
                  Selected set {id}: details not loaded. This choice will be preserved.
                </Text>
                <Button
                  variant="subtle"
                  onClick={() => toggle(id, false)}
                >
                  Remove selected set {id}
                </Button>
              </Group>
            ))}
          {query.hasNextPage && (
            <Button
              variant="light"
              loading={query.isFetchingNextPage}
              onClick={() => void query.fetchNextPage()}
            >
              More disc sets
            </Button>
          )}
        </>
      )}
    </Stack>
  );
}
