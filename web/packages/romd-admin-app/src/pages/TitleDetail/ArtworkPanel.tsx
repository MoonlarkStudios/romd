import { ActionIcon, Alert, Anchor, Badge, Box, Button, FileInput, Group, Loader, Modal, Select, SimpleGrid, Stack, Switch, Tabs, Text, Title, Tooltip, UnstyledButton } from '@mantine/core';
import type { ArtworkCandidateDto, ArtworkProviderDto, TitleDetail } from '@romd/admin-api-client';
import { applyArtworkCandidate, browseArtworkCandidates, pinSavedArtwork, previewArtworkCandidate, returnArtworkToAutomatic } from '@romd/admin-api-client';
import { IconFocus2, IconRestore } from '@tabler/icons-react';
import { useInfiniteQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useRef, useState } from 'react';
import { ArtworkFrame } from '../../components/ArtworkFrame';
import { artworkPresentation, artworkRoleLabel, artworkRoles, artworkSourceLabel, backdropSuitable, type ArtworkRole as Role } from '../../components/artworkPresentation';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import { useAuth } from '../../contexts/AuthContext';
import { artworkKeys, useArtworkProviders, useArtworkSelections, useSavedArtwork } from '../../hooks/api/useArtwork';
import { useJob } from '../../hooks/api/useJobs';
import { useUploadTitleMedia } from '../../hooks/api/useTitleActions';
import { titleDetailKeys } from '../../hooks/api/useTitleDetail';
import { createRequestId } from '../../utils/requestId';
import { AddArtwork } from './AddArtwork';
import { ArtworkAcquisition } from './ArtworkAcquisition';
import { ArtworkComposer, type ArtworkDraft } from './ArtworkComposer';
import classes from './ArtworkWorkspace.module.css';
import { artworkJobStatus } from './artworkJobStatus';
import { CandidateImage } from './CandidateImage';
import { MediaGallery, type SavedArtworkTarget } from './MediaGallery';


function CandidateBrowser({ titleId, title, role, provider, gameId, onChoose }: {
  titleId: string; title: TitleDetail; role: Role; provider: ArtworkProviderDto; gameId: string; onChoose: (candidate: ArtworkCandidateDto) => Promise<void>;
}) {
  const [dimension, setDimension] = useState<string | null>(null);
  const [style, setStyle] = useState<string | null>(null);
  const [selected, setSelected] = useState<ArtworkCandidateDto | null>(null);
  const [visible, setVisible] = useState(12);
  const [suitableOnly, setSuitableOnly] = useState(role === 'Backdrop');
  const [pending, setPending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const capability = provider.roles.find((item) => item.role === role);
  const candidates = useInfiniteQuery({
    queryKey: ['artwork', 'candidates', titleId, provider.id, gameId, role, dimension, style],
    initialPageParam: null as string | null,
    queryFn: async ({ pageParam, signal }) => {
      const response = await browseArtworkCandidates({ path: { titleId }, query: {
        providerId: provider.id, gameId, role, dimension: dimension ?? undefined, style: style ?? undefined, cursor: pageParam ?? undefined,
      }, signal });
      if (response.error || !response.data) throw new Error(response.error?.detail ?? 'Could not browse artwork.');
      return response.data;
    },
    getNextPageParam: (page) => page.nextCursor ?? undefined,
    staleTime: 0,
    refetchOnWindowFocus: false,
  });
  const items = Array.from(new Map((candidates.data?.pages.flatMap(page => page.items) ?? []).map(item => [item.providerAssetId, item])).values());
  const displayed = role === 'Backdrop' && suitableOnly ? items.filter(item => backdropSuitable(item.width, item.height))
    .sort((a, b) => Math.abs(Math.log(a.width / a.height / (16 / 9))) - Math.abs(Math.log(b.width / b.height / (16 / 9))) || b.width * b.height - a.width * a.height) : items;
  const changeFilters = (nextDimension: string | null, nextStyle: string | null) => {
    setDimension(nextDimension); setStyle(nextStyle); setSelected(null); setVisible(12); setError(null);
  };
  const apply = async () => {
    if (!selected) return;
    setPending(true); setError(null);
    try {
      await onChoose(selected);
    } catch { setError('Preview expired or unavailable. Refresh candidates and try again.'); }
    finally { setPending(false); }
  };
  return <Stack>
    {!!(capability?.dimensions.length || capability?.styles.length) && <Group grow>{!!capability?.dimensions.length && <Select label="Dimensions" placeholder="All supported dimensions" clearable value={dimension} data={capability.dimensions} onChange={(value) => changeFilters(value, style)} disabled={pending} />}
      {!!capability?.styles.length && <Select label="Style" placeholder="All styles" clearable value={style} data={capability.styles} onChange={(value) => changeFilters(dimension, value)} disabled={pending} />}</Group>}
    {role === 'Backdrop' && <Stack gap="xs"><Text size="sm">Choose a scene without a built-in game logo. Check the subject and text contrast in the composition preview.</Text><Switch label="Suitable landscape dimensions" description="At least 1200 × 720; includes crops from 1.3:1 to 2.1:1. Turn off to inspect all artwork." checked={suitableOnly} onChange={event => { setSuitableOnly(event.currentTarget.checked); setSelected(null); setVisible(12); }} /></Stack>}
    {candidates.isLoading && <Loader aria-label="Loading candidates" />}
    {candidates.isError && <Alert color="red">{candidates.error.message}</Alert>}
    <Group justify="space-between"><Text size="sm" c="dimmed">{displayed.length} images</Text><Button variant="subtle" disabled={pending} onClick={() => { setSelected(null); void candidates.refetch(); }}>Refresh candidates</Button></Group>
    <SimpleGrid cols={{ base: 2, sm: role === 'Poster' ? 4 : 2 }}>
      {displayed.slice(0, visible).map((candidate) => <UnstyledButton key={candidate.reference} aria-label={`Select artwork ${candidate.providerAssetId}`} aria-pressed={selected?.reference === candidate.reference} disabled={pending} onClick={() => { setSelected(candidate); setError(null); }} className={classes.candidate}>
        <CandidateImage titleId={titleId} candidate={candidate} title={title.name} />
        <Text size="xs" mt="xs">{candidate.width} × {candidate.height}{candidate.style ? ` · ${candidate.style}` : ''}</Text>
      </UnstyledButton>)}
    </SimpleGrid>
    {!candidates.isLoading && !candidates.isError && displayed.length === 0 && <Text>No artwork matches these filters.</Text>}
    {(visible < displayed.length || candidates.hasNextPage) && <Button variant="light" loading={candidates.isFetchingNextPage} disabled={pending} onClick={() => {
      if (visible >= displayed.length) void candidates.fetchNextPage();
      setVisible((count) => count + 12);
    }}>More candidates</Button>}
    {selected && <Stack style={{ position: 'sticky', bottom: 0, background: 'var(--mantine-color-body)', paddingBlock: 12, borderTop: '1px solid var(--mantine-color-default-border)' }}>
      <Group justify="space-between"><Text size="sm">{selected.attribution && `Artwork by ${selected.attribution}. `}<Anchor href={selected.sourcePageUrl} target="_blank" rel="noreferrer">View on {provider.name}</Anchor></Text>
      <Button {...workspaceActionProps} onClick={() => void apply()} loading={pending}>Preview {artworkRoleLabel(role).toLowerCase()}</Button></Group>
      {error && <Alert color="red">{error}</Alert>}
    </Stack>}
  </Stack>;
}

function ArtworkPanelContent({ titleId, title }: { titleId: string; title: TitleDetail }) {
  const cache = useQueryClient();
  const selections = useArtworkSelections(titleId);
  const saved = useSavedArtwork(titleId);
  const [mode, setMode] = useState('saved');
  const [adding, setAdding] = useState(false);
  const [draft, setDraft] = useState<ArtworkDraft | null>(null);
  const [role, setRole] = useState<Role | null>(null);
  const providers = useArtworkProviders(titleId, role ?? undefined);
  const upload = useUploadTitleMedia();
  const request = useRef<{ key: string; id: string } | null>(null);
  const uploaded = useRef<{ file: File; mediaId: string } | null>(null);
  const temporaryUrl = useRef<string | null>(null);
  useEffect(() => () => { if (temporaryUrl.current) URL.revokeObjectURL(temporaryUrl.current); }, []);
  const [acceptedJob, setAcceptedJob] = useState<string>();
  const [busyRole, setBusyRole] = useState<Role | null>(null);
  const [error, setError] = useState<string | null>(null);
  const pendingJob = selections.data?.find((selection) => selection.pendingJobId)?.pendingJobId ?? acceptedJob;
  const job = useJob(pendingJob ?? undefined);
  const jobStatus = job.data ? artworkJobStatus({
    ...job.data,
    wasSuperseded: 'wasSuperseded' in job.data && job.data.wasSuperseded === true,
  }) : null;
  const refresh = () => Promise.all([
    cache.invalidateQueries({ queryKey: artworkKeys.selections(titleId) }),
    cache.invalidateQueries({ queryKey: artworkKeys.saved(titleId) }),
    cache.invalidateQueries({ queryKey: titleDetailKeys.detail(titleId) }),
    cache.invalidateQueries({ queryKey: ['catalog'] }),
  ]);
  useEffect(() => {
    if (job.data?.isTerminal) {
      void cache.invalidateQueries({ queryKey: artworkKeys.selections(titleId) });
      void cache.invalidateQueries({ queryKey: artworkKeys.saved(titleId) });
      void cache.invalidateQueries({ queryKey: titleDetailKeys.detail(titleId) });
      void cache.invalidateQueries({ queryKey: ['catalog'] });
    }
  }, [job.data?.isTerminal, cache, titleId]);
  const provider = providers.data?.find((item) => item.id === mode);
  const releasePreview = () => {
    if (temporaryUrl.current) URL.revokeObjectURL(temporaryUrl.current);
    temporaryUrl.current = null;
  };
  const beginCandidate = async (candidate: ArtworkCandidateDto) => {
    const selection = selections.data?.find((item) => item.role === role);
    if (!role || !selection) return;
    setBusyRole(role);
    try {
    const response = await previewArtworkCandidate({ path: { titleId }, body: { candidateReference: candidate.reference }, parseAs: 'blob' });
    const data: unknown = response.data;
    if (response.error || !(data instanceof Blob)) throw new Error('Preview unavailable');
    releasePreview();
    temporaryUrl.current = URL.createObjectURL(data);
    setDraft({ role, candidate, url: temporaryUrl.current, revision: selection.revision, focalX: 50, focalY: 50 });
    setRole(null); setError(null);
    } finally { setBusyRole(null); }
  };
  const beginDraft = async (selectedRole: Role, target: SavedArtworkTarget, previewUrl?: string) => {
    const selection = selections.data?.find((item) => item.role === selectedRole);
    const asset = target.assetId ? saved.data?.find((item) => item.id === target.assetId) : null;
    const url = previewUrl ?? asset?.artwork.url ?? title.media?.find((item) => item.id === target.mediaId)?.url;
    if (!selection || !url || busyRole) return false;
    const current = target.assetId ? title.artwork?.find((item) => item.role === selectedRole && item.assetId === target.assetId) : undefined;
    setError(null); setRole(null);
    setDraft({ role: selectedRole, target, url, revision: selection.revision, focalX: current?.focalX ?? 50, focalY: current?.focalY ?? 50 });
    return true;
  };
  const assign = async (proposed: ArtworkDraft) => {
    const { role: selectedRole, target } = proposed;
    if (busyRole) return false;
    setBusyRole(selectedRole); setError(null);
    try {
      if (proposed.candidate) {
        const key = JSON.stringify([proposed.candidate.reference, proposed.focalX, proposed.focalY, proposed.revision]);
        if (request.current?.key !== key) request.current = { key, id: createRequestId() };
        const response = await applyArtworkCandidate({ path: { titleId }, body: { requestId: request.current.id, candidateReference: proposed.candidate.reference, focalX: proposed.focalX, focalY: proposed.focalY, expectedRevision: proposed.revision } });
        if (response.error || !response.data) { setError(response.error?.detail ?? 'Could not queue artwork. Retry or choose a fresh candidate.'); return false; }
        setAcceptedJob(response.data.jobId);
      } else {
      let mediaId = target?.mediaId;
      if (proposed.file) {
        if (uploaded.current?.file !== proposed.file) {
          const mediaType = artworkPresentation[selectedRole].mediaType.toLowerCase();
          const media = await upload.mutateAsync({ titleId, file: proposed.file, type: mediaType });
          uploaded.current = { file: proposed.file, mediaId: media.id };
        }
        mediaId = uploaded.current.mediaId;
      }
      const response = await pinSavedArtwork({ path: { titleId, role: selectedRole }, body: { assetId: target?.assetId ?? null, mediaId: mediaId ?? null, expectedRevision: proposed.revision, focalX: proposed.focalX, focalY: proposed.focalY } });
      if (response.error) { setError(response.error.status === 409 ? 'The selection changed. Cancel this preview and choose again to review the latest artwork.' : response.error.detail ?? 'Could not select artwork.'); await refresh(); return false; }
      }
      await refresh(); setRole(null); setDraft(null); releasePreview(); return true;
    } catch { setError(uploaded.current?.file === proposed.file && proposed.file ? 'The image is saved in your gallery, but could not be selected. Retry or cancel to keep it in the gallery.' : 'Could not save artwork. Try again.'); return false; }
    finally { setBusyRole(null); }
  };
  const automatic = async (selectedRole: Role) => {
    setBusyRole(selectedRole); setError(null);
    try {
      const response = await returnArtworkToAutomatic({ path: { titleId, role: selectedRole } });
      if (response.error) { setError(response.error.detail ?? 'Could not return to automatic.'); return; }
      await refresh();
      setRole(null);
    } catch { setError('Could not return to automatic. Try again.'); }
    finally { setBusyRole(null); }
  };
  return <Stack>
    {error && !draft && !role && <Alert color="red">{error}</Alert>}
    <div className={classes.header}><Title order={3} className={classes.heading}>Artwork library</Title><Button {...workspaceActionProps} onClick={() => setAdding(true)}>Add artwork</Button></div>
    <Text size="sm" c="dimmed">Collect images from your providers or upload your own. Choose a poster, backdrop, banner, and logo from your library.</Text>
    <div className={classes.assignments}>
    <ArtworkAcquisition titleId={titleId} onReviewBackdrop={() => { setRole('Backdrop'); setMode('igdb'); setError(null); }} />
    {selections.isError && <Alert color="red">Could not load selection state. <Button onClick={() => void selections.refetch()}>Retry</Button></Alert>}
    {jobStatus && <Alert color={jobStatus.color} title={jobStatus.title}>
      {jobStatus.message}
      <Anchor href={`/jobs/${pendingJob}`} ml="xs">View job</Anchor>
    </Alert>}
    <SimpleGrid cols={{ base: 1, sm: 2, xl: 4 }} mt="md">{artworkRoles.map((itemRole) => {
      const artwork = title.artwork?.find((item) => item.role === itemRole);
      const selection = selections.data?.find((item) => item.role === itemRole);
      const asset = saved.data?.find(item => item.id === artwork?.assetId);
      return <Stack key={itemRole} gap="sm" className={classes.assignment}>
        <Box h={120} style={{ display: 'grid', alignItems: 'center' }}><Box w={itemRole === 'Poster' ? 80 : 160}><ArtworkFrame role={itemRole} src={artwork?.url} title={title.name} fit={artwork?.fit === 'Contain' ? 'Contain' : 'Cover'} focalX={artwork?.focalX} focalY={artwork?.focalY} /></Box></Box>
        <Stack gap="xs" style={{ minWidth: 0 }}>
        <Group justify="space-between"><Title order={4} className={classes.heading}>{artworkRoleLabel(itemRole)}</Title><Badge color={artwork?.url ? 'teal' : 'gray'} variant="light">{artwork?.url ? 'Available' : 'Missing'}</Badge></Group><Badge w="fit-content" variant="light">{selection?.mode === 'Pinned' ? 'Selected by you' : selection?.mode === 'Automatic' ? 'Automatic' : 'Loading'}</Badge>
        <Text size="xs" c="dimmed">{artworkPresentation[itemRole].description}</Text>
        {asset && <Text size="xs" c="dimmed">{artworkSourceLabel(asset.sourceId)}{asset.artwork.originalWidth && asset.artwork.originalHeight ? ` · ${asset.artwork.originalWidth} × ${asset.artwork.originalHeight}` : ''}</Text>}
        {selection?.pendingJobId && <Text size="sm">A replacement is pending.</Text>}
        <Group><Button {...workspaceActionProps} variant="light" disabled={!selection || busyRole !== null} onClick={() => { setRole(itemRole); setMode('saved'); setError(null); }}>Choose {artworkRoleLabel(itemRole).toLowerCase()}</Button>
          {(itemRole === 'Hero' || itemRole === 'Backdrop') && artwork?.assetId && <Tooltip label="Adjust focal point"><ActionIcon aria-label={`Adjust ${artworkRoleLabel(itemRole).toLowerCase()} focal point`} variant="subtle" disabled={!selection || busyRole !== null} onClick={() => void beginDraft(itemRole, { assetId: artwork.assetId! }, artwork.url ?? undefined)}><IconFocus2 size={18} /></ActionIcon></Tooltip>}
          <Tooltip label={`Return ${artworkRoleLabel(itemRole).toLowerCase()} to automatic`}><ActionIcon variant="subtle" color="gray" aria-label={`Return ${artworkRoleLabel(itemRole).toLowerCase()} to automatic`} loading={busyRole === itemRole} disabled={!selection || (!selection.pendingJobId && selection.mode === 'Automatic') || busyRole !== null} onClick={() => void automatic(itemRole)}><IconRestore size={18} /></ActionIcon></Tooltip></Group>
      </Stack></Stack>;
    })}</SimpleGrid>
    </div>
    <div className={classes.library}>
    <Title order={4} className={classes.heading} mb="md">Saved images</Title>
    {saved.isLoading && <Loader size="sm" aria-label="Loading saved artwork" />}
    {saved.isError && <Alert color="red">Could not load saved artwork. <Button variant="subtle" onClick={() => void saved.refetch()}>Retry</Button></Alert>}
    <MediaGallery titleId={titleId} media={title.media ?? []} saved={saved.data ?? []} artwork={title.artwork ?? []} onAssign={beginDraft} busy={busyRole !== null || !selections.data} onAdd={() => setAdding(true)} />
    </div>
    <AddArtwork opened={adding} onClose={() => setAdding(false)} titleId={titleId} titleName={title.name} />
    {draft && <ArtworkComposer title={title} titleId={titleId} draft={draft} busy={busyRole !== null} error={error} onCancel={() => { setRole(draft.role); setDraft(null); releasePreview(); setError(null); }} onSave={(value) => void assign(value)} />}
    <Modal opened={role !== null} onClose={() => busyRole === null && setRole(null)} title={`Choose ${role ? artworkRoleLabel(role).toLowerCase() : ''} artwork`} closeButtonProps={{ 'aria-label': 'Close artwork chooser' }} size="xl">
      <Stack>
        <Tabs color="teal" value={mode} onChange={(value) => setMode(value ?? 'saved')}><Tabs.List style={{ flexWrap: 'nowrap', overflowX: 'auto' }}>
          <Tabs.Tab value="saved" disabled={busyRole !== null}>Saved</Tabs.Tab>
          {providers.data?.filter(item => item.isAvailable && item.roles.some(capability => capability.role === role)).map((item) => <Tabs.Tab key={item.id} value={item.id} disabled={busyRole !== null}>{item.name}</Tabs.Tab>)}
          <Tabs.Tab value="custom" disabled={busyRole !== null}>Upload</Tabs.Tab>
        </Tabs.List></Tabs>
        {error && <Alert color="red">{error}</Alert>}
        {role && mode === 'saved' && <MediaGallery titleId={titleId} media={title.media ?? []} saved={saved.data ?? []} artwork={title.artwork ?? []} role={role} onAssign={beginDraft} busy={busyRole !== null || !selections.data} />}
        {role && mode === 'custom' && <FileInput label={`${artworkRoleLabel(role)} image`} placeholder="Select image" accept="image/jpeg,image/png,image/webp" onChange={(file) => {
          const selection = selections.data?.find((item) => item.role === role);
          if (!file || !selection) return;
          if (!['image/jpeg', 'image/png', 'image/webp'].includes(file.type) || file.size > 32 * 1024 * 1024) { setError('Choose a JPEG, PNG, or WebP image under 32 MB.'); return; }
          releasePreview(); temporaryUrl.current = URL.createObjectURL(file); uploaded.current = null;
          setDraft({ role, file, url: temporaryUrl.current, revision: selection.revision, focalX: 50, focalY: 50 }); setRole(null); setError(null);
        }} />}
        {providers.isError && <Alert color="red">Could not load providers. <Button onClick={() => void providers.refetch()}>Retry</Button></Alert>}
        {role && provider?.gameId && <Stack>
          {provider.gameUrl && <Anchor href={provider.gameUrl} target="_blank" rel="noopener noreferrer">{provider.gameName ?? provider.name}</Anchor>}
          <CandidateBrowser key={`${role}:${provider.id}:${provider.gameId}`} titleId={titleId} title={title} role={role} provider={provider} gameId={provider.gameId} onChoose={beginCandidate} />
        </Stack>}
        {!providers.isPending && !providers.isError && !providers.data?.some(item => item.isAvailable && item.roles.some(capability => capability.role === role)) && <Text size="sm" c="dimmed">No linked provider currently supports this role. Choose a saved image, upload artwork, or <Anchor href={`/titles/${titleId}?tab=metadata`}>Manage provider matches</Anchor></Text>}
      </Stack>
    </Modal>
  </Stack>;
}

export function ArtworkPanel(props: { titleId: string; title: TitleDetail }) {
  const { user } = useAuth();
  if (!user?.roles.some((value) => value === 'Admin' || value === 'Manager')) return <MediaGallery titleId={props.titleId} media={props.title.media ?? []} artwork={props.title.artwork ?? []} />;
  return <ArtworkPanelContent {...props} />;
}
