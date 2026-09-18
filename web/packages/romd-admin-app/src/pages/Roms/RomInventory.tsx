import { Alert, Anchor, Badge, Button, Group, Select, Skeleton, Stack, Table, Text, TextInput } from '@mantine/core';
import { useDebouncedValue } from '@mantine/hooks';
import { IconSearch } from '@tabler/icons-react';
import { Link, useSearchParams } from 'react-router';
import { type StatusFilter, useRomsList } from '../../hooks/api/useRomsList';
import { formatBytes } from '../../utils/format';

export function RomInventory() {
  const [params, setParams] = useSearchParams();
  const search = params.get('q') ?? '';
  const [term] = useDebouncedValue(search, 250);
  const status = ['cataloged', 'unrouted', 'unidentified'].includes(params.get('status') ?? '') ? params.get('status') as StatusFilter : undefined;
  const query = useRomsList({ status, search: term });
  const items = query.data?.pages.flatMap((page) => page.items).map((rom) => ({ ...rom, status: ({ cataloged: 'Cataloged', unrouted: 'Unrouted', unidentified: 'Unidentified' } as Record<string, string>)[rom.status.toLowerCase()] ?? rom.status })) ?? [];
  const change = (key: string, value: string) => { const next = new URLSearchParams(params); if (value) next.set(key, value); else next.delete(key); setParams(next, { replace: true }); };
  return <Stack gap="md">
    <Group align="flex-end"><TextInput aria-label="Search ROM filenames" placeholder="Search filenames..." maxLength={200} value={search} onChange={(event) => change('q', event.currentTarget.value)} leftSection={<IconSearch size={16} />} style={{ flex: 1, minWidth: 180 }} /><Select aria-label="File status" value={status ?? 'all'} onChange={(value) => change('status', value === 'all' ? '' : value ?? '')} data={[{ value: 'all', label: 'Any status' }, { value: 'cataloged', label: 'Cataloged' }, { value: 'unrouted', label: 'Unrouted' }, { value: 'unidentified', label: 'Unidentified' }]} /></Group>
    {query.isPending ? <Skeleton height={160} /> : <>
      {query.isError && <Alert color="red" title="Files could not be loaded"><Button variant="subtle" onClick={() => void query.refetch()}>Retry files</Button></Alert>}
      {!!items.length && <Table.ScrollContainer minWidth={650}><Table verticalSpacing="sm" highlightOnHover layout="fixed" aria-label="ROM files"><Table.Thead><Table.Tr><Table.Th>Filename</Table.Th><Table.Th w={130}>Status</Table.Th><Table.Th w={110}>Size</Table.Th><Table.Th w={130}>Imported</Table.Th></Table.Tr></Table.Thead><Table.Tbody>{items.map((rom) => <Table.Tr key={rom.id}><Table.Td><Anchor component={Link} to={`/roms/${rom.id}?${params}`} size="sm" ta="left" style={{ overflowWrap: 'anywhere' }}>{rom.originalFilename}</Anchor></Table.Td><Table.Td><Badge size="xs" color={rom.status === 'Cataloged' ? 'teal' : rom.status === 'Unidentified' ? 'orange' : 'gray'}>{rom.status}</Badge></Table.Td><Table.Td>{formatBytes(rom.size)}</Table.Td><Table.Td><Text size="sm" c="dimmed">{rom.importedAt ? new Date(rom.importedAt).toLocaleDateString() : 'Unknown'}</Text></Table.Td></Table.Tr>)}</Table.Tbody></Table></Table.ScrollContainer>}
      {!query.isError && !items.length && <Text c="dimmed" py="xl">{search || status ? 'No files match these filters.' : 'No ROM files have been imported yet.'}</Text>}
      {query.hasNextPage && <Button variant="light" color="teal" loading={query.isFetchingNextPage} onClick={() => void query.fetchNextPage()}>Load more files</Button>}
      {!!items.length && <Text size="xs" c="dimmed">{items.length.toLocaleString()} {items.length === 1 ? 'file' : 'files'} shown{query.hasNextPage ? ' (more available)' : ''}</Text>}
    </>}
  </Stack>;
}
