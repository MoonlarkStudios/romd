import { Alert, Badge, Button, Group, Stack, Table, Tabs, Text } from '@mantine/core';
import type { AccountSessionDto } from '@romd/admin-api-client';
import { useState } from 'react';

export function AccountSessions({ sessions, onRevoke, onRevokeOthers, pending }: {
  sessions: AccountSessionDto[]; onRevoke: (id: string | null) => void;
  onRevokeOthers?: () => void; pending: boolean;
}) {
  const [view, setView] = useState<string | null>('current');
  const current = sessions.filter(session => session.status === 'Current');
  const history = sessions.filter(session => session.status !== 'Current');
  const rows = view === 'current' ? current : history;
  const hasThisSession = current.some(session => session.isCurrent);
  return <Stack>
    <Group justify="space-between" align="start"><Text size="sm" c="dimmed">Signed-in browsers and consoles. Last used reflects authenticated requests, not whether a device is online.</Text>
      <Group gap="xs">{onRevokeOthers && hasThisSession && <Button variant="default" disabled={pending || !current.some(session => !session.isCurrent)} onClick={() => { if (window.confirm('Revoke all other sessions and keep this one signed in?')) onRevokeOthers(); }}>Revoke other sessions</Button>}
        <Button color="red" variant="light" disabled={pending || !current.length} onClick={() => { if (window.confirm('Sign this account out of all ROMD clients, including this session?')) onRevoke(null); }}>Revoke all sessions</Button></Group>
    </Group>
    <Tabs value={view} onChange={setView}><Tabs.List><Tabs.Tab value="current">Sessions ({current.length})</Tabs.Tab><Tabs.Tab value="history">History ({history.length})</Tabs.Tab></Tabs.List></Tabs>
    {!rows.length ? <Text c="dimmed" size="sm">{view === 'current' ? 'No current sessions.' : 'No ended sessions recorded.'}</Text> : <Table.ScrollContainer minWidth={640}><Table verticalSpacing="md"><Table.Thead><Table.Tr><Table.Th>Session</Table.Th><Table.Th>Last used</Table.Th><Table.Th>Details</Table.Th><Table.Th>Action</Table.Th></Table.Tr></Table.Thead><Table.Tbody>{rows.map(session => <Table.Tr key={session.id}>
      <Table.Td><Group gap="xs"><Text fw={500} size="sm">{session.client}</Text>{session.isCurrent && <Badge color="teal" variant="light">This session</Badge>}</Group><Text size="sm" c="dimmed">{session.device || 'Device not recorded'}</Text>{session.status !== 'Current' && <Badge color="gray" variant="light" mt={4}>{session.status}</Badge>}</Table.Td>
      <Table.Td>{session.lastUsedAt ? new Date(session.lastUsedAt).toLocaleString() : 'Not recorded'}</Table.Td>
      <Table.Td><details><summary>Session details</summary><Stack gap={4} mt="xs"><Text size="xs">Started: {session.createdAt ? new Date(session.createdAt).toLocaleString() : 'Not recorded'}</Text><Text size="xs">Expires: {session.expiresAt ? new Date(session.expiresAt).toLocaleString() : 'Not recorded'}</Text>{session.revokedAt && <Text size="xs">Ended: {new Date(session.revokedAt).toLocaleString()}</Text>}{session.revocationReason && <Text size="xs">{session.revocationReason}</Text>}{session.revokedBy && <Text size="xs">Revoked by: {session.revokedBy}</Text>}</Stack></details></Table.Td>
      <Table.Td>{session.status === 'Current' && <Button size="compact-sm" variant="subtle" color="red" disabled={pending} onClick={() => { if (window.confirm(session.isCurrent ? 'Sign out of this session?' : `Revoke ${session.device || session.client}?`)) onRevoke(session.id); }}>{session.isCurrent ? 'Sign out' : 'Revoke'}</Button>}</Table.Td>
    </Table.Tr>)}</Table.Tbody></Table></Table.ScrollContainer>}
    <Text size="xs" c="dimmed">Activity is updated at most once every five minutes. Device descriptions are labels, not verified identities. Showing all current sessions and the latest 200 ended sessions.</Text>
    <Alert color="gray">Revocation blocks subsequent API requests and refreshes. Work already accepted by the server can finish.</Alert>
  </Stack>;
}
