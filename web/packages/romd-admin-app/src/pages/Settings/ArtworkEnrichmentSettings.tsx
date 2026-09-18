import { Alert, Badge, Button, Group, Skeleton, Stack, Switch, Text, Title } from '@mantine/core';
import type { ArtworkEnrichmentSettingsDto } from '@romd/admin-api-client';
import { useState } from 'react';
import { UnsavedChanges } from '../../components/Administration/UnsavedChanges';
import { artworkSourceLabel } from '../../components/artworkPresentation';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import { useAuth } from '../../contexts/AuthContext';
import { useArtworkEnrichmentSettings, useSaveArtworkEnrichmentSettings } from '../../hooks/api/useArtworkAcquisition';

function SettingsForm({ settings, reload }: { settings: ArtworkEnrichmentSettingsDto; reload: () => void }) {
  const [baseline, setBaseline] = useState(settings);
  const [posters, setPosters] = useState(settings.fillPosters);
  const [heroes, setHeroes] = useState(settings.fillHeroes);
  const [logos, setLogos] = useState(settings.fillLogos);
  const [backdrops, setBackdrops] = useState(settings.fillBackdrops);
  const [reviewBackdrops, setReviewBackdrops] = useState(settings.reviewBackdrops);
  const save = useSaveArtworkEnrichmentSettings();
  const changed = posters !== baseline.fillPosters || heroes !== baseline.fillHeroes || logos !== baseline.fillLogos || backdrops !== baseline.fillBackdrops || reviewBackdrops !== baseline.reviewBackdrops;
  return <Stack gap="md"><UnsavedChanges dirty={changed} />
    <Switch label="Automatically fill missing posters" checked={posters} onChange={(event) => setPosters(event.currentTarget.checked)} disabled={save.isPending} />
    <Switch label="Automatically fill missing banners" checked={heroes} onChange={(event) => setHeroes(event.currentTarget.checked)} disabled={save.isPending} />
    <Switch label="Find missing backdrops" description="Discover landscape artwork independently of banners. Existing selections are preserved." checked={backdrops} onChange={event => setBackdrops(event.currentTarget.checked)} disabled={save.isPending} />
    <Switch label="Review backdrops before selection" description="Recommended: image dimensions cannot detect built-in logos or incorrect edition branding. Turn off to automatically select the best dimensional match." checked={reviewBackdrops} onChange={event => setReviewBackdrops(event.currentTarget.checked)} disabled={save.isPending || !backdrops} />
    <Switch label="Automatically fill missing logos" description="Logos require a confirmed match with a logo provider such as SteamGridDB." checked={logos} onChange={(event) => setLogos(event.currentTarget.checked)} disabled={save.isPending} />
    <Group gap="xs"><Text size="sm" c="dimmed">Automatic artwork providers</Text>{settings.providers.length ? settings.providers.map((provider) => <Badge key={provider} variant="light" color="gray">{artworkSourceLabel(provider)}</Badge>) : <Text size="sm">None enabled</Text>}</Group>
    {save.isError && <Alert color="red">{save.error.message}<Button variant="subtle" onClick={() => { if (!changed || window.confirm('Discard changes and reload settings?')) reload(); }}>Reload saved settings</Button></Alert>}
    <Group justify="flex-end"><Button {...workspaceActionProps} disabled={!changed} loading={save.isPending} onClick={() => save.mutate({ revision: baseline.revision, fillPosters: posters, fillHeroes: heroes, fillLogos: logos, fillBackdrops: backdrops, reviewBackdrops }, { onSuccess: saved => setBaseline(saved) })}>Save changes</Button></Group>
  </Stack>;
}

function ArtworkEnrichmentSettingsContent() {
  const query = useArtworkEnrichmentSettings();
  const [version, setVersion] = useState(0);
  return <Stack component="section" gap="md" py="md">
    <Title order={3} size="h4">Artwork enrichment</Title>
    {query.isLoading && <Skeleton height={140} />}
    {query.isError && <Alert color="red">{query.error.message}<Button variant="subtle" onClick={() => void query.refetch()}>Reload</Button></Alert>}
    {query.data && <SettingsForm key={version} settings={query.data} reload={() => { void query.refetch().then(result => { if (result.isSuccess) setVersion(version => version + 1); }); }} />}
  </Stack>;
}

export function ArtworkEnrichmentSettings() {
  const { user } = useAuth();
  return user?.roles.includes('Admin') ? <ArtworkEnrichmentSettingsContent /> : null;
}
