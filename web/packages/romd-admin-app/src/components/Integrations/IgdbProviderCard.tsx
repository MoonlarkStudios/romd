import {
  Alert,
  Badge,
  Button,
  Checkbox,
  Group,
  PasswordInput,
  Skeleton,
  Stack,
  Switch,
  Text,
  TextInput,
  Title,
} from '@mantine/core';
import {
  testIgdbProviderConnection,
  updateIgdbProviderSettings,
} from '@romd/admin-api-client';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useAuth } from '../../contexts/AuthContext';
import { UnsavedChanges } from '../Administration/UnsavedChanges';
import classes from '../Workspace/Workspace.module.css';
import { workspaceActionProps } from '../Workspace/workspaceActions';

import { igdbSettingsQuery } from './providerQueries';

const settingsKey = igdbSettingsQuery.queryKey;

interface Draft {
  revision: string;
  enabled: boolean;
  clientId: string;
  clientSecret: string;
  clearClientSecret: boolean;
}

function IgdbSettings() {
  const queryClient = useQueryClient();
  const query = useQuery(igdbSettingsQuery);
  const [draft, setDraft] = useState<Draft | null>(null);
  const [pending, setPending] = useState<'save' | 'test' | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);
  const settings = query.data;

  if (query.isError && !settings) {
    return (
      <Alert color="red" title="Could not load IGDB settings.">
        <Button mt="sm" variant="light" onClick={() => void query.refetch()} loading={query.isFetching}>
          Retry
        </Button>
      </Alert>
    );
  }
  if (!settings) return <Skeleton height={260} aria-label="Loading IGDB settings" />;

  const values: Draft = draft ?? {
    revision: settings.revision ?? '00000000-0000-0000-0000-000000000000',
    enabled: settings.enabled,
    clientId: settings.clientId ?? '',
    clientSecret: '',
    clearClientSecret: false,
  };
  const dirty = values.enabled !== settings.enabled ||
    values.clientId.trim() !== (settings.clientId ?? '') ||
    values.clientSecret.length > 0 || values.clearClientSecret;
  const replacingSecret = values.clientSecret.trim().length > 0;
  const needsReplacement = settings.hasClientSecret &&
    values.clientId.trim() !== (settings.clientId ?? '') &&
    !replacingSecret && !values.clearClientSecret;
  const missingCredentials = values.enabled &&
    (!values.clientId.trim() || (!replacingSecret && (!settings.hasClientSecret || values.clearClientSecret)));
  const disabled = settings.managedByDeployment || pending !== null;

  const edit = (patch: Partial<Draft>) => {
    setDraft({ ...values, ...patch });
    setSaved(false);
    setError(null);
  };

  const save = async () => {
    setPending('save');
    setError(null);
    setSaved(false);
    try {
      // Keep plaintext out of React Query's mutation cache; only the safe response is cached.
      const response = await updateIgdbProviderSettings({
        body: {
          revision: values.revision,
          enabled: values.enabled,
          clientId: values.clientId.trim() || null,
          clientSecret: replacingSecret ? values.clientSecret : null,
          clearClientSecret: values.clearClientSecret,
        },
      });
      if (response.error || !response.data) {
        setError(response.error?.detail ?? response.error?.title ?? 'Could not save IGDB settings.');
        return;
      }
      await queryClient.cancelQueries({ queryKey: settingsKey });
      queryClient.setQueryData(settingsKey, response.data);
      setDraft(null);
      setSaved(true);
    } catch {
      setError('Could not save IGDB settings. Try again.');
    } finally {
      setPending(null);
    }
  };

  const testConnection = async () => {
    setPending('test');
    setError(null);
    try {
      const response = await testIgdbProviderConnection();
      if (response.error || !response.data) {
        setError('Could not test the IGDB connection. Try again.');
        return;
      }
      await queryClient.cancelQueries({ queryKey: settingsKey });
      queryClient.setQueryData(settingsKey, response.data);
    } catch {
      setError('Could not test the IGDB connection. Try again.');
    } finally {
      setPending(null);
    }
  };

  return (
    <Stack gap="md"><UnsavedChanges dirty={dirty} />
      <Group justify="space-between">
        <Title order={2} className={classes.sectionHeading}>Connection settings</Title>
        <Group gap="xs">
          <Badge color={settings.isConfigured ? 'teal' : 'orange'}>
            {settings.isConfigured ? 'Configured' : 'Not configured'}
          </Badge>
          <Badge color={settings.enabled ? 'teal' : 'gray'}>
            {settings.enabled ? 'Enabled' : 'Disabled'}
          </Badge>
        </Group>
      </Group>
      <Text size="sm" c="dimmed">
        Connect IGDB using your Twitch application credentials. Saved changes apply to new
        enrichment runs. Existing title metadata is preserved when enrichment is disabled.
      </Text>
      {settings.managedByDeployment && (
        <Alert color="blue" title="Managed by deployment">
          IGDB credentials are supplied by deployment configuration. Remove both deployment
          credentials to manage IGDB here.
        </Alert>
      )}
      {settings.configurationError && <Alert color="orange">{settings.configurationError}</Alert>}
      <Switch
        label="Enable IGDB enrichment"
        checked={values.enabled}
        onChange={(event) => edit({ enabled: event.currentTarget.checked })}
        disabled={disabled}
      />
      <TextInput
        label="Twitch Client ID"
        value={values.clientId}
        onChange={(event) => edit({ clientId: event.currentTarget.value })}
        autoComplete="off"
        maxLength={256}
        disabled={disabled}
      />
      <PasswordInput
        label="Twitch Client Secret"
        description={settings.hasClientSecret
          ? 'A client secret is saved. Leave blank to keep it.'
          : 'Enter the client secret from your Twitch application.'}
        value={values.clientSecret}
        onChange={(event) => edit({ clientSecret: event.currentTarget.value })}
        autoComplete="new-password"
        maxLength={4096}
        disabled={disabled || values.clearClientSecret}
      />
      {settings.hasClientSecret && !settings.managedByDeployment && (
        <Checkbox
          label="Remove saved client secret"
          description="Disable enrichment before removing the secret, then save your changes."
          checked={values.clearClientSecret}
          onChange={(event) => edit({ clearClientSecret: event.currentTarget.checked, clientSecret: '' })}
          disabled={disabled}
        />
      )}
      {needsReplacement && <Text size="sm" c="orange">Enter a new client secret when changing the Client ID.</Text>}
      {missingCredentials && <Text size="sm" c="orange">Both credentials are required to enable IGDB enrichment.</Text>}
      {error && <Alert color="red">{error}</Alert>}
      {saved && <Text role="status" size="sm" c="teal">IGDB settings saved.</Text>}
      {settings.lastTestedAt && (
        <Alert color={settings.lastTestSucceeded ? 'teal' : 'red'} title={settings.lastTestSucceeded ? 'Connection successful' : 'Connection failed'}>
          <Text size="sm">{settings.lastTestMessage}</Text>
          <Text size="xs" c="dimmed">Last tested: {new Date(settings.lastTestedAt).toLocaleString()}</Text>
        </Alert>
      )}
      {!settings.lastTestedAt && <Text size="sm" c="dimmed">This configuration has not been tested.</Text>}
      {dirty && <Text size="sm" c="dimmed">Save your changes before testing the connection.</Text>}
      <Group justify="flex-end">
        <Button variant="light" {...workspaceActionProps} onClick={() => void testConnection()}
          loading={pending === 'test'} disabled={dirty || pending !== null || !settings.isConfigured}>
          Test connection
        </Button>
        {!settings.managedByDeployment && (
          <Button {...workspaceActionProps} onClick={() => void save()} loading={pending === 'save'}
            disabled={!dirty || disabled || needsReplacement || missingCredentials}>
            Save IGDB settings
          </Button>
        )}
      </Group>
    </Stack>
  );
}

export function IgdbProviderCard() {
  const { user } = useAuth();
  if (!user?.roles.includes('Admin')) return null;

  return (
    <section className={classes.panel}>
      <IgdbSettings />
    </section>
  );
}
