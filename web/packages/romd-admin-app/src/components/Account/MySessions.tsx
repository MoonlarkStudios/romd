import { Alert, Button, Skeleton, Stack, Title } from '@mantine/core';
import { getMyAccountSessions, revokeMyAccountSessions } from '@romd/admin-api-client';
import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { useAuth } from '../../contexts/AuthContext';
import { apiError } from '../Administration/apiError';
import { AccountSessions } from '../Users/AccountSessions';
import classes from '../Workspace/Workspace.module.css';
export function MySessions() {
  const { logout } = useAuth();
  const [pending, setPending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const query = useQuery({ queryKey: ['my-account-sessions'], queryFn: async () => {
    const response = await getMyAccountSessions();
    if (response.error || !response.data) throw new Error(apiError(response.error, 'Could not load sessions.'));
    return response.data;
  } });
  const revoke = async (sessionId: string | null, othersOnly = false) => {
    setPending(true); setError(null);
    try { const response = await revokeMyAccountSessions({ query: { sessionId: sessionId ?? undefined, othersOnly } }); if (response.error) throw new Error(apiError(response.error, 'Could not revoke sessions.')); if (!othersOnly && (sessionId === null || query.data?.sessions.some(session => session.id === sessionId && session.isCurrent))) await logout();
      else await query.refetch(); }
    catch (error) { setError(apiError(error, 'Could not revoke sessions.')); }
    finally { setPending(false); }
  };
  return <Stack className={classes.panel}><Title order={2} className={classes.sectionHeading}>My sessions</Title>{query.isPending && <Skeleton height={140} />}{(query.isError || error) && <Alert color="red">{error ?? query.error?.message}<Button variant="subtle" onClick={() => void query.refetch()}>Retry</Button></Alert>}{query.data && <AccountSessions sessions={query.data.sessions} pending={pending} onRevokeOthers={() => void revoke(null, true)} onRevoke={id => void revoke(id)} />}</Stack>;
}
