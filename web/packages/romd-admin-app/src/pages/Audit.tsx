import { Alert, Button, Code, Group, Select, Skeleton, Stack, Table, Text, Title } from '@mantine/core';
import { getAdminAudit } from '@romd/admin-api-client';
import { useInfiniteQuery } from '@tanstack/react-query';
import { useSearchParams } from 'react-router';
import { apiError } from '../components/Administration/apiError';
import classes from '../components/Workspace/Workspace.module.css';

export function AuditEvents({ targetId, targetType }: { targetId?: string; targetType?: string }) {
  const query = useInfiniteQuery({ queryKey: ['admin-audit', targetId, targetType], initialPageParam: undefined as string | undefined, queryFn: async ({ pageParam }) => {
    const response = await getAdminAudit({ query: { before: pageParam, targetId, targetType } });
    if (response.error || !response.data) throw new Error(apiError(response.error, 'Could not load audit history.'));
    return response.data;
  }, getNextPageParam: page => page.nextCursor ?? undefined });
  const items = query.data?.pages.flatMap(page => page.items) ?? [];
  return <Stack><Group justify="space-between"><Text size="sm" c="dimmed">Saved administrative changes, newest first. History begins when auditing was enabled.</Text><Button variant="default" loading={query.isFetching && !query.isFetchingNextPage} onClick={() => void query.refetch()}>Refresh history</Button></Group>
    {query.isPending && <Skeleton height={220} />}{query.isError && <Alert color="red">{query.error.message}<Button variant="subtle" onClick={() => void query.refetch()}>Retry</Button></Alert>}
    {!!items.length && <Table.ScrollContainer minWidth={760}><Table verticalSpacing="md"><Table.Thead><Table.Tr><Table.Th>When</Table.Th><Table.Th>Actor</Table.Th><Table.Th>Action</Table.Th><Table.Th>Target</Table.Th><Table.Th>Changes</Table.Th></Table.Tr></Table.Thead><Table.Tbody>{items.map(item => <Table.Tr key={item.id}><Table.Td>{new Date(item.occurredAt).toLocaleString()}</Table.Td><Table.Td>{item.actorEmail ?? item.actorId}</Table.Td><Table.Td>{item.action}</Table.Td><Table.Td>{item.targetType}<Text size="xs" c="dimmed">{item.targetId}</Text></Table.Td><Table.Td>{item.changes !== '{}' ? <details><summary>Inspect changes</summary><Code block mt="xs">{JSON.stringify(JSON.parse(item.changes), null, 2)}</Code></details> : '—'}</Table.Td></Table.Tr>)}</Table.Tbody></Table></Table.ScrollContainer>}
    {query.isSuccess && !items.length && <Text c="dimmed" ta="center" py="xl">No recorded changes for this scope.</Text>}
    {query.hasNextPage && <Button variant="default" loading={query.isFetchingNextPage} onClick={() => void query.fetchNextPage()}>Load older changes</Button>}
  </Stack>;
}
export function Audit() {
  const [params, setParams] = useSearchParams();
  const targetType = params.get('type') ?? undefined;
  return <Stack className={classes.page} gap="lg"><div className={classes.hero}><Title order={1} className={classes.heading}>Audit log</Title></div><Select label="Scope" placeholder="All administrative changes" clearable value={targetType ?? null} data={['User', 'Integration', 'Automation', 'System metadata', 'Region', 'Language']} onChange={value => setParams(value ? { type: value } : {})} maw={320} /><AuditEvents targetType={targetType} /></Stack>;
}
