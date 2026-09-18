import { Alert, Badge, Button, Checkbox, Group, PasswordInput, Skeleton, Stack, Switch, Text, Title } from '@mantine/core';
import { testSteamGridDbConnection, updateSteamGridDbSettings } from '@romd/admin-api-client';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useAuth } from '../../contexts/AuthContext';
import { UnsavedChanges } from '../Administration/UnsavedChanges';
import classes from '../Workspace/Workspace.module.css';
import { workspaceActionProps } from '../Workspace/workspaceActions';

import { steamGridDbSettingsQuery } from './providerQueries';

const settingsKey = steamGridDbSettingsQuery.queryKey;
interface Draft {
  revision: string; enabled: boolean; apiKey: string; clearApiKey: boolean }

function SettingsForm() {
  const cache = useQueryClient();
  const query = useQuery(steamGridDbSettingsQuery);
  const [draft, setDraft] = useState<Draft | null>(null);
  const [pending, setPending] = useState<'save' | 'test' | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);
  const settings = query.data;
  if (query.isError && !settings) return <Alert color="red">Could not load SteamGridDB settings. <Button onClick={() => void query.refetch()}>Retry</Button></Alert>;
  if (!settings) return <Skeleton height={240} aria-label="Loading SteamGridDB settings" />;
  const values = draft ?? { revision: settings.revision, enabled: settings.enabled, apiKey: '', clearApiKey: false };
  const dirty = values.enabled !== settings.enabled || !!values.apiKey || values.clearApiKey;
  const missingKey = values.enabled && !values.apiKey.trim() && (!settings.hasApiKey || values.clearApiKey);
  const disabled = settings.managedByDeployment || pending !== null;
  const edit = (patch: Partial<Draft>) => { setDraft({ ...values, ...patch }); setError(null); setSaved(false); };
  const save = async () => {
    setPending('save'); setError(null); setSaved(false);
    try {
      // Credential plaintext stays out of React Query's mutation cache.
      const response = await updateSteamGridDbSettings({ body: {
        revision: values.revision, enabled: values.enabled,
        apiKey: values.apiKey.trim() || null, clearApiKey: values.clearApiKey,
      } });
      if (response.error || !response.data) {
        setError(response.error?.detail ?? 'Could not save settings. Reload if another administrator changed them.');
        return;
      }
      await cache.cancelQueries({ queryKey: settingsKey });
      cache.setQueryData(settingsKey, response.data);
      await cache.invalidateQueries({ queryKey: ['artwork', 'providers'] });
      setDraft(null); setSaved(true);
    } catch { setError('Could not save SteamGridDB settings. Try again.'); }
    finally { setPending(null); }
  };
  const test = async () => {
    setPending('test'); setError(null);
    try {
      const response = await testSteamGridDbConnection();
      if (response.error || !response.data) { setError('Could not test the connection.'); return; }
      await cache.cancelQueries({ queryKey: settingsKey });
      cache.setQueryData(settingsKey, response.data);
    } catch { setError('Could not test the connection. Try again.'); }
    finally { setPending(null); }
  };
  return <Stack><UnsavedChanges dirty={dirty} />
    <Group justify="space-between"><Title order={2} className={classes.sectionHeading}>Connection settings</Title><Badge color={settings.isConfigured ? 'teal' : 'orange'}>{settings.isConfigured ? 'Configured' : 'Not configured'}</Badge></Group>
    <Text size="sm" c="dimmed">Browse posters and heroes, then import the artwork you choose. Downloaded artwork remains available when this provider is disabled.</Text>
    {settings.managedByDeployment && <Alert color="blue" title="Managed by deployment">SteamGridDB settings are supplied by deployment configuration.</Alert>}
    {settings.configurationError && <Alert color="orange">{settings.configurationError}</Alert>}
    <Switch label="Enable SteamGridDB artwork" checked={values.enabled} disabled={disabled} onChange={(event) => edit({ enabled: event.currentTarget.checked })} />
    <PasswordInput label="SteamGridDB API key" description={settings.hasApiKey ? 'An API key is saved. Leave blank to keep it.' : 'Create an API key in your SteamGridDB account preferences.'}
      autoComplete="new-password" value={values.apiKey} maxLength={4096} disabled={disabled || values.clearApiKey} onChange={(event) => edit({ apiKey: event.currentTarget.value })} />
    {settings.hasApiKey && !settings.managedByDeployment && <Checkbox label="Remove saved API key" checked={values.clearApiKey} disabled={disabled}
      onChange={(event) => edit({ clearApiKey: event.currentTarget.checked, apiKey: '' })} />}
    {missingKey && <Text c="orange" size="sm">An API key is required to enable SteamGridDB.</Text>}
    {error && <Alert color="red">{error}<Button variant="subtle" onClick={() => { if (!dirty || window.confirm('Discard your draft and reload saved settings?')) { setDraft(null); void query.refetch(); } }}>Reload settings</Button></Alert>}
    {saved && <Text role="status" c="teal">SteamGridDB settings saved.</Text>}
    {settings.lastTestedAt && <Alert color={settings.lastTestSucceeded ? 'teal' : 'red'} title={settings.lastTestSucceeded ? 'Connection successful' : 'Connection failed'}>
      {settings.lastTestMessage}<Text size="xs">Last tested: {new Date(settings.lastTestedAt).toLocaleString()}</Text>
    </Alert>}
    {dirty && <Text size="sm" c="dimmed">Save your changes before testing the connection.</Text>}
    <Group justify="flex-end"><Button variant="light" onClick={() => void test()} loading={pending === 'test'} disabled={dirty || pending !== null || !settings.isConfigured}>Test SteamGridDB connection</Button>
      {!settings.managedByDeployment && <Button {...workspaceActionProps} onClick={() => void save()} loading={pending === 'save'} disabled={!dirty || disabled || missingKey}>Save SteamGridDB settings</Button>}</Group>
  </Stack>;
}

export function SteamGridDbProviderCard() {
  const { user } = useAuth();
  if (!user?.roles.includes('Admin')) return null;
  return <section className={classes.panel}><SettingsForm /></section>;
}
