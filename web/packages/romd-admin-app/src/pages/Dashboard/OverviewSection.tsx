import { Alert, Button, Group, Skeleton, Stack, Text, Title } from '@mantine/core';
import type { ReactNode } from 'react';
import workspace from '../../components/Workspace/Workspace.module.css';

type QueryState = { isPending: boolean; isError: boolean; dataUpdatedAt: number; refetch: () => unknown };
export function OverviewSection({ title, query, children, action }: { title: string; query: QueryState; children: ReactNode; action?: ReactNode }) {
  return <Stack className={workspace.panel} gap="md">
    <Group justify="space-between"><Title order={2} className={workspace.sectionHeading}>{title}</Title>{action}</Group>
    {query.isPending ? <Skeleton height={90} /> : query.isError ? <Alert color="red" title={`${title} unavailable`}>This section could not be refreshed. <Button variant="subtle" color="red" onClick={() => void query.refetch()}>Retry</Button></Alert> : children}
    {!query.isPending && !query.isError && query.dataUpdatedAt > 0 && <Text size="xs" c="dimmed">Updated {new Date(query.dataUpdatedAt).toLocaleTimeString()}</Text>}
  </Stack>;
}
