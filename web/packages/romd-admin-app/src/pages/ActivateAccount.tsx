import { Alert, Anchor, Button, Paper, PasswordInput, Stack, Text, Title } from '@mantine/core';
import { redeemAccountLink } from '@romd/admin-api-client';
import { useEffect, useState } from 'react';
import { Link } from 'react-router';
import { apiError } from '../components/Administration/apiError';
import { workspaceActionProps } from '../components/Workspace/workspaceActions';

export function ActivateAccount() {
  const [token, setToken] = useState(() => window.location.hash.slice(1));
  const [password, setPassword] = useState('');
  const [confirm, setConfirm] = useState('');
  const [pending, setPending] = useState(false);
  const [complete, setComplete] = useState(false);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => { window.history.replaceState(window.history.state, '', window.location.pathname); }, []);
  const submit = async () => {
    if (password !== confirm) { setError('Passwords do not match.'); return; }
    setPending(true); setError(null);
    try {
      const response = await redeemAccountLink({ body: { token, password } });
      if (response.error) throw new Error(apiError(response.error, 'Could not set your password.'));
      setToken(''); setPassword(''); setConfirm(''); setComplete(true);
    } catch (error) { setError(apiError(error, 'Could not set your password.')); }
    finally { setPending(false); }
  };
  return <Paper withBorder radius="md" p="xl" maw={480} mx="auto" my="xl"><Stack>
    <Title order={1} size="h2">{complete ? 'Password saved' : 'Set your ROMD password'}</Title>
    {complete ? <><Text>Your account is ready. Sign in to the ROMD app or console you use. Admin portal access depends on your assigned role.</Text><Anchor component={Link} to="/login">Sign in to the admin portal</Anchor></> : !token ? <Alert color="yellow">Open the complete activation or recovery link supplied by your administrator. Ask for a new link if this one has already been used.</Alert> : <Stack component="form" onSubmit={event => { event.preventDefault(); void submit(); }}>
      <Text size="sm" c="dimmed">Use a unique password. Saving consumes this link and signs out existing ROMD sessions.</Text>
      <PasswordInput label="New password" autoComplete="new-password" required value={password} onChange={event => setPassword(event.currentTarget.value)} disabled={pending} /><PasswordInput label="Confirm password" autoComplete="new-password" required value={confirm} onChange={event => setConfirm(event.currentTarget.value)} disabled={pending} />
      {error && <Alert color="red">{error}</Alert>}<Button type="submit" {...workspaceActionProps} loading={pending} disabled={!password || !confirm}>Save password</Button>
    </Stack>}
  </Stack></Paper>;
}
