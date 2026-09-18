import { ActionIcon, Alert, Anchor, Badge, Button, Group, Loader, Menu, Select, Stack, Table, Text, TextInput } from '@mantine/core';
import { useDebouncedValue } from '@mantine/hooks';
import { notifications } from '@mantine/notifications';
import type { JobItemDto } from '@romd/admin-api-client';
import { IconChevronLeft, IconChevronRight, IconDownload, IconSearch } from '@tabler/icons-react';
import { useState } from 'react';
import { Link } from 'react-router';
import { useJobItems } from '../../hooks/api/useJobItems';
import { authenticatedDownload } from '../../utils/download';
import { formatBytes } from '../../utils/format';

const OUTCOMES: Record<string, { label: string; color: string }> = {
  ingested: { label: 'Stored', color: 'teal' }, deduplicated: { label: 'Already stored', color: 'gray' },
  rejected: { label: 'Not stored', color: 'orange' }, failed: { label: 'Failed', color: 'red' },
  dat_routed: { label: 'Catalog imported', color: 'teal' }, dat_unrouted: { label: 'Needs routing', color: 'orange' },
};

function Matches({ item }: { item: JobItemDto }) {
  if (item.kind === 'dat') return item.systemKey
    ? <Anchor component={Link} size="sm" to={`/systems/${item.systemKey}?tab=sources&dat=${item.datFileId}`}>{item.platformName ?? 'Open system'}</Anchor>
    : <Text size="sm" c="dimmed">{item.outcome === 'dat_unrouted' ? 'No system assigned' : 'No match recorded'}</Text>;
  return <Stack gap={4}>
    {item.matchedTitles.map((title) => <Anchor key={title.id} component={Link} size="sm" to={`/titles/${title.id}`}>{title.name}</Anchor>)}
    <Text size="xs" c="dimmed">{item.platformName ?? (item.outcome === 'rejected' ? 'No catalog match' : 'No title match')}</Text>
  </Stack>;
}

export function ProvenanceLedger({ jobId }: { jobId: string }) {
  const [filter, setFilter] = useState('all');
  const [search, setSearch] = useState('');
  const [term] = useDebouncedValue(search, 250);
  const [paging, setPaging] = useState<{ key: string; cursors: (string | undefined)[] }>({ key: '', cursors: [undefined] });
  const key = `${jobId}:${filter}:${term}`;
  const cursors = paging.key === key ? paging.cursors : [undefined];
  const query = useJobItems(jobId, filter === 'all' ? undefined : filter, { cursor: cursors.at(-1), search: term || undefined });
  const exportItems = async (format: 'csv' | 'json') => {
    try { await authenticatedDownload(`/api/jobs/${jobId}/items/export?format=${format}`, `import.${format}`); }
    catch { notifications.show({ color: 'red', message: 'Export failed.' }); }
  };
  return <Stack gap="md">
    <Group gap="sm">
      <TextInput aria-label="Search imported files" placeholder="Search file paths" leftSection={<IconSearch size={16} />} value={search} maxLength={200} onChange={(event) => setSearch(event.currentTarget.value)} style={{ flex: '1 1 220px' }} />
      <Select aria-label="Filter outcomes" value={filter} onChange={(value) => setFilter(value ?? 'all')} data={[{ value: 'all', label: 'All outcomes' }, ...Object.entries(OUTCOMES).map(([value, meta]) => ({ value, label: meta.label }))]} w={180} />
      <Menu><Menu.Target><ActionIcon variant="subtle" color="gray" aria-label="Export details"><IconDownload size={18} /></ActionIcon></Menu.Target><Menu.Dropdown>
        <Menu.Item onClick={() => void exportItems('csv')}>Download CSV</Menu.Item><Menu.Item onClick={() => void exportItems('json')}>Download JSON</Menu.Item>
      </Menu.Dropdown></Menu>
    </Group>
    {query.isError ? <Alert color="red" title="Could not load file results"><Button variant="subtle" onClick={() => void query.refetch()}>Retry results</Button></Alert> : query.isPending ? <Group justify="center" py="lg"><Loader size="sm" /></Group> : !query.data?.items.length ? <Text size="sm" c="dimmed" py="lg">No files for this filter.</Text> : <>
      <Table.ScrollContainer minWidth={650}><Table layout="fixed" verticalSpacing="sm" highlightOnHover aria-label="Import results">
        <Table.Thead><Table.Tr><Table.Th w="48%">File</Table.Th><Table.Th w="20%">Outcome</Table.Th><Table.Th w="32%">Matches</Table.Th></Table.Tr></Table.Thead>
        <Table.Tbody>{query.data.items.map((item) => { const meta = OUTCOMES[item.outcome] ?? { label: item.outcome, color: 'gray' }; return <Table.Tr key={item.id} style={{ verticalAlign: 'top', overflowWrap: 'anywhere' }}>
          <Table.Td><Stack gap={4}>{item.romFileId ? <Anchor component={Link} size="sm" to={`/roms/${item.romFileId}?tab=all`}>{item.fileName}</Anchor> : <Text size="sm">{item.fileName}</Text>}<Text size="xs" c="dimmed">{formatBytes(item.sizeBytes)}</Text>{item.error && <Text size="xs" c="red">{item.error}</Text>}</Stack></Table.Td>
          <Table.Td><Badge size="sm" variant="light" color={meta.color}>{meta.label}</Badge></Table.Td>
          <Table.Td><Matches item={item} /></Table.Td>
        </Table.Tr>; })}</Table.Tbody>
      </Table></Table.ScrollContainer>
      <Group justify="space-between"><Text size="xs" c="dimmed">Page {cursors.length} · {query.data.items.length} {query.data.items.length === 1 ? 'file' : 'files'}</Text><Group gap="xs">
        <Button variant="default" size="xs" leftSection={<IconChevronLeft size={14} />} disabled={cursors.length === 1} onClick={() => setPaging({ key, cursors: cursors.slice(0, -1) })}>Previous</Button>
        <Button variant="default" size="xs" rightSection={<IconChevronRight size={14} />} disabled={!query.data.nextCursor} onClick={() => setPaging({ key, cursors: [...cursors, query.data?.nextCursor ?? undefined] })}>Next</Button>
      </Group></Group>
    </>}
  </Stack>;
}
