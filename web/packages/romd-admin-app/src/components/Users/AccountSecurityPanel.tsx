import { Alert, Button, CopyButton, Group, Modal, Skeleton, Stack, Table, Text, Textarea, Title } from '@mantine/core';
import { getAccountSecurity, issueAccountLink, revokeAccountLink, revokeAccountSessions, setAccountSuspension, type UserDto } from '@romd/admin-api-client';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useAuth } from '../../contexts/AuthContext';
import { userKeys } from '../../hooks/api/useUsers';
import { apiError } from '../Administration/apiError';
import classes from '../Workspace/Workspace.module.css';
import { workspaceActionProps } from '../Workspace/workspaceActions';
import { AccountSessions } from './AccountSessions';

export function AccountSecurityPanel({ user }: { user: UserDto }) {
  const client = useQueryClient();
  const { user: current } = useAuth();
  const [pending, setPending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [issued, setIssued] = useState<{ url: string; expiresAt: string } | null>(null);
  const query = useQuery({ queryKey: [...userKeys.all, 'security', user.id], queryFn: async () => {
    const response = await getAccountSecurity({ path: { userId: user.id } });
    if (response.error || !response.data) throw new Error(apiError(response.error, 'Could not load account security.'));
    return response.data;
  } });
  const run = async (operation: () => Promise<{ error?: unknown }>) => {
    setPending(true); setError(null);
    try { const response = await operation(); if (response.error) throw new Error(apiError(response.error, 'Account change failed.')); await client.invalidateQueries({ queryKey: userKeys.all }); }
    catch (error) { setError(apiError(error, 'Account change failed.')); }
    finally { setPending(false); }
  };
  const issue = async () => {
    setPending(true); setError(null);
    try {
      const response = await issueAccountLink({ path: { userId: user.id }, body: { purpose: user.requiresActivation ? 'activation' : 'recovery' } });
      if (response.error || !response.data) throw new Error(apiError(response.error, 'Could not issue a link.'));
      setIssued({ url: `${window.location.origin}/activate#${response.data.token}`, expiresAt: response.data.expiresAt });
      await query.refetch();
    } catch (error) { setError(apiError(error, 'Could not issue a link.')); }
    finally { setPending(false); }
  };
  return <Stack gap="lg">
    {error && <Alert color="red" title="Change failed">{error}</Alert>}
    <section className={classes.panel}><Group justify="space-between"><div><Title order={2} className={classes.sectionHeading}>{user.requiresActivation ? 'Activation' : 'Password recovery'}</Title><Text size="sm" c="dimmed" mt="xs">Generate a single-use link to share securely. No email is sent. Issuing a new link revokes previous pending links.</Text></div><Button {...workspaceActionProps} disabled={user.isSuspended} loading={pending} onClick={() => void issue()}>Generate {user.requiresActivation ? 'activation' : 'recovery'} link</Button></Group>
      {query.isPending && <Skeleton height={80} mt="md" />}{query.isError && <Alert color="red" mt="md">{query.error.message}<Button variant="subtle" onClick={() => void query.refetch()}>Retry</Button></Alert>}
      {query.data && <Table.ScrollContainer minWidth={540}><Table mt="md"><Table.Thead><Table.Tr><Table.Th>Purpose</Table.Th><Table.Th>Expires</Table.Th><Table.Th>Status</Table.Th><Table.Th>Action</Table.Th></Table.Tr></Table.Thead><Table.Tbody>{query.data.links.map(link => <Table.Tr key={link.id}><Table.Td>{link.purpose}</Table.Td><Table.Td>{new Date(link.expiresAt).toLocaleString()}</Table.Td><Table.Td>{link.status}</Table.Td><Table.Td>{link.status === 'Pending' && <Button size="compact-sm" color="red" variant="subtle" disabled={pending} onClick={() => void run(() => revokeAccountLink({ path: { userId: user.id, linkId: link.id } }))}>Revoke link</Button>}</Table.Td></Table.Tr>)}</Table.Tbody></Table>{!query.data.links.length && <Text size="sm" c="dimmed" mt="md">No activation or recovery links issued.</Text>}</Table.ScrollContainer>}
    </section>
    <section className={classes.panel}><Title order={2} className={classes.sectionHeading} mb="md">Sessions</Title>{query.data ? <AccountSessions sessions={query.data.sessions} pending={pending} onRevoke={id => void run(() => revokeAccountSessions({ path: { userId: user.id }, query: { sessionId: id ?? undefined } }))} /> : <Text c="dimmed">Session data is unavailable.</Text>}</section>
    <section className={classes.panel}><Title order={2} className={classes.sectionHeading}>Account access</Title><Text size="sm" c="dimmed" my="md">Suspension blocks sign-in and revokes sessions and pending links. Library assignments and account history are preserved.</Text><Button color={user.isSuspended ? 'teal' : 'red'} variant="light" disabled={current?.id === user.id} loading={pending} onClick={() => { if (window.confirm(`${user.isSuspended ? 'Reactivate' : 'Suspend'} ${user.email}?`)) void run(() => setAccountSuspension({ path: { userId: user.id }, body: { suspended: !user.isSuspended } })); }}>{user.isSuspended ? 'Reactivate account' : 'Suspend account'}</Button>{current?.id === user.id && <Text size="sm" c="dimmed" mt="xs">You cannot suspend your own account.</Text>}</section>
    <Modal opened={issued !== null} onClose={() => setIssued(null)} title="Share this account link" centered><Stack><Text>Copy the link now and send it through a trusted channel. It will not be displayed again.</Text><Textarea label="Account link" value={issued?.url ?? ''} readOnly autosize minRows={3} /><Text size="sm">Expires {issued ? new Date(issued.expiresAt).toLocaleString() : ''}.</Text><Group justify="flex-end"><CopyButton value={issued?.url ?? ''}>{({ copied, copy }) => <Button {...workspaceActionProps} onClick={copy}>{copied ? 'Copied' : 'Copy link'}</Button>}</CopyButton><Button variant="default" onClick={() => setIssued(null)}>Done</Button></Group></Stack></Modal>
  </Stack>;
}
