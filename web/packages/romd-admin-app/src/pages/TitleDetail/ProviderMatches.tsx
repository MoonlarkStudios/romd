import { ActionIcon, Alert, Anchor, Badge, Button, Group, Image, Menu, Modal, SegmentedControl, Skeleton, Stack, Text, TextInput, Tooltip } from '@mantine/core';
import { confirmTitleProviderMatch, getTitleProviderMatches, type ProviderGameDto, searchProviderMatches, setTitleProviderMatch, type TitleProviderMatchDto, unlinkTitleProviderMatch } from '@romd/admin-api-client';
import { IconCheck, IconDots, IconExternalLink, IconSearch, IconUnlink } from '@tabler/icons-react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';

export function useProviderMatches(titleId: string) {
  return useQuery({ queryKey: ['provider-matches', titleId], queryFn: async ({ signal }) => {
    const result = await getTitleProviderMatches({ path: { titleId }, signal });
    if (result.error || !result.data) throw new Error('Provider matches could not be loaded.');
    return result.data;
  }, staleTime: 30_000 });
}

function GamePreview({ game }: { game: ProviderGameDto }) {
  return <Group gap="sm" wrap="nowrap" align="center" style={{ minWidth: 0 }}>
    {game.thumbnailUrl && <Image src={game.thumbnailUrl} alt="" w={40} h={56} fit="contain" referrerPolicy="no-referrer" style={{ flexShrink: 0 }} />}
    <Stack gap={2} style={{ minWidth: 0 }}>
      <Anchor href={game.url} target="_blank" rel="noopener noreferrer" size="sm" style={{ overflowWrap: 'anywhere' }}>
        {game.name} <IconExternalLink size={13} aria-label="Open provider page" />
      </Anchor>
      <Text size="xs" c="dimmed" style={{ overflowWrap: 'anywhere' }}>
        {[game.year, ...(game.platforms ?? []), `#${game.id}`].filter(Boolean).join(' · ')}
      </Text>
    </Stack>
  </Group>;
}

export function ProviderMatchPicker({ titleId, titleName, row, onClose }: {
  titleId: string; titleName: string; row: TitleProviderMatchDto; onClose: () => void;
}) {
  const cache = useQueryClient();
  const [match, setMatch] = useState(row);
  const [conflicted, setConflicted] = useState(false);
  const [mode, setMode] = useState('search');
  const [text, setText] = useState(titleName);
  const [selected, setSelected] = useState<ProviderGameDto | null>(row.state === 'NeedsReview' ? row.game ?? null : null);
  const search = useMutation({ mutationFn: async () => {
    const result = await searchProviderMatches({ path: { providerId: row.providerId }, body: { query: text.trim(), resolve: mode === 'resolve' } });
    if (result.error || !result.data) throw new Error('Could not find provider games. Check the input or try again.');
    return result.data;
  } });
  const save = useMutation({ mutationFn: async () => {
    if (!selected) return;
    const result = await setTitleProviderMatch({ path: { titleId, providerId: row.providerId }, body: { expectedRevision: match.revision, externalId: selected.id } });
    if (result.error) {
      setConflicted(result.response.status === 409);
      throw new Error(result.response.status === 409
        ? 'The provider match changed. Reload the current match, then select and confirm your choice again.'
        : 'The match could not be saved. Check the provider configuration or try again.');
    }
    await cache.invalidateQueries({ queryKey: ['provider-matches', titleId] });
    await cache.invalidateQueries({ queryKey: ['artwork'] });
    onClose();
  } });
  const reload = useMutation({ mutationFn: async () => {
    const result = await getTitleProviderMatches({ path: { titleId } });
    if (result.error || !result.data) throw new Error('Provider matches could not be reloaded. Try again.');
    const latest = result.data.find(item => item.providerId === row.providerId);
    if (!latest) throw new Error('This provider is no longer enabled. Close the picker and check provider settings.');
    cache.setQueryData(['provider-matches', titleId], result.data);
    setMatch(latest);
    setSelected(null);
    search.reset();
    save.reset();
    setConflicted(false);
  } });
  return <Modal opened onClose={onClose} title={`Match ${row.providerName}`} size="lg">
    <Stack>
      <SegmentedControl value={mode} onChange={(value) => { setMode(value); setText(value === 'search' ? titleName : ''); setSelected(null); search.reset(); }} data={[{ value: 'search', label: 'Search' }, { value: 'resolve', label: 'ID or URL' }]} />
      <form onSubmit={(event) => { event.preventDefault(); setSelected(null); search.mutate(); }}>
        <Group align="end" wrap="nowrap">
          <TextInput label={mode === 'search' ? 'Game name' : 'Provider ID or page URL'} value={text} onChange={(event) => setText(event.currentTarget.value)} maxLength={500} style={{ flex: 1, minWidth: 0 }} />
          <Tooltip label={mode === 'search' ? 'Search games' : 'Look up game'}><ActionIcon type="submit" size="lg" variant="filled" aria-label="Search provider games" loading={search.isPending} disabled={!text.trim()}><IconSearch size={18} /></ActionIcon></Tooltip>
        </Group>
      </form>
      {search.isError && <Alert color="red">{search.error.message}</Alert>}
      {!search.data && selected && <GamePreview game={selected} />}
      {search.data?.length === 0 && <Text c="dimmed" size="sm">No matching games found.</Text>}
      <Stack gap={0}>
        {search.data?.map((game) => <Group key={game.id} justify="space-between" wrap="nowrap" py="sm" px="xs" style={{ borderBottom: '1px solid var(--mantine-color-default-border)', background: selected?.id === game.id ? 'var(--mantine-color-default-hover)' : undefined }}>
          <GamePreview game={game} />
          <Tooltip label={selected?.id === game.id ? 'Selected' : `Select ${game.name}`}><ActionIcon aria-label={`Select ${game.name}`} variant={selected?.id === game.id ? 'filled' : 'subtle'} onClick={() => setSelected(game)} style={{ flexShrink: 0 }}><IconCheck size={16} /></ActionIcon></Tooltip>
        </Group>)}
      </Stack>
      {save.isError && <Alert color="red">{save.error.message}</Alert>}
      {conflicted && <Button variant="light" loading={reload.isPending} onClick={() => reload.mutate()}>Reload current match</Button>}
      {reload.isError && <Alert color="red">{reload.error.message}</Alert>}
      {reload.isSuccess && <Alert color="blue">Current match: {match.game?.name ?? match.externalId ?? 'No linked game'}. Select a game to confirm your choice.</Alert>}
      <Group justify="end"><Button variant="default" onClick={onClose}>Cancel</Button><Button disabled={!selected || conflicted || !match.isAvailable || reload.isPending} loading={save.isPending} onClick={() => save.mutate()}>Confirm match</Button></Group>
    </Stack>
  </Modal>;
}

export function ProviderMatches({ titleId, titleName }: { titleId: string; titleName: string }) {
  const query = useProviderMatches(titleId);
  const cache = useQueryClient();
  const [editing, setEditing] = useState<TitleProviderMatchDto | null>(null);
  const [unlinking, setUnlinking] = useState<TitleProviderMatchDto | null>(null);
  const mutation = useMutation({ mutationFn: async ({ row, unlink }: { row: TitleProviderMatchDto; unlink: boolean }) => {
    const input = { path: { titleId, providerId: row.providerId }, body: { expectedRevision: row.revision } };
    const result = await (unlink ? unlinkTitleProviderMatch(input) : confirmTitleProviderMatch(input));
    if (result.error) throw new Error('The match could not be updated. Reload matches and try again.');
    setUnlinking(null);
    await cache.invalidateQueries({ queryKey: ['provider-matches', titleId] });
    await cache.invalidateQueries({ queryKey: ['artwork'] });
  } });
  return <Stack gap="sm">
    <Group justify="space-between"><Text fw={600}>Provider matches</Text><Button size="compact-xs" variant="subtle" loading={query.isFetching} onClick={() => { mutation.reset(); void query.refetch(); }}>Reload matches</Button></Group>
    {query.isPending && <Skeleton height={72} />}
    {query.isError && <Alert color="red">{query.error.message}</Alert>}
    {mutation.isError && <Alert color="red">{mutation.error.message}</Alert>}
    {query.data?.length === 0 && <Text size="sm" c="dimmed">No providers enabled.</Text>}
    {query.data?.map((row) => <Group key={row.providerId} justify="space-between" py="sm" align="center" style={{ borderBottom: '1px solid var(--mantine-color-default-border)' }}>
      <Stack gap={6} style={{ flex: '1 1 220px', minWidth: 0 }}>
        <Group gap="xs"><Text fw={500} size="sm">{row.providerName}</Text><Badge size="xs" variant="light" color={row.state === 'Confirmed' ? 'green' : 'gray'}>{({ AutoMatched: 'Auto-matched', NeedsMatch: 'Needs match', NeedsReview: 'Needs review', NoMatchFound: 'No match found' } as Record<string, string>)[row.state] ?? row.state}</Badge></Group>
        {row.game ? <GamePreview game={row.game} /> : row.externalId && <Text size="sm" ff="monospace">#{row.externalId}</Text>}
        {!row.isAvailable && <Text size="xs" c="dimmed">Provider configuration required</Text>}
        {row.lastError && <Text size="xs" c="dimmed">{row.lastError}</Text>}
        {row.metadataNeedsRefresh && <Text size="xs" c="orange">Metadata retained from a previous match</Text>}
      </Stack>
      <Group gap="xs" wrap="nowrap">
        <Button size="compact-sm" variant="subtle" disabled={!row.isAvailable} onClick={() => setEditing(row)}>{row.externalId ? 'Change' : 'Find match'}</Button>
        {row.externalId && <Menu><Menu.Target><ActionIcon variant="subtle" aria-label={`${row.providerName} match actions`}><IconDots size={18} /></ActionIcon></Menu.Target><Menu.Dropdown>
          {row.confidence != null && <Menu.Label>Original match confidence: {Math.round(row.confidence * 100)}%</Menu.Label>}
          {row.state !== 'Confirmed' && <Menu.Item leftSection={<IconCheck size={16} />} disabled={mutation.isPending || !row.isAvailable} onClick={() => mutation.mutate({ row, unlink: false })}>Confirm match</Menu.Item>}
          <Menu.Item color="red" leftSection={<IconUnlink size={16} />} disabled={!row.isAvailable} onClick={() => setUnlinking(row)}>Unlink</Menu.Item>
        </Menu.Dropdown></Menu>}
      </Group>
    </Group>)}
    {editing && <ProviderMatchPicker key={editing.providerId} titleId={titleId} titleName={titleName} row={editing} onClose={() => setEditing(null)} />}
    <Modal opened={!!unlinking} onClose={() => setUnlinking(null)} title={`Unlink ${unlinking?.providerName ?? ''}`} size="sm"><Stack><Text size="sm">Retained metadata and artwork will remain. Automatic matching for this provider will stay off until you choose another match.</Text><Button color="red" loading={mutation.isPending} onClick={() => unlinking && mutation.mutate({ row: unlinking, unlink: true })}>Unlink match</Button></Stack></Modal>
  </Stack>;
}
