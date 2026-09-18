import { Alert, Button, Group, Select, Skeleton, Stack, Text, Title } from '@mantine/core';
import { getPlatformFieldDefaults, type PlatformMetadataPolicyDto, setPlatformFieldDefaults } from '@romd/admin-api-client';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { apiError } from '../../components/Administration/apiError';
import { UnsavedChanges } from '../../components/Administration/UnsavedChanges';
import classes from '../../components/Workspace/Workspace.module.css';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import { usePermissions } from '../../hooks/usePermissions';

const fields = ['Description', 'Genre', 'Publisher', 'Developer', 'ReleaseDate', 'Players', 'Rating'];
function PolicyForm({ systemKey, initial }: { systemKey: string; initial: PlatformMetadataPolicyDto }) {
  const client = useQueryClient();
  const [baseline, setBaseline] = useState(initial);
  const [draft, setDraft] = useState(initial.defaults);
  const [pending, setPending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);
  const changed = fields.some(field => (draft[field] ?? '') !== (baseline.defaults[field] ?? ''));
  const reload = async () => {
    setPending(true);
    try {
      const response = await getPlatformFieldDefaults({ path: { systemKey } });
      if (response.error || !response.data) throw new Error(apiError(response.error, 'Could not load metadata policy.'));
      setBaseline(response.data); setDraft(response.data.defaults); setError(null); setSaved(false);
    } catch (error) { setError(apiError(error, 'Could not reload policy.')); }
    finally { setPending(false); }
  };
  const save = async () => {
    setPending(true); setError(null); setSaved(false);
    try {
      const response = await setPlatformFieldDefaults({ path: { systemKey }, body: { revision: baseline.revision, defaults: Object.fromEntries(fields.filter(field => (draft[field] ?? '') !== (baseline.defaults[field] ?? '')).map(field => [field, draft[field] || ''])) } });
      if (response.error) throw new Error(apiError(response.error, 'Could not save metadata policy.'));
      // Keep the submitted snapshot until an explicit reload; a background response never replaces a draft.
      setBaseline({ ...baseline, defaults: draft }); setSaved(true);
      await client.invalidateQueries({ queryKey: ['metadata-policy', systemKey] });
      const updated = await getPlatformFieldDefaults({ path: { systemKey } });
      if (updated.data) { setBaseline(updated.data); setDraft(updated.data.defaults); }
    } catch (error) { setError(apiError(error, 'Could not save policy.')); }
    finally { setPending(false); }
  };
  const sourcePriority = [...new Set(baseline.globalSourcePriority)];
  const options = [{ value: '', label: `Automatic (${sourcePriority.join(' → ') || 'available providers'})` }, ...sourcePriority.map(source => ({ value: source, label: source.toUpperCase() }))];
  for (const source of Object.values(draft)) if (source && !options.some(option => option.value === source)) options.push({ value: source, label: source });
  return <Stack>
    <UnsavedChanges dirty={changed} />
    <Text size="sm" c="dimmed">Applies to {baseline.affectedTitles.toLocaleString()} titles in this system. Title overrides and user-entered metadata take priority. A preferred provider falls back to automatic priority when it has no value.</Text>
    {fields.map(field => <Select key={field} label={field === 'ReleaseDate' ? 'Release date' : field} data={options} value={draft[field] ?? ''} disabled={pending} onChange={value => { setDraft(current => ({ ...current, [field]: value ?? '' })); setSaved(false); }} />)}
    {error && <Alert color="red" title="Policy not saved">{error}</Alert>}
    {saved && <Alert color="teal">Policy saved. A durable request to recalculate this system’s titles was queued.</Alert>}
    <Group justify="flex-end"><Button variant="default" disabled={pending} onClick={() => { if (!changed || window.confirm('Discard your changes and reload the saved policy?')) void reload(); }}>Reload saved policy</Button><Button {...workspaceActionProps} loading={pending} disabled={!changed} onClick={() => void save()}>Save policy</Button></Group>
  </Stack>;
}
export function MetadataPolicy({ systemKey }: { systemKey: string }) {
  const { canEditMetadataPolicy } = usePermissions();
  const query = useQuery({ queryKey: ['metadata-policy', systemKey], enabled: canEditMetadataPolicy, queryFn: async () => {
    const response = await getPlatformFieldDefaults({ path: { systemKey } });
    if (response.error || !response.data) throw new Error(apiError(response.error, 'Could not load metadata policy.'));
    return response.data;
  } });
  if (!canEditMetadataPolicy) return null;
  return <section className={classes.panel}><Title order={2} className={classes.sectionHeading} mb="md">Metadata policy</Title>
    {query.isPending && <Skeleton height={240} />}
    {query.isError && <Alert color="red">{query.error.message}<Button variant="subtle" onClick={() => void query.refetch()}>Retry</Button></Alert>}
    {query.data && <PolicyForm key={systemKey} systemKey={systemKey} initial={query.data} />}
  </section>;
}
