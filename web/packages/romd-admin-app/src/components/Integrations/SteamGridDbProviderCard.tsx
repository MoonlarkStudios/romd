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
  Title,
} from '@mantine/core';
import { replaceSteamGridDbSettings, testSteamGridDbConnection } from '@romd/admin-api-client';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useAuth } from '../../contexts/AuthContext';
import { invalidateEnrichmentAssessments } from '../../hooks/api/enrichmentWorkspaceCache';
import { ifMatch } from '../../hooks/api/titleConflict';
import { UnsavedChanges } from '../Administration/UnsavedChanges';
import classes from '../Workspace/Workspace.module.css';
import { workspaceActionProps } from '../Workspace/workspaceActions';

import { observedProviderSettings, steamGridDbSettingsQuery } from './providerQueries';

const settingsKey = steamGridDbSettingsQuery.queryKey;
interface Draft {
  revision: string;
  enabled: boolean;
  apiKey: string;
  clearApiKey: boolean;
}

function SettingsForm() {
  const cache = useQueryClient();
  const query = useQuery(steamGridDbSettingsQuery);
  const [draft, setDraft] = useState<Draft | null>(null);
  const [pending, setPending] = useState<'save' | 'test' | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);
  const settings = query.data;
  if (query.isError && !settings)
    return (
      <Alert color="red">
        Could not load SteamGridDB settings.{' '}
        <Button onClick={() => void query.refetch()}>Retry</Button>
      </Alert>
    );
  if (!settings)
    return (
      <Skeleton
        height={240}
        aria-label="Loading SteamGridDB settings"
      />
    );
  const values = draft ?? {
    revision: settings.revision,
    enabled: settings.enabled,
    apiKey: '',
    clearApiKey: false,
  };
  const dirty = values.enabled !== settings.enabled || !!values.apiKey || values.clearApiKey;
  const staleDraft = draft !== null && draft.revision !== settings.revision;
  const missingKey =
    values.enabled && !values.apiKey.trim() && (!settings.hasApiKey || values.clearApiKey);
  const disabled = settings.managedByDeployment || pending !== null;
  const edit = (patch: Partial<Draft>) => {
    setDraft({
      ...values,
      ...patch,
    });
    setError(null);
    setSaved(false);
  };
  const save = async () => {
    setPending('save');
    setError(null);
    setSaved(false);
    try {
      // Credential plaintext stays out of React Query's mutation cache.
      const response = await replaceSteamGridDbSettings({
        headers: ifMatch(values.revision),
        body: {
          enabled: values.enabled,
          apiKey: values.apiKey.trim() || null,
          clearApiKey: values.clearApiKey,
        },
      });
      if (response.error || !response.data) {
        setError(
          response.error?.detail ??
            'Could not save settings. Reload if another administrator changed them.',
        );
        if (response.response?.status === 412)
          await cache.invalidateQueries({
            queryKey: settingsKey,
          });
        return;
      }
      const accepted = observedProviderSettings(response.data, response.response);
      await cache.cancelQueries({
        queryKey: settingsKey,
      });
      cache.setQueryData(settingsKey, accepted);
      await Promise.all([
        cache.invalidateQueries({
          queryKey: [
            'artwork',
            'providers',
          ],
        }),
        invalidateEnrichmentAssessments(cache),
      ]);
      setDraft(null);
      setSaved(true);
    } catch {
      setError('Could not save SteamGridDB settings. Try again.');
    } finally {
      setPending(null);
    }
  };
  const test = async () => {
    const testedRevision = values.revision;
    if (testedRevision !== settings.revision) {
      setError('Settings changed. Reload saved settings before testing again.');
      return;
    }
    setPending('test');
    setError(null);
    try {
      const response = await testSteamGridDbConnection({
        headers: ifMatch(testedRevision),
      });
      if (response.error || !response.data) {
        setError(
          response.response?.status === 412
            ? 'Settings changed. Reload saved settings before testing again.'
            : 'Could not test the connection.',
        );
        if (response.response?.status === 412)
          await cache.invalidateQueries({
            queryKey: settingsKey,
          });
        return;
      }
      const accepted = observedProviderSettings(response.data, response.response);
      await cache.cancelQueries({
        queryKey: settingsKey,
      });
      cache.setQueryData(settingsKey, accepted);
      setDraft((current) =>
        current?.revision === testedRevision
          ? {
              ...current,
              revision: accepted.revision,
            }
          : current,
      );
    } catch {
      setError('Could not test the connection. Try again.');
    } finally {
      setPending(null);
    }
  };
  return (
    <Stack>
      <UnsavedChanges dirty={dirty} />
      <Group justify="space-between">
        <Title
          order={2}
          className={classes.sectionHeading}
        >
          Connection settings
        </Title>
        <Badge color={settings.isConfigured ? 'teal' : 'orange'}>
          {settings.isConfigured ? 'Configured' : 'Not configured'}
        </Badge>
      </Group>
      <Text
        size="sm"
        c="dimmed"
      >
        Browse posters and heroes, then import the artwork you choose. Downloaded artwork remains
        available when this provider is disabled.
      </Text>
      {settings.managedByDeployment && (
        <Alert
          color="blue"
          title="Managed by deployment"
        >
          SteamGridDB settings are supplied by deployment configuration.
        </Alert>
      )}
      {settings.configurationError && <Alert color="orange">{settings.configurationError}</Alert>}
      <Switch
        label="Enable SteamGridDB artwork"
        checked={values.enabled}
        disabled={disabled}
        onChange={(event) =>
          edit({
            enabled: event.currentTarget.checked,
          })
        }
      />
      <PasswordInput
        label="SteamGridDB API key"
        description={
          settings.hasApiKey
            ? 'An API key is saved. Leave blank to keep it.'
            : 'Create an API key in your SteamGridDB account preferences.'
        }
        autoComplete="new-password"
        value={values.apiKey}
        maxLength={4096}
        disabled={disabled || values.clearApiKey}
        onChange={(event) =>
          edit({
            apiKey: event.currentTarget.value,
          })
        }
      />
      {settings.hasApiKey && !settings.managedByDeployment && (
        <Checkbox
          label="Remove saved API key"
          checked={values.clearApiKey}
          disabled={disabled}
          onChange={(event) =>
            edit({
              clearApiKey: event.currentTarget.checked,
              apiKey: '',
            })
          }
        />
      )}
      {missingKey && (
        <Text
          c="orange"
          size="sm"
        >
          An API key is required to enable SteamGridDB.
        </Text>
      )}
      {error && (
        <Alert color="red">
          {error}
          <Button
            variant="subtle"
            onClick={() => {
              if (!dirty || window.confirm('Discard your draft and reload saved settings?')) {
                setDraft(null);
                void query.refetch();
              }
            }}
          >
            Reload settings
          </Button>
        </Alert>
      )}
      {saved && (
        <Text
          role="status"
          c="teal"
        >
          SteamGridDB settings saved.
        </Text>
      )}
      {settings.lastTestedAt && (
        <Alert
          color={settings.lastTestSucceeded ? 'teal' : 'red'}
          title={settings.lastTestSucceeded ? 'Connection successful' : 'Connection failed'}
        >
          {settings.lastTestMessage}
          <Text size="xs">Last tested: {new Date(settings.lastTestedAt).toLocaleString()}</Text>
        </Alert>
      )}
      {dirty && (
        <Text
          size="sm"
          c="dimmed"
        >
          Save your changes before testing the connection.
        </Text>
      )}
      <Group justify="flex-end">
        <Button
          variant="light"
          onClick={() => void test()}
          loading={pending === 'test'}
          disabled={dirty || staleDraft || pending !== null || !settings.isConfigured}
        >
          Test SteamGridDB connection
        </Button>
        {!settings.managedByDeployment && (
          <Button
            {...workspaceActionProps}
            onClick={() => void save()}
            loading={pending === 'save'}
            disabled={!dirty || disabled || missingKey}
          >
            Save SteamGridDB settings
          </Button>
        )}
      </Group>
    </Stack>
  );
}

export function SteamGridDbProviderCard() {
  const { user } = useAuth();
  if (!user?.roles.includes('Admin')) return null;
  return (
    <section className={classes.panel}>
      <SettingsForm />
    </section>
  );
}
