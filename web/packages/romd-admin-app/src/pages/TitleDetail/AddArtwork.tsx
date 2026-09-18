import { Alert, Anchor, Badge, Button, Group, Loader, Modal, NativeSelect, Progress, SimpleGrid, Stack, Tabs, Text, UnstyledButton } from '@mantine/core';
import { type ArtworkCandidateDto, type ArtworkProviderDto, browseArtworkCandidates, importGalleryArtwork } from '@romd/admin-api-client';
import { useInfiniteQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { artworkPresentation } from '../../components/artworkPresentation';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import { useArtworkProviders } from '../../hooks/api/useArtwork';
import { titleDetailKeys } from '../../hooks/api/useTitleDetail';
import classes from './ArtworkWorkspace.module.css';
import { BatchArtworkUpload, typeLabel } from './BatchArtworkUpload';
import { CandidateImage } from './CandidateImage';
import { ProviderMatchPicker, useProviderMatches } from './ProviderMatches';

function ProviderImages({ titleId, provider, mediaType, onBusyChange }: { titleId: string; provider: ArtworkProviderDto; mediaType: string; onBusyChange: (busy: boolean) => void }) {
  const cache = useQueryClient();
  const [selected, setSelected] = useState<ArtworkCandidateDto[]>([]);
  const [added, setAdded] = useState<Set<string>>(new Set());
  const [failures, setFailures] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState(false);
  const [progress, setProgress] = useState({ completed: 0, total: 0 });
  const [preview, setPreview] = useState<ArtworkCandidateDto | null>(null);
  const [visible, setVisible] = useState(12);
  const [dimension, setDimension] = useState('');
  const [style, setStyle] = useState('');
  const role = mediaType === 'Cover' ? 'Poster' : mediaType === 'Logo' ? 'Logo' : 'Hero';
  const capability = provider.roles.find(item => item.role === role);
  const query = useInfiniteQuery({
    queryKey: ['artwork', 'collect', titleId, provider.id, provider.gameId, mediaType, dimension, style],
    initialPageParam: null as string | null,
    queryFn: async ({ pageParam, signal }) => {
      const result = await browseArtworkCandidates({ path: { titleId }, query: { providerId: provider.id, gameId: provider.gameId!, role, mediaType, dimension: dimension || undefined, style: style || undefined, cursor: pageParam ?? undefined }, signal });
      if (result.error || !result.data) throw new Error(result.error?.detail ?? 'Images could not be loaded.');
      return result.data;
    },
    getNextPageParam: page => page.nextCursor ?? undefined,
    refetchOnWindowFocus: false,
  });
  const items = Array.from(new Map((query.data?.pages.flatMap(page => page.items) ?? []).map(item => [item.providerAssetId, item])).values());
  const shown = items.slice(0, visible);
  const select = (candidate: ArtworkCandidateDto) => setSelected(previous => previous.some(item => item.providerAssetId === candidate.providerAssetId) ? previous.filter(item => item.providerAssetId !== candidate.providerAssetId) : [...previous, candidate]);
  const save = async () => {
    setBusy(true); onBusyChange(true); setFailures({}); setProgress({ completed: 0, total: selected.length });
    try {
      for (const candidate of selected) {
        try {
          const result = await importGalleryArtwork({ path: { titleId }, body: { candidateReference: candidate.reference } });
          if (result.error || !result.data) throw new Error(result.error?.detail ?? 'Could not add this image.');
          setAdded(previous => new Set([...previous, candidate.providerAssetId]));
          setSelected(previous => previous.filter(item => item.providerAssetId !== candidate.providerAssetId));
        } catch (error) { setFailures(previous => ({ ...previous, [candidate.providerAssetId]: error instanceof Error ? error.message : 'Could not add this image.' })); }
        finally { setProgress(previous => ({ ...previous, completed: previous.completed + 1 })); }
      }
      await Promise.all([
        cache.invalidateQueries({ queryKey: titleDetailKeys.detail(titleId) }),
        cache.invalidateQueries({ queryKey: ['artwork', 'saved', titleId] }),
        cache.invalidateQueries({ queryKey: ['catalog'] }),
        cache.invalidateQueries({ queryKey: ['artwork', 'candidates', titleId] }),
        cache.invalidateQueries({ queryKey: ['artwork', 'collect', titleId] }),
      ]);
    } finally { setBusy(false); onBusyChange(false); }
  };
  const refresh = async () => { setSelected([]); setFailures({}); await query.refetch(); };
  return <Stack>
    <Group justify="space-between"><Text size="sm" c="dimmed">{items.length} images loaded</Text><Button variant="subtle" disabled={busy} onClick={() => void refresh()}>Refresh images</Button></Group>
    <Group grow>
      {!!capability?.dimensions.length && <NativeSelect label="Dimensions" value={dimension} disabled={busy} data={[{ value: '', label: 'All dimensions' }, ...capability.dimensions]} onChange={event => { setDimension(event.currentTarget.value); setSelected([]); setFailures({}); setVisible(12); }} />}
      {!!capability?.styles.length && <NativeSelect label="Style" value={style} disabled={busy} data={[{ value: '', label: 'All styles' }, ...capability.styles.map(value => ({ value, label: value.replaceAll('_', ' ') }))]} onChange={event => { setStyle(event.currentTarget.value); setSelected([]); setFailures({}); setVisible(12); }} />}
    </Group>
    {query.isPending && <Loader aria-label="Loading provider images" />}
    {query.isError && <Alert color="red">{query.error.message} <Button variant="subtle" onClick={() => void query.refetch()}>Retry</Button></Alert>}
    {!query.isPending && !query.isError && !items.length && <Alert color="gray">No {typeLabel(mediaType).toLowerCase()} images found. Try another type or provider, or upload your own.</Alert>}
    {!!shown.length && <Group gap="xs"><Button size="compact-sm" variant="subtle" disabled={busy} onClick={() => setSelected(shown.filter(item => !item.isSaved && !added.has(item.providerAssetId)))}>Select visible</Button><Button size="compact-sm" variant="subtle" disabled={busy || !selected.length} onClick={() => setSelected([])}>Clear selection</Button></Group>}
    <SimpleGrid cols={{ base: 2, sm: mediaType === 'Cover' ? 4 : 3 }}>
      {shown.map(candidate => <Stack key={candidate.providerAssetId} gap={4}>
        <UnstyledButton className={classes.candidate} aria-label={`Select image ${candidate.providerAssetId}`} aria-pressed={selected.some(item => item.providerAssetId === candidate.providerAssetId)} disabled={busy || candidate.isSaved || added.has(candidate.providerAssetId)} onClick={() => select(candidate)}>
          <CandidateImage gallery titleId={titleId} candidate={candidate} title={`${typeLabel(mediaType)} ${candidate.providerAssetId}`} />
          <Group justify="space-between" mt="xs" gap={4}><Text size="xs">{candidate.width} × {candidate.height}</Text>{(candidate.isSaved || added.has(candidate.providerAssetId)) && <Badge size="xs" color="teal">Added</Badge>}</Group>
        </UnstyledButton>
        {candidate.style && <Text size="xs" c="dimmed">{candidate.style.replaceAll('_', ' ')}</Text>}
        <Button variant="subtle" size="compact-xs" disabled={busy} aria-label={`Preview image ${candidate.providerAssetId}`} onClick={() => setPreview(candidate)}>Preview</Button>
        {candidate.attribution && <Text size="xs" c="dimmed">By {candidate.attribution}</Text>}
        <Anchor size="xs" href={candidate.sourcePageUrl} target="_blank" rel="noreferrer">View original</Anchor>
        {failures[candidate.providerAssetId] && <Text size="xs" c="red">{failures[candidate.providerAssetId]}</Text>}
      </Stack>)}
    </SimpleGrid>
    {(visible < items.length || query.hasNextPage) && <Button variant="light" loading={query.isFetchingNextPage} disabled={busy} onClick={() => { if (visible >= items.length) void query.fetchNextPage(); setVisible(value => value + 12); }}>Load more images</Button>}
    <div className={classes.footer}>{busy && <Stack gap={4} mb="sm"><Progress aria-label="Import progress" value={progress.total ? progress.completed / progress.total * 100 : 0} /><Text size="xs" role="status">{progress.completed} of {progress.total} processed</Text></Stack>}<Group justify="space-between"><Text size="sm" role="status">{selected.length} selected{added.size > 0 ? ` · ${added.size} added` : ''}</Text><Button {...workspaceActionProps} disabled={!selected.length} loading={busy} onClick={() => void save()}>{Object.keys(failures).length ? 'Retry selected images' : `Add ${selected.length || ''} ${selected.length === 1 ? 'image' : 'images'}`}</Button></Group><Text size="xs" c="dimmed" mt="xs">Adds originals to your library. Your poster, hero, and logo selections stay in place.</Text></div>
    <Modal opened={preview !== null} onClose={() => setPreview(null)} title={`Preview ${typeLabel(mediaType).toLowerCase()}`} size="xl">
      {preview && <Stack><CandidateImage expanded titleId={titleId} candidate={preview} title={`${typeLabel(mediaType)} ${preview.providerAssetId}`} />
        <Text size="sm">{preview.width} × {preview.height}{preview.style ? ` · ${preview.style}` : ''}{preview.attribution ? ` · By ${preview.attribution}` : ''}</Text>
        <Anchor href={preview.sourcePageUrl} target="_blank" rel="noreferrer">View original on {provider.name}</Anchor>
        <Button {...workspaceActionProps} disabled={preview.isSaved || added.has(preview.providerAssetId)} onClick={() => { select(preview); setPreview(null); }}>{selected.some(item => item.providerAssetId === preview.providerAssetId) ? 'Remove from selection' : 'Select image'}</Button>
      </Stack>}
    </Modal>
  </Stack>;
}

function AddArtworkContent({ titleId, titleName, onBusyChange }: { titleId: string; titleName: string; onBusyChange: (busy: boolean) => void }) {
  const providers = useArtworkProviders();
  const linked = useArtworkProviders(titleId);
  const matches = useProviderMatches(titleId);
  const [mode, setMode] = useState('providers');
  const [source, setSource] = useState<string | null>(null);
  const [type, setType] = useState('Screenshot');
  const [editing, setEditing] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const reportBusy = (value: boolean) => { setBusy(value); onBusyChange(value); };
  const enabledProviders = providers.data?.filter(item => matches.data?.some(match => match.providerId === item.id));
  const provider = enabledProviders?.find(item => item.id === source) ?? enabledProviders?.find(item => item.isAvailable) ?? enabledProviders?.[0];
  const link = linked.data?.find(item => item.id === provider?.id);
  const match = matches.data?.find(item => item.providerId === provider?.id);
  const types = provider?.mediaTypes ?? provider?.roles.flatMap(item => Object.entries(artworkPresentation).filter(([role]) => role === item.role).map(([, config]) => config.mediaType)) ?? [];
  const mediaType = types.includes(type) ? type : types[0];
  const editingMatch = matches.data?.find(item => item.providerId === editing);
  return <Stack>
    <Tabs color="teal" value={mode} onChange={value => setMode(value ?? 'providers')}><Tabs.List><Tabs.Tab value="providers" disabled={busy}>Browse providers</Tabs.Tab><Tabs.Tab value="upload" disabled={busy}>Upload images</Tabs.Tab></Tabs.List></Tabs>
    <div hidden={mode !== 'upload'}><BatchArtworkUpload titleId={titleId} onBusyChange={reportBusy} /></div>
    <div className={classes.browser} style={{ display: mode === 'providers' ? undefined : 'none' }}>
      <div className={classes.sources}>
        {providers.isPending && <Loader size="sm" />}
        {enabledProviders?.map(item => <UnstyledButton key={item.id} className={classes.source} aria-pressed={provider?.id === item.id} disabled={busy} onClick={() => setSource(item.id)}><Text size="sm" fw={600}>{item.name}</Text><Text size="xs" c="dimmed">{!item.isAvailable ? 'Setup required' : linked.data?.some(link => link.id === item.id) ? 'Connected' : 'Match game'}</Text></UnstyledButton>)}
      </div>
      <Stack style={{ minWidth: 0 }}>
        {(providers.isError || linked.isError || matches.isError) && <Alert color="red">Could not load artwork sources. <Button variant="subtle" onClick={() => { void providers.refetch(); void linked.refetch(); void matches.refetch(); }}>Retry</Button></Alert>}
        {!providers.isPending && !providers.isError && !matches.isPending && !matches.isError && !enabledProviders?.length && <Text c="dimmed">No artwork providers enabled. You can still upload images.</Text>}
        {provider && <>
          <Group justify="space-between" align="start"><Stack gap={3}><Text fw={600}>{provider.name}</Text>{link?.gameUrl ? <Anchor size="sm" href={link.gameUrl} target="_blank" rel="noreferrer">{link.gameName ?? titleName}</Anchor> : <Text size="sm" c="dimmed">Choose the matching game to browse its artwork.</Text>}</Stack>{match && provider.isAvailable && <Button variant="subtle" disabled={busy} onClick={() => setEditing(provider.id)}>{link ? 'Change match' : 'Find match'}</Button>}</Group>
          {!provider.isAvailable && <Alert color="gray">This provider needs configuration before you can browse its images. <Anchor href="/settings">Open settings</Anchor></Alert>}
          {provider.isAvailable && !link && !linked.isPending && <Alert color="gray">{match?.state === 'NeedsReview' ? 'Review the game match to unlock this artwork source.' : 'Match this title once, then collect images from this provider here.'}</Alert>}
          {provider.isAvailable && link && mediaType && <><NativeSelect label="Image type" value={mediaType} disabled={busy} data={types.map(value => ({ value, label: typeLabel(value) }))} onChange={event => setType(event.currentTarget.value)} /><ProviderImages key={`${provider.id}:${link.gameId}:${mediaType}`} titleId={titleId} provider={link} mediaType={mediaType} onBusyChange={reportBusy} /></>}
        </>}
      </Stack>
    </div>
    {editingMatch && <ProviderMatchPicker titleId={titleId} titleName={titleName} row={editingMatch} onClose={() => setEditing(null)} />}
  </Stack>;
}

export function AddArtwork({ opened, onClose, titleId, titleName }: { opened: boolean; onClose: () => void; titleId: string; titleName: string }) {
  const [busy, setBusy] = useState(false);
  return <Modal opened={opened} onClose={() => !busy && onClose()} title={`Add artwork · ${titleName}`} size={1100} closeButtonProps={{ disabled: busy, 'aria-label': 'Close add artwork' }} closeOnClickOutside={!busy} closeOnEscape={!busy}>
    {opened && <AddArtworkContent titleId={titleId} titleName={titleName} onBusyChange={setBusy} />}
  </Modal>;
}
