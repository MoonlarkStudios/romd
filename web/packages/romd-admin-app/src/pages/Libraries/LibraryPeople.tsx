import {
  Alert,
  Badge,
  Button,
  Group,
  Modal,
  Select,
  Skeleton,
  Stack,
  Text,
  Title,
} from '@mantine/core';
import { useDebouncedValue } from '@mantine/hooks';
import { getLibraryAccountAccess, type Library, type User } from '@romd/admin-api-client';
import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { Link } from 'react-router';
import { TitleSearchSelect } from '../../components/TitleSearchSelect';
import { ApiClientError, useLibraries } from '../../hooks/api/useLibraryManagement';
import { useAssignUserLibrary, userKeys, useUserDirectory } from '../../hooks/api/useUsers';
import classes from './Libraries.module.css';

const unavailableAccess: Record<string, string> = {
  Unassigned: 'This account has no assigned library.',
  NeedsPublication: 'This library is waiting for its first publication.',
  InvalidRules: 'Repair the library rules to restore access.',
  Suspended: 'This account is suspended.',
  ActivationRequired: 'This account still needs to be activated.',
};

function AccountAccess({ account, close }: { account: User; close: () => void }) {
  const [titleId, setTitleId] = useState<string | null>(null);
  const query = useQuery({
    queryKey: [
      ...userKeys.all,
      'library-access',
      account.id,
      titleId,
    ],
    queryFn: async ({ signal }) => {
      const result = await getLibraryAccountAccess({
        path: {
          userId: account.id,
        },
        query: {
          titleId: titleId ?? undefined,
        },
        signal,
      });
      if (result.error || !result.data) throw new Error('Could not check library access.');
      return result.data;
    },
    staleTime: 0,
  });
  const access = query.data;
  return (
    <Modal
      opened
      onClose={close}
      title="Check library access"
    >
      <Stack gap="sm">
        <Text fw={600}>{account.email}</Text>
        {query.isPending && (
          <Skeleton
            h={70}
            aria-label="Checking library access"
          />
        )}
        {query.isError && (
          <Alert color="red">
            {query.error.message}
            <Button
              variant="subtle"
              onClick={() => void query.refetch()}
            >
              Retry
            </Button>
          </Alert>
        )}
        {access && (
          <>
            {access.libraryName && <Text size="sm">{access.libraryName}</Text>}
            {access.state !== 'Available' ? (
              <Alert>{unavailableAccess[access.state] ?? 'This library is unavailable.'}</Alert>
            ) : (
              <>
                <Text size="sm">
                  {access.ownedGames === 0
                    ? 'Published library with no owned games'
                    : `${access.ownedGames} owned ${access.ownedGames === 1 ? 'game' : 'games'} visible`}
                </Text>
                {access.publishedAt && (
                  <Text
                    size="xs"
                    c="dimmed"
                  >
                    Published {new Date(access.publishedAt).toLocaleString()}
                  </Text>
                )}
              </>
            )}
          </>
        )}
        {(access?.state === 'Available' || titleId) && (
          <TitleSearchSelect
            label="Check a game"
            placeholder="Search the catalog"
            localFirst
            withinPortal={false}
            value={titleId}
            onChange={setTitleId}
          />
        )}
        {access?.selectedGameVisible != null && (
          <Text
            size="sm"
            role="status"
          >
            {access.selectedGameVisible
              ? 'This game is visible to this account.'
              : 'This game is hidden from this account.'}
          </Text>
        )}
        <Button
          component={Link}
          to={`/users/${account.id}`}
          variant="subtle"
          w="fit-content"
        >
          Open account
        </Button>
      </Stack>
    </Modal>
  );
}

export function LibraryPeople({ library }: { library: Library }) {
  const [pages, setPages] = useState<(string | undefined)[]>([
    undefined,
  ]);
  const roster = useUserDirectory({
    libraryId: library.id,
    cursor: pages.at(-1),
    limit: 25,
  });
  const [search, setSearch] = useState('');
  const [term] = useDebouncedValue(search.trim(), 250);
  const matches = useUserDirectory(
    {
      search: term,
      limit: 20,
    },
    term.length >= 2,
  );
  const libraries = useLibraries();
  const assign = useAssignUserLibrary();
  const [selected, setSelected] = useState<User>();
  const [change, setChange] = useState<{
    account: User;
    destination: string | null;
  }>();
  const [checking, setChecking] = useState<User>();
  const waitingForSearch = term !== search.trim() || matches.isFetching;
  const candidates = waitingForSearch
    ? []
    : (matches.data?.items ?? []).filter((user) => user.libraryId !== library.id);
  const options =
    selected && !candidates.some((user) => user.id === selected.id)
      ? [
          selected,
          ...candidates,
        ]
      : candidates;
  const libraryName = (id?: string | null) =>
    libraries.data?.find((item) => item.id === id)?.name ?? 'the current library';
  const assignmentChanged = assign.error instanceof ApiClientError && assign.error.status === 412;
  const clearSelection = () => {
    setChange(undefined);
    setSelected(undefined);
    setSearch('');
  };
  const startChange = (account: User, destination: string | null) => {
    assign.reset();
    setChange({
      account,
      destination,
    });
  };
  const state = (user: User) => {
    if (user.isSuspended) return 'Suspended';
    if (user.requiresActivation) return 'Activation required';
    if (library.configurationState !== 'Valid') return 'Library unavailable';
    if (!library.lastMaterializedAt) return 'Awaiting publication';
    return 'Active';
  };

  return (
    <Stack
      className={classes.panel}
      gap="md"
    >
      <Title order={3}>People in this audience</Title>
      {roster.isError && (
        <Alert color="red">
          Could not load audience members.
          <Button
            variant="subtle"
            onClick={() => void roster.refetch()}
          >
            Retry
          </Button>
        </Alert>
      )}
      {roster.isPending && <Skeleton h={60} />}
      {roster.isSuccess && !roster.data.items.length && (
        <Text size="sm">
          {pages.length === 1 ? 'No one is assigned to this library.' : 'No accounts on this page.'}
        </Text>
      )}
      {roster.data?.items.map((user) => (
        <Group
          key={user.id}
          justify="space-between"
          gap="xs"
        >
          <Group gap="xs">
            <Text
              size="sm"
              style={{
                overflowWrap: 'anywhere',
              }}
            >
              {user.email}
            </Text>
            <Badge
              variant="light"
              color={state(user) === 'Active' ? 'teal' : 'gray'}
            >
              {state(user)}
            </Badge>
          </Group>
          <Group gap="xs">
            <Button
              size="compact-sm"
              variant="light"
              onClick={() => setChecking(user)}
            >
              Check access
            </Button>
            <Button
              size="compact-sm"
              variant="subtle"
              color="gray"
              disabled={!user.assignmentRevision}
              onClick={() => startChange(user, null)}
            >
              Unassign
            </Button>
          </Group>
        </Group>
      ))}
      {(pages.length > 1 || roster.data?.nextCursor) && (
        <Group>
          <Button
            size="compact-sm"
            variant="default"
            disabled={pages.length === 1}
            onClick={() => setPages((current) => current.slice(0, -1))}
          >
            Previous people
          </Button>
          <Button
            size="compact-sm"
            variant="default"
            disabled={!roster.data?.nextCursor}
            onClick={() =>
              setPages((current) => [
                ...current,
                roster.data?.nextCursor ?? undefined,
              ])
            }
          >
            Next people
          </Button>
        </Group>
      )}
      <Group align="end">
        <Select
          flex={1}
          searchable
          label="Assign a person"
          placeholder="Search by email"
          searchValue={search}
          onSearchChange={setSearch}
          value={selected?.id ?? null}
          filter={({ options: results }) => results}
          nothingFoundMessage={
            search.trim().length < 2
              ? 'Type at least two characters'
              : waitingForSearch
                ? 'Searching…'
                : 'No matching accounts'
          }
          data={options.map((user) => ({
            value: user.id,
            label: user.libraryId ? `${user.email} · ${libraryName(user.libraryId)}` : user.email,
          }))}
          onChange={(id) => setSelected(options.find((user) => user.id === id))}
        />
        <Button
          disabled={!selected?.assignmentRevision || library.configurationState !== 'Valid'}
          onClick={() => {
            if (selected) startChange(selected, library.id);
          }}
        >
          Assign
        </Button>
      </Group>
      {matches.isError && (
        <Alert color="red">
          Could not search for accounts.{' '}
          <Button
            variant="subtle"
            onClick={() => void matches.refetch()}
          >
            Retry
          </Button>
        </Alert>
      )}
      <Button
        component={Link}
        to="/users"
        variant="subtle"
        w="fit-content"
      >
        Manage people and roles
      </Button>
      {checking && (
        <AccountAccess
          account={checking}
          close={() => setChecking(undefined)}
        />
      )}
      {change && (
        <Modal
          opened
          onClose={() => setChange(undefined)}
          title={
            change.destination
              ? change.account.libraryId
                ? 'Move to another library'
                : 'Assign library'
              : 'Unassign library'
          }
          closeOnClickOutside={!assign.isPending}
          closeOnEscape={!assign.isPending}
          withCloseButton={!assign.isPending}
        >
          <Stack gap="sm">
            <Text size="sm">
              {change.destination
                ? change.account.libraryId
                  ? `Move ${change.account.email} from ${libraryName(change.account.libraryId)} to ${library.name}.`
                  : `Assign ${change.account.email} to ${library.name}.`
                : `Remove ${change.account.email} from ${library.name}. They will have no library access.`}
            </Text>
            {change.destination && !library.lastMaterializedAt && (
              <Alert>This person will get access after the library is published.</Alert>
            )}
            <Text
              size="sm"
              c="dimmed"
            >
              Any active sessions for this account will end. The person will need to sign in again.
            </Text>
            {assign.isError && <Alert color="red">{assign.error.message}</Alert>}
            <Group justify="end">
              <Button
                variant="default"
                disabled={assign.isPending}
                onClick={() => setChange(undefined)}
              >
                Cancel
              </Button>
              {assignmentChanged ? (
                <Button onClick={clearSelection}>Review current assignment</Button>
              ) : (
                <Button
                  color={change.destination ? undefined : 'red'}
                  loading={assign.isPending}
                  onClick={() =>
                    assign.mutate(
                      {
                        userId: change.account.id,
                        libraryId: change.destination,
                        expectedRevision: change.account.assignmentRevision,
                      },
                      {
                        onSuccess: clearSelection,
                      },
                    )
                  }
                >
                  {change.destination
                    ? change.account.libraryId
                      ? 'Move account'
                      : 'Assign account'
                    : 'Unassign account'}
                </Button>
              )}
            </Group>
          </Stack>
        </Modal>
      )}
    </Stack>
  );
}
