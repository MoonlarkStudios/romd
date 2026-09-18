import { Alert, Anchor, Badge, Button, Checkbox, Group, Menu, Modal, Select, Skeleton, Stack, Table, Text, TextInput, Title } from '@mantine/core';
import { useDebouncedValue } from '@mantine/hooks';
import { IconDots, IconPlus, IconRefresh, IconSearch } from '@tabler/icons-react';
import { useMemo, useState } from 'react';
import { Link, useSearchParams } from 'react-router';
import { CreateAccount } from '../../components/Users/CreateAccount';
import { accountStatus, highestRole } from '../../components/Users/userPresentation';
import classes from '../../components/Workspace/Workspace.module.css';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import { useAssignDefaultLibraryToUsers, useLibraries, userRoleOptions, useUserDirectory } from '../../hooks/api/useUsers';

export function Users() {
  const libraries = useLibraries();
  const assign = useAssignDefaultLibraryToUsers();
  const [params, setParams] = useSearchParams();
  const search = params.get('q') ?? '';
  const role = params.get('role');
  const library = params.get('library');
  const status = params.get('status');
  const cursor = params.get('cursor') ?? undefined;
  const [debouncedSearch] = useDebouncedValue(search, 250);
  const query = useUserDirectory({ search: debouncedSearch || undefined, role: role ?? undefined, status: status ?? undefined, libraryId: library ?? undefined, cursor });
  const [previousCursors, setPreviousCursors] = useState<(string | undefined)[]>([]);
  const goPage = (nextCursor?: string) => { const next = new URLSearchParams(params); if (nextCursor) next.set('cursor', nextCursor); else next.delete('cursor'); setParams(next); setSelected(new Set()); };
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [creating, setCreating] = useState(false);
  const [confirm, setConfirm] = useState(false);
  const [result, setResult] = useState<string | null>(null);
  const updateFilter = (name: string, value: string | null) => { const next = new URLSearchParams(params); if (value) next.set(name, value); else next.delete(name); next.delete('cursor'); setPreviousCursors([]); setParams(next, { replace: true }); setSelected(new Set()); };
  const rows = query.data?.items ?? [];
  const libraryNames = useMemo(() => new Map(libraries.data?.map(library => [library.id, library.name])), [libraries.data]);
  const eligible = rows.filter(user => selected.has(user.id) && !user.libraryId && !user.isSuspended);
  const defaultLibrary = libraries.data?.find(library => library.isDefault);
  const toggle = (id: string, checked: boolean) => setSelected(current => { const next = new Set(current); if (checked) next.add(id); else next.delete(id); return next; });
  return <Stack className={classes.page} gap="lg">
    <Group className={classes.hero} justify="space-between"><Title order={1} className={classes.heading}>Users</Title><Group gap="xs">
      <Text size="sm" c="dimmed" w={90} aria-live="polite">{selected.size ? `${selected.size} selected` : ''}</Text>
      <Menu position="bottom-end"><Menu.Target><Button variant="default" rightSection={<IconDots size={16} />} disabled={!selected.size}>Selection</Button></Menu.Target><Menu.Dropdown><Menu.Item disabled={!eligible.length || defaultLibrary?.configurationState !== 'Valid' || libraries.isError} onClick={() => setConfirm(true)}>Assign default library ({eligible.length})</Menu.Item><Menu.Item onClick={() => setSelected(new Set())}>Clear selection</Menu.Item></Menu.Dropdown></Menu>
      <Button variant="default" leftSection={<IconRefresh size={16} />} loading={query.isFetching} onClick={() => void query.refetch()}>Refresh</Button><Button {...workspaceActionProps} leftSection={<IconPlus size={16} />} onClick={() => setCreating(true)}>Create account</Button>
    </Group></Group>
    <Group align="end"><TextInput label="Search accounts" placeholder="Email" leftSection={<IconSearch size={16} />} value={search} onChange={event => updateFilter('q', event.currentTarget.value)} style={{ flex: '1 1 240px' }} /><Select label="Role" placeholder="All roles" data={userRoleOptions} value={role} onChange={value => updateFilter('role', value)} clearable /><Select label="Status" placeholder="All statuses" data={['Active', 'Pending activation', 'Suspended']} value={status} onChange={value => updateFilter('status', value)} clearable /><Select label="Consumer library" placeholder="All libraries" data={[{ value: 'none', label: 'No library' }, ...(libraries.data ?? []).map(library => ({ value: library.id, label: library.name }))]} value={library} onChange={value => updateFilter('library', value)} clearable disabled={libraries.isError} /></Group>
    {libraries.isError && <Alert color="yellow">Library names could not be loaded. Account data remains available. <Button variant="subtle" onClick={() => void libraries.refetch()}>Retry libraries</Button></Alert>}
    {query.isError && <Alert color="red">{query.error.message}<Button variant="subtle" onClick={() => void query.refetch()}>Retry</Button></Alert>}
    {result && <Alert color="teal" withCloseButton onClose={() => setResult(null)}>{result}</Alert>}
    {query.isPending ? <Skeleton height={240} /> : query.data && <>
      <Table.ScrollContainer minWidth={760}><Table verticalSpacing="md" highlightOnHover>
        <Table.Thead><Table.Tr><Table.Th w={42}><Checkbox aria-label="Select visible accounts" checked={rows.length > 0 && rows.every(user => selected.has(user.id))} indeterminate={rows.some(user => selected.has(user.id)) && !rows.every(user => selected.has(user.id))} onChange={event => setSelected(event.currentTarget.checked ? new Set(rows.map(user => user.id)) : new Set())} /></Table.Th><Table.Th>Account</Table.Th><Table.Th>Status</Table.Th><Table.Th>Role</Table.Th><Table.Th>Consumer library</Table.Th><Table.Th>Last sign-in</Table.Th></Table.Tr></Table.Thead>
        <Table.Tbody>{rows.map(user => <Table.Tr key={user.id} bg={selected.has(user.id) ? 'var(--mantine-color-teal-light)' : undefined}>
          <Table.Td><Checkbox aria-label={`Select ${user.email}`} checked={selected.has(user.id)} onChange={event => toggle(user.id, event.currentTarget.checked)} /></Table.Td><Table.Td><Anchor component={Link} to={`/users/${user.id}`} fw={500}>{user.email}</Anchor></Table.Td><Table.Td><Badge variant="light" color={user.isSuspended ? 'red' : user.requiresActivation ? 'yellow' : 'teal'}>{accountStatus(user)}</Badge></Table.Td><Table.Td>{highestRole(user)}</Table.Td><Table.Td>{user.libraryId ? libraryNames.get(user.libraryId) ?? 'Unavailable library' : 'No consumer access'}</Table.Td><Table.Td>{user.lastSignedInAt ? new Date(user.lastSignedInAt).toLocaleString() : 'Not recorded'}</Table.Td>
        </Table.Tr>)}</Table.Tbody>
      </Table></Table.ScrollContainer>
      {!rows.length && <Text c="dimmed" ta="center" py="xl">{search || role || library || status ? 'No accounts match these filters.' : 'No human accounts found.'}</Text>}
      <Group justify="space-between"><Text size="sm" c="dimmed">{rows.length} accounts on this page · System activity uses a protected internal account.</Text><Group><Button variant="default" disabled={!cursor || query.isFetching} onClick={() => { const previous = previousCursors.at(-1); setPreviousCursors(items => items.slice(0, -1)); goPage(previous); }}>Previous page</Button><Button variant="default" disabled={!query.data.nextCursor || query.isFetching} onClick={() => { setPreviousCursors(items => [...items, cursor]); goPage(query.data?.nextCursor ?? undefined); }}>Next page</Button></Group></Group>
    </>}
    {creating && <CreateAccount onClose={() => setCreating(false)} />}
    <Modal opened={confirm} onClose={() => !assign.isPending && setConfirm(false)} title="Assign default library?" centered><Stack><Text>Grant {eligible.length} selected unassigned accounts access to {defaultLibrary?.name}. Existing assignments and suspended accounts are skipped.</Text>{assign.isError && <Alert color="red">{assign.error.message}</Alert>}<Group justify="flex-end"><Button variant="default" onClick={() => setConfirm(false)} disabled={assign.isPending}>Cancel</Button><Button {...workspaceActionProps} loading={assign.isPending} disabled={!eligible.length} onClick={() => assign.mutate(eligible.map(user => user.id), { onSuccess: response => { setResult(`Default library assigned to ${response?.updatedCount ?? 0} accounts.`); setConfirm(false); setSelected(new Set()); } })}>Assign library</Button></Group></Stack></Modal>
  </Stack>;
}
