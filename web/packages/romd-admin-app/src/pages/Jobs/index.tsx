import { Alert, Button, Group, NativeSelect, Skeleton, Stack, Text, TextInput, Title } from '@mantine/core';
import { useDebouncedValue } from '@mantine/hooks';
import { IconRefresh, IconSearch } from '@tabler/icons-react';
import { useMemo, useRef } from 'react';
import { useSearchParams } from 'react-router';
import { jobOutcomes } from '../../components/Jobs/jobTypeRegistry';
import workspace from '../../components/Workspace/Workspace.module.css';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import { useJobHistory } from '../../hooks/api/useJobHistory';
import { useJobOperations } from '../../hooks/useJobOperations';
import { JobsTable } from './JobsTable';

export function Jobs() {
  const [params, setParams] = useSearchParams();
  const pendingParams = useRef(params);
  pendingParams.current = params;
  const [search] = useDebouncedValue(params.get('search') ?? '', 250);
  const filters = useMemo(() => ({
    search: search || undefined,
    outcome: params.get('outcome') || undefined,
    jobType: params.get('type') || undefined,
    archive: params.get('archive') || 'active',
    from: params.get('from') ? `${params.get('from')}T00:00:00Z` : undefined,
    before: params.get('before') ? `${params.get('before')}T00:00:00Z` : undefined,
    cursor: params.get('cursor') || undefined,
    limit: 50,
  }), [params, search]);
  const query = useJobHistory(filters);
  const operations = useJobOperations();
  const update = (key: string, value: string) => {
    const next = new URLSearchParams(pendingParams.current);
    if (value) next.set(key, value); else next.delete(key);
    if (key !== 'cursor') next.delete('cursor');
    pendingParams.current = next;
    setParams(next, { replace: key === 'search' });
  };
  return <Stack className={workspace.page} gap="lg">
    <Group className={workspace.hero} justify="space-between"><Title order={1} className={workspace.heading}>Jobs</Title><Button {...workspaceActionProps} variant="light" leftSection={<IconRefresh size={16} />} loading={query.isFetching} onClick={() => void query.refetch()}>Refresh</Button></Group>
    <fieldset disabled={operations.actionsPending} style={{ border: 0, padding: 0, margin: 0 }}><Stack gap="md"><Group align="flex-end">
      <TextInput label="Search jobs" placeholder="Name, job ID, or correlation ID" leftSection={<IconSearch size={16} />} value={params.get('search') ?? ''} onChange={(event) => update('search', event.currentTarget.value)} maxLength={200} style={{ flex: '1 1 260px' }} />
      <NativeSelect label="Outcome" value={params.get('outcome') ?? ''} onChange={(event) => update('outcome', event.currentTarget.value)} data={[{ value: '', label: 'All outcomes' }, ...Object.entries(jobOutcomes).filter(([value]) => value !== 'unknown').map(([value, meta]) => ({ value, label: meta.label }))]} />
      <NativeSelect label="Job type" value={params.get('type') ?? ''} onChange={(event) => update('type', event.currentTarget.value)} data={[{ value: '', label: 'All types' }, { value: 'upload', label: 'File import' }, { value: 'replace_dat', label: 'Source replacement' }, { value: 'enrichment', label: 'Title enrichment' }, { value: 'bulk_enrichment', label: 'Bulk enrichment' }, { value: 'export', label: 'Export' }, { value: 'materialization', label: 'Library materialization' }, { value: 'artwork-import', label: 'Artwork import' }]} />
      <NativeSelect label="History" value={params.get('archive') ?? 'active'} onChange={(event) => update('archive', event.currentTarget.value)} data={[{ value: 'active', label: 'Unarchived' }, { value: 'archived', label: 'Archived' }, { value: 'all', label: 'All history' }]} />
    </Group>
    <Group align="flex-end"><TextInput type="date" label="Created from (UTC)" value={params.get('from') ?? ''} onChange={(event) => update('from', event.currentTarget.value)} /><TextInput type="date" label="Created before (UTC)" value={params.get('before') ?? ''} onChange={(event) => update('before', event.currentTarget.value)} /><Button variant="subtle" color="gray" onClick={() => { pendingParams.current = new URLSearchParams(); setParams({}); }}>Reset filters</Button></Group></Stack></fieldset>
    <Text size="xs" c="dimmed">History refreshes every 15 seconds. Archived jobs are retained for 30 days before scheduled deletion. Dates filter job creation time.</Text>
    {query.isError && <Alert color="red" title={query.data ? 'Could not refresh jobs' : 'Could not load jobs'}>{query.error.message}. {query.data ? 'Showing the previous page.' : 'Review filters or refresh to try again.'}</Alert>}
    {query.isPending ? <Stack role="status" aria-label="Loading jobs">{[0, 1, 2].map((id) => <Skeleton key={id} h={90} />)}</Stack> : query.data && <>
      <JobsTable key={params.toString()} jobs={query.data.items} operations={operations} updatedAt={query.dataUpdatedAt} />
      <Group justify="space-between"><Button variant="default" disabled={operations.actionsPending || !params.has('cursor')} onClick={() => update('cursor', '')}>Newest results</Button><Button {...workspaceActionProps} variant="light" disabled={operations.actionsPending || !query.data.nextCursor || query.isFetching || search !== (params.get('search') ?? '')} onClick={() => update('cursor', query.data?.nextCursor ?? '')}>Older results</Button></Group>
    </>}
  </Stack>;
}
