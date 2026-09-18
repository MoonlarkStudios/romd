import { Alert, Button, Group, Modal, PasswordInput, Select, Stack, Switch, Text, TextInput } from '@mantine/core';
import { createUser } from '@romd/admin-api-client';
import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useNavigate } from 'react-router';
import { useLibraries, userKeys, userRoleOptions } from '../../hooks/api/useUsers';
import { apiError } from '../Administration/apiError';
import { workspaceActionProps } from '../Workspace/workspaceActions';

export function CreateAccount({ onClose }: { onClose: () => void }) {
  const libraries = useLibraries();
  const client = useQueryClient();
  const navigate = useNavigate();
  const [email, setEmail] = useState('');
  const [role, setRole] = useState('User');
  const [libraryId, setLibraryId] = useState<string | null>(null);
  const [activation, setActivation] = useState(true);
  const [password, setPassword] = useState('');
  const [pending, setPending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const submit = async () => {
    setPending(true); setError(null);
    try {
      const response = await createUser({ body: { email, role, libraryId, password: activation ? '' : password, requiresActivation: activation } });
      if (response.error || !response.data) throw new Error(apiError(response.error, 'Could not create account.'));
      setPassword('');
      await client.invalidateQueries({ queryKey: userKeys.all });
      onClose(); navigate(`/users/${response.data.id}?tab=security`);
    } catch (error) { setError(apiError(error, 'Could not create account.')); }
    finally { setPending(false); }
  };
  return <Modal opened onClose={() => { if (!pending && (!email || window.confirm('Discard this account draft?'))) onClose(); }} title="Create account" centered>
    <Stack component="form" onSubmit={event => { event.preventDefault(); void submit(); }}>
      <TextInput type="email" label="Email" required value={email} onChange={event => setEmail(event.currentTarget.value)} disabled={pending} />
      <Select label="Role" data={userRoleOptions} value={role} onChange={value => setRole(value ?? 'User')} disabled={pending} />
      <Select label="Consumer library" placeholder="Installation default, if configured" clearable data={(libraries.data ?? []).filter(library => library.configurationState === 'Valid').map(library => ({ value: library.id, label: library.name }))} value={libraryId} onChange={setLibraryId} disabled={pending || libraries.isPending || libraries.isError} />
      {libraries.isError && <Alert color="red">Libraries could not be loaded. <Button variant="subtle" onClick={() => void libraries.refetch()}>Retry</Button></Alert>}
      <Switch label="Require activation" checked={activation} onChange={event => setActivation(event.currentTarget.checked)} disabled={pending} />
      {activation ? <Text size="sm" c="dimmed">Create a pending account, then generate a link from its Security tab. The recipient chooses their own password. ROMD does not send email.</Text> : <PasswordInput label="Initial password" required autoComplete="new-password" value={password} onChange={event => setPassword(event.currentTarget.value)} disabled={pending} description="This password works immediately and does not expire automatically." />}
      {error && <Alert color="red">{error}</Alert>}
      <Group justify="flex-end"><Button variant="default" onClick={onClose} disabled={pending}>Cancel</Button><Button type="submit" {...workspaceActionProps} loading={pending} disabled={!email || libraries.isError || libraries.isPending || (!activation && !password)}>Create account</Button></Group>
    </Stack>
  </Modal>;
}
