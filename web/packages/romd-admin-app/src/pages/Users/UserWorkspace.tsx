import { Alert, Anchor, Badge, Button, Group, Select, Skeleton, Stack, Tabs, Text, TextInput, Title } from '@mantine/core';
import type { UserDto } from '@romd/admin-api-client';
import { IconArrowLeft } from '@tabler/icons-react';
import { useState } from 'react';
import { Link, useNavigate, useParams, useSearchParams } from 'react-router';
import { UnsavedChanges } from '../../components/Administration/UnsavedChanges';
import { AccountSecurityPanel } from '../../components/Users/AccountSecurityPanel';
import { accountStatus, highestRole, isSystemUser, roleDescription } from '../../components/Users/userPresentation';
import classes from '../../components/Workspace/Workspace.module.css';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import { useAuth } from '../../contexts/AuthContext';
import { type UserRole, useAssignUserLibrary, useAssignUserRole, useDeleteUser, useLibraries, userRoleOptions, useUpdateUser, useUser } from '../../hooks/api/useUsers';
import { AuditEvents } from '../Audit';

function AccountWorkspace({ user }: { user: UserDto }) {
  const [params, setParams] = useSearchParams();
  const tab = ['overview', 'access', 'security', 'activity'].find(tab => tab === params.get('tab')) ?? 'overview';
  const [baseline, setBaseline] = useState({ email: user.email, role: highestRole(user), libraryId: user.libraryId });
  const [email, setEmail] = useState(user.email);
  const [role, setRole] = useState<UserRole>(highestRole(user));
  const [libraryId, setLibraryId] = useState(user.libraryId);
  const dirty = email !== baseline.email || role !== baseline.role || libraryId !== baseline.libraryId;
  const libraries = useLibraries();
  const update = useUpdateUser();
  const assignRole = useAssignUserRole();
  const assignLibrary = useAssignUserLibrary();
  const remove = useDeleteUser();
  const navigate = useNavigate();
  const { user: current } = useAuth();
  const protectedAccount = isSystemUser(user);
  const library = libraries.data?.find(library => library.id === user.libraryId);
  const pending = update.isPending || assignRole.isPending || assignLibrary.isPending || remove.isPending;
  const [notice, setNotice] = useState<string | null>(null);
  return <Stack className={classes.page} gap="lg">
    <UnsavedChanges dirty={dirty} />
    <Button component={Link} to="/users" variant="subtle" color="gray" px={0} w="fit-content" leftSection={<IconArrowLeft size={15} />}>Users</Button>
    <Group className={classes.hero} justify="space-between"><Title order={1} className={classes.heading}>{user.email}</Title><Group><Badge color={user.isSuspended ? 'red' : 'teal'} variant="light">{accountStatus(user)}</Badge>{dirty && <Badge color="yellow" variant="light">Unsaved changes</Badge>}</Group></Group>
    {protectedAccount ? <Alert color="gray">This protected account records automated system activity. Human account controls do not apply.</Alert> : <>
      {notice && <Alert color="teal" withCloseButton onClose={() => setNotice(null)}>{notice}</Alert>}
      <Tabs value={tab} onChange={value => setParams({ tab: value ?? 'overview' })} classNames={{ root: classes.tabs, list: classes.tabsList, tab: classes.tab }} keepMounted={false}>
        <Tabs.List><Tabs.Tab value="overview">Overview</Tabs.Tab><Tabs.Tab value="access">Access</Tabs.Tab><Tabs.Tab value="security">Security</Tabs.Tab><Tabs.Tab value="activity">Activity</Tabs.Tab></Tabs.List>
        <Tabs.Panel value="overview" pt="lg"><Stack gap="lg"><section className={classes.panel}><Title order={2} className={classes.sectionHeading} mb="md">Identity</Title><Stack><TextInput label="Email" type="email" value={email} disabled={pending} onChange={event => setEmail(event.currentTarget.value)} />{update.isError && <Alert color="red">{update.error.message}</Alert>}<Group justify="flex-end"><Button variant="default" disabled={pending || email === baseline.email} onClick={() => setEmail(baseline.email)}>Discard email change</Button><Button {...workspaceActionProps} disabled={!email || email === baseline.email} loading={update.isPending} onClick={() => update.mutate({ userId: user.id, request: { email, newPassword: null } }, { onSuccess: saved => { setBaseline(value => ({ ...value, email: saved.email })); setEmail(saved.email); setNotice('Email saved.'); } })}>Save email</Button></Group></Stack></section>
          <section className={classes.panel}><Title order={2} className={classes.sectionHeading} mb="md">Effective access</Title><Stack gap="xs"><Text>{roleDescription[highestRole(user)]}</Text><Text size="sm">{user.isSuspended ? 'Sign-in blocked: account suspended.' : user.requiresActivation ? 'Sign-in blocked until the recipient activates this account.' : 'Account is activated and not administratively suspended.'}</Text><Text size="sm">Consumer library: {user.libraryId ? library?.name ?? 'Library details unavailable' : 'None assigned — no consumer catalog access.'}</Text>{library && <Anchor component={Link} to={`/libraries/${library.id}`} size="sm">Inspect library configuration</Anchor>}<Text size="sm" c="dimmed">Created {new Date(user.createdAt).toLocaleString()} · Last sign-in {user.lastSignedInAt ? new Date(user.lastSignedInAt).toLocaleString() : 'not recorded'}</Text></Stack></section>
          <section className={classes.panel}><Title order={2} className={classes.sectionHeading} mb="md">Delete account</Title><Text size="sm" c="dimmed" mb="md">Permanent removal. Suspend the account when you may need to restore access later. Audit evidence is retained.</Text>{remove.isError && <Alert color="red" mb="md">{remove.error.message}</Alert>}<Button color="red" variant="light" disabled={current?.id === user.id || dirty} loading={remove.isPending} onClick={() => { if (window.confirm(`Permanently delete ${user.email}?`)) remove.mutate(user.id, { onSuccess: () => navigate('/users') }); }}>Delete account</Button></section>
        </Stack></Tabs.Panel>
        <Tabs.Panel value="access" pt="lg"><Stack gap="lg">
          <section className={classes.panel}><Title order={2} className={classes.sectionHeading} mb="md">Role</Title><Stack><Select label="ROMD role" data={userRoleOptions} value={role} onChange={value => setRole((value ?? 'User') as UserRole)} disabled={pending} /><Text size="sm" c="dimmed">{roleDescription[role]}</Text><Text size="sm">Changing a role revokes existing sessions. The account must sign in again.</Text>{assignRole.isError && <Alert color="red">{assignRole.error.message}</Alert>}<Group justify="flex-end"><Button variant="default" disabled={pending || role === baseline.role} onClick={() => setRole(baseline.role)}>Discard role change</Button><Button {...workspaceActionProps} disabled={role === baseline.role} loading={assignRole.isPending} onClick={() => { if (window.confirm(`Change ${user.email} to ${role} and revoke their sessions?`)) assignRole.mutate({ userId: user.id, role }, { onSuccess: saved => { const next = highestRole(saved); setRole(next); setBaseline(value => ({ ...value, role: next })); setNotice('Role saved. Existing sessions revoked.'); } }); }}>Save role</Button></Group></Stack></section>
          <section className={classes.panel}><Title order={2} className={classes.sectionHeading} mb="md">Consumer library</Title><Stack>{libraries.isError && <Alert color="red">Library details could not be loaded.<Button variant="subtle" onClick={() => void libraries.refetch()}>Retry</Button></Alert>}<Select label="Assigned library" placeholder="No consumer access" clearable data={(libraries.data ?? []).map(library => ({ value: library.id, label: library.name, disabled: library.configurationState !== 'Valid' }))} value={libraryId} onChange={setLibraryId} disabled={pending || libraries.isPending || libraries.isError} /><Text size="sm" c="dimmed">This limits the catalog available in the consumer app and console. It does not change the account’s admin role.</Text>{assignLibrary.isError && <Alert color="red">{assignLibrary.error.message}</Alert>}<Group justify="flex-end"><Button variant="default" disabled={pending || libraryId === baseline.libraryId} onClick={() => setLibraryId(baseline.libraryId)}>Discard library change</Button><Button {...workspaceActionProps} disabled={libraryId === baseline.libraryId || libraries.isError || libraries.isPending} loading={assignLibrary.isPending} onClick={() => assignLibrary.mutate({ userId: user.id, libraryId }, { onSuccess: saved => { setLibraryId(saved.libraryId); setBaseline(value => ({ ...value, libraryId: saved.libraryId })); setNotice('Consumer library saved.'); } })}>Save library</Button></Group></Stack></section>
        </Stack></Tabs.Panel>
        <Tabs.Panel value="security" pt="lg"><AccountSecurityPanel user={user} /></Tabs.Panel>
        <Tabs.Panel value="activity" pt="lg"><AuditEvents targetId={user.id} targetType="User" /></Tabs.Panel>
      </Tabs>
    </>}
  </Stack>;
}

export function UserWorkspace() {
  const { userId = '' } = useParams();
  const query = useUser(userId);
  if (query.isPending) return <Skeleton height={320} />;
  if (!query.data) return <Alert color="red">{query.error?.message ?? 'Account could not be loaded.'}<Button variant="subtle" onClick={() => void query.refetch()}>Retry</Button><Anchor component={Link} to="/users">Return to Users</Anchor></Alert>;
  return <>{query.isError && <Alert color="yellow">Refresh failed. Showing the last loaded account.</Alert>}<AccountWorkspace key={userId} user={query.data} /></>;
}
