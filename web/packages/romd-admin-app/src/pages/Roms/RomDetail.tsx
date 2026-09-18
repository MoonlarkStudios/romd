import { ActionIcon, Alert, Anchor, Badge, Button, Group, Menu, Modal, SimpleGrid, Skeleton, Stack, Table, Text, Title } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { getRomById } from '@romd/admin-api-client';
import { IconArrowLeft, IconDots, IconDownload, IconTrash } from '@tabler/icons-react';
import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { Link, useNavigate, useParams, useSearchParams } from 'react-router';
import { HashRow } from '../../components/HashRow';
import workspace from '../../components/Workspace/Workspace.module.css';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import { romKeys, useDeleteRom } from '../../hooks/api/useRoms';
import { usePermissions } from '../../hooks/usePermissions';
import { authenticatedDownload } from '../../utils/download';
import { formatBytes } from '../../utils/format';

export function RomDetail() {
  const { romId = '' } = useParams();
  const [params] = useSearchParams();
  const backParams = new URLSearchParams(params);
  if (!backParams.has('tab')) backParams.set('tab', 'all');
  const back = `/roms?${backParams}`;
  const navigate = useNavigate();
  const { canManageTitles } = usePermissions();
  const deletion = useDeleteRom();
  const [confirmDelete, setConfirmDelete] = useState(false);
  const query = useQuery({ queryKey: romKeys.detail(romId), queryFn: async ({ signal }) => {
    const response = await getRomById({ path: { romId }, signal });
    if (response.response?.status === 404) return null;
    if (response.error || !response.data) throw new Error('File details could not be loaded.');
    return response.data;
  } });
  const rom = query.data;
  const matches = rom?.matches ?? [];
  const groups = new Map<string, typeof matches>();
  for (const match of matches) {
    const key = JSON.stringify([match.systemKey, match.titleId, match.datId, match.datGameId ?? match.gameName]);
    const group = groups.get(key) ?? [];
    group.push(match);
    groups.set(key, group);
  }
  return <div className={workspace.page}><Stack gap="lg">
    <Button component={Link} to={back} variant="subtle" color="gray" p={0} w="fit-content" leftSection={<IconArrowLeft size={15} />}>Back to ROMs</Button>
    {query.isPending ? <Skeleton height={240} /> : query.isError ? <Alert color="red" title="File details could not be loaded"><Button variant="subtle" onClick={() => void query.refetch()}>Retry file</Button></Alert> : !rom ? <Alert title="ROM file not found">It may have been deleted.</Alert> : <>
      <Group justify="space-between" align="flex-start" className={workspace.hero}><Stack gap="xs" style={{ minWidth: 0, flex: '1 1 480px' }}><Title order={1} style={{ overflowWrap: 'anywhere', fontSize: 18, fontWeight: 600, lineHeight: 1.5 }}>{rom.originalFilename}</Title></Stack><Group gap="xs"><Button {...workspaceActionProps} variant="light" leftSection={<IconDownload size={16} />} onClick={() => void authenticatedDownload(`/api/roms/${rom.id}/download`, rom.originalFilename).catch(() => notifications.show({ color: 'red', message: 'Download failed. Please try again.' }))}>Download</Button>{canManageTitles && <Menu position="bottom-end"><Menu.Target><ActionIcon aria-label="File actions" title="File actions" variant="subtle" color="gray"><IconDots size={18} /></ActionIcon></Menu.Target><Menu.Dropdown><Menu.Item color="red" leftSection={<IconTrash size={16} />} onClick={() => { deletion.reset(); setConfirmDelete(true); }}>Delete file</Menu.Item></Menu.Dropdown></Menu>}</Group></Group>
      <section><Stack gap="md"><Title order={2} className={workspace.sectionHeading}>File details</Title>
        <SimpleGrid cols={{ base: 2, sm: 3 }} spacing="md">
          <Stack gap={5}><Text size="xs" c="dimmed">Status</Text><Badge w="fit-content" color={rom.status.toLowerCase() === 'cataloged' ? 'teal' : 'gray'}>{rom.status}</Badge></Stack>
          <Stack gap={5}><Text size="xs" c="dimmed">Size</Text><Text size="sm">{formatBytes(rom.size)}</Text></Stack>
          <Stack gap={5}><Text size="xs" c="dimmed">Imported</Text><Text size="sm">{rom.importedAt ? new Date(rom.importedAt).toLocaleString() : 'Date unavailable'}</Text></Stack>
        </SimpleGrid>
      </Stack></section>
      <section><Stack gap="md"><Title order={2} className={workspace.sectionHeading}>Checksums</Title>
        <Stack gap="sm" maw={740}>
          <HashRow label="SHA1" value={rom.sha1} />
          <HashRow label="MD5" value={rom.md5} />
          <HashRow label="CRC32" value={rom.crc32} />
        </Stack>
      </Stack></section>
      <section><Stack gap="md"><Title order={2} className={workspace.sectionHeading}>Matches</Title>
        {!matches.length ? <Text c="dimmed" size="sm">No DAT matches are recorded for this file.</Text> :
          <Table.ScrollContainer minWidth={760}><Table aria-label="ROM matches" verticalSpacing="md" layout="fixed" highlightOnHover>
            <Table.Thead><Table.Tr><Table.Th w="20%">System</Table.Th><Table.Th w="18%">Title</Table.Th><Table.Th w="37%">Release / DAT entry</Table.Th><Table.Th w="25%">Source</Table.Th></Table.Tr></Table.Thead>
            <Table.Tbody>{Array.from(groups, ([id, entries]) => {
              const match = entries[0];
              const filenames = Array.from(new Set(entries.map((entry) => entry.romName))).filter((name) => name !== rom.originalFilename);
              return <Table.Tr key={id} style={{ verticalAlign: 'top', overflowWrap: 'anywhere' }}>
                <Table.Td><Text size="sm">{match.platformName ?? 'Unassigned system'}</Text></Table.Td>
                <Table.Td>{match.titleId ? <Anchor component={Link} to={`/titles/${match.titleId}`} size="sm">{match.titleName ?? 'Open title'}</Anchor> : <Text size="sm" c="dimmed">No title assigned</Text>}</Table.Td>
                <Table.Td><Stack gap={5}>{match.titleId && match.datGameId ? <Anchor component={Link} size="sm" to={`/titles/${match.titleId}?tab=releases&release=${match.datGameId}`}>{match.gameName}</Anchor> : <Text size="sm">{match.gameName}</Text>}{filenames.map((name) => <Text key={name} size="xs" c="dimmed">Expected file: {name}</Text>)}</Stack></Table.Td>
                <Table.Td>{match.systemKey ? <Anchor component={Link} size="sm" to={`/systems/${match.systemKey}?tab=sources&dat=${match.datId}${match.titleId ? `&sourceTitle=${match.titleId}` : ''}`}>{match.datName}</Anchor> : <Text size="sm">{match.datName}</Text>}</Table.Td>
              </Table.Tr>;
            })}</Table.Tbody>
          </Table></Table.ScrollContainer>}
      </Stack></section>
      <Modal opened={confirmDelete} onClose={() => { if (!deletion.isPending) setConfirmDelete(false); }} title="Delete ROM file?" centered><Stack><Text size="sm" style={{ overflowWrap: 'anywhere' }}>{rom.originalFilename}</Text><Text size="sm">This removes the stored ROM association and affects every release that uses it. This cannot be undone.</Text>{deletion.isError && <Alert color="red">The file could not be deleted. Try again.</Alert>}<Group justify="flex-end"><Button variant="default" disabled={deletion.isPending} onClick={() => setConfirmDelete(false)}>Cancel</Button><Button color="red" loading={deletion.isPending} onClick={() => deletion.mutate(rom.id, { onSuccess: () => navigate(back, { replace: true }) })}>Delete file</Button></Group></Stack></Modal>
    </>}
  </Stack></div>;
}
