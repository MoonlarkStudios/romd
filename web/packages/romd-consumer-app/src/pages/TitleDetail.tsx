import { GameDetailHero, GameMetadata, PlatformMark } from '@romd/consumer-ui';
import '@romd/consumer-ui/styles.css';
import { Accordion, AspectRatio, Badge, Button, Group, Image, Modal, SimpleGrid, Stack, Tabs, Text, Title } from '@mantine/core';
import type { ConsumerMediaRefDto, ConsumerReleaseDto, ConsumerTitleDetailDto } from '@romd/consumer-api-client';
import { IconArrowLeft, IconDownload, IconPlayerPlayFilled } from '@tabler/icons-react';
import { useState } from 'react';
import { Link, useLocation, useParams, useSearchParams } from 'react-router';
import { ArtworkFallback } from '../components/library/ArtworkFallback';
import { ArtworkImage } from '../components/library/ArtworkImage';
import { ReleaseManifestPanel } from '../components/library/ReleaseManifestPanel';
import { ReleaseVersionMenu } from '../components/library/ReleaseVersionMenu';
import { TitleLink } from '../components/navigation/TitleLink';
import { readReturnContext } from '../components/navigation/titleNavigation';
import { StatusState } from '../components/status/StatusState';
import { useBrowserPlaybackCapability } from '../hooks/useBrowserPlaybackCapability';
import { useTitleDetail } from '../hooks/useConsumerLibrary';
import { type BrowserPlaybackPreflight, getBrowserPlaybackPreflight } from '../services/browserPlayback';
import { getDownloadVerificationUnavailableReason } from '../services/downloadVerification';
import type { PlayerCapabilityResult } from '../services/playerBridge';
import { artworkDeliveryUrl, selectArtwork } from '../utils/artwork';
import { formatBytes } from '../utils/format';
import { formatList } from '../utils/library';
import classes from './TitleDetail.module.css';

export function TitleDetail() {
  const { titleId } = useParams();
  const titleQuery = useTitleDetail(titleId);
  const capability = useBrowserPlaybackCapability();
  const location = useLocation();
  const returnTo = readReturnContext(location.state);
  const [params, setParams] = useSearchParams();
  const [activeMedia, setActiveMedia] = useState<ConsumerMediaRefDto | null>(null);
  const [downloadRelease, setDownloadRelease] = useState<string | null>(null);
  const [failedBackdrop, setFailedBackdrop] = useState<string | null>(null);
  const title = titleQuery.data;
  if (titleQuery.isLoading) return <StatusState kind="loading" />;
  if (titleQuery.isError || !title) return <StatusState kind="error" message="This title could not be loaded." onRetry={() => void titleQuery.refetch()} retrying={titleQuery.isRefetching} />;

  const media = title.media.filter(item => !['video', 'background', 'logo'].includes(item.type.toLowerCase()));
  const requestedTab = params.get('tab');
  const tab = ['details', 'releases', ...(media.length ? ['media'] : [])].includes(requestedTab ?? '') ? requestedTab! : 'details';
  const select = (key: string, value: string) => setParams(previous => { const next = new URLSearchParams(previous); next.set(key, value); return next; }, { preventScrollReset: true, state: location.state });
  const selectedRelease = title.releases.find(release => release.id === params.get('release'))
    ?? title.releases.find(release => release.id === title.defaultReleaseId) ?? title.releases[0] ?? null;
  const playback = selectedRelease ? getCapabilityGatedPreflight(title, selectedRelease, capability.data, capability.isLoading) : null;
  const backdrop = title.artwork?.find(item => item.role === 'Backdrop' && artworkDeliveryUrl(item.url, window.location.origin));
  const backdropKey = `${title.id}:${backdrop?.contentVersion}:${backdrop?.url}`;
  const hasBackdrop = Boolean(backdrop && failedBackdrop !== backdropKey);

  return <div className={classes.page}>
    <GameDetailHero
      name={title.name}
      backdrop={hasBackdrop ? <ArtworkImage artwork={title.artwork} role="Backdrop" priority style={{ height: '100%', aspectRatio: 'auto' }} onError={() => setFailedBackdrop(backdropKey)} /> : undefined}
      poster={<ArtworkImage artwork={title.artwork} priority fallback={<ArtworkFallback name={title.name} />} />}
      logoUrl={artworkDeliveryUrl(selectArtwork(title.artwork, 'Logo', 620, window.devicePixelRatio)?.url, window.location.origin)}
      metadata={<GameMetadata {...title} />}
      backAction={<Button variant="subtle" color="gray" leftSection={<IconArrowLeft size={16} />} component={Link} to={returnTo.href} state={{ restoreTitleReturn: returnTo }}>
        {returnTo.label}
      </Button>}
      actions={<HeroActions titleId={title.id} releases={title.releases} defaultReleaseId={title.defaultReleaseId ?? null} selectedRelease={selectedRelease} playback={playback} checking={capability.isLoading} onSelectRelease={id => select('release', id)} onDownload={() => { setDownloadRelease(selectedRelease?.id ?? null); select('tab', 'releases'); }} />}
    />
    <Tabs value={tab} onChange={value => select('tab', value ?? 'details')} keepMounted={false} className={classes.tabs}>
      <Tabs.List className={classes.tabList} aria-label="Game information">
        <Tabs.Tab value="details">Details</Tabs.Tab>
        <Tabs.Tab value="releases">Releases</Tabs.Tab>
        {media.length > 0 && <Tabs.Tab value="media">Media</Tabs.Tab>}
      </Tabs.List>
      <Tabs.Panel value="details" className={classes.panel}>
        <div className={classes.details}>
          <div className={classes.summary}>
            {title.artwork?.some(item => item.role === 'Poster') && <div className={classes.detailsPoster}>
              <ArtworkImage artwork={title.artwork} alt={`${title.name} box art`} style={{ aspectRatio: '3 / 4' }} fallback={<ArtworkFallback name={title.name} />} />
            </div>}
            <Stack gap="md" className={classes.summaryText}><Title order={2} className="romd-section-heading">{title.name}</Title><Text className={classes.description}>{title.description ?? 'A description has not been added yet.'}</Text></Stack>
          </div>
          <SimpleGrid cols={{ base: 2, sm: 3 }} spacing="xl">
            {[['Developer', title.developer], ['Publisher', title.publisher], ['Released', title.releaseDate], ['Genre', title.genre], ['Players', title.players?.toString()]].filter(([, value]) => value).map(([label, value]) => <InfoCell key={label} label={label!} value={value!} />)}
          <Stack gap={2}><Text className="romd-eyebrow">Platform</Text><Text component="div" size="sm" fw={600}><PlatformMark system={title.system} /></Text></Stack>
          </SimpleGrid>
        </div>
      </Tabs.Panel>
      <Tabs.Panel value="releases" className={classes.panel}>
        <div id="release-download" className={classes.releaseContent}>
          {title.releases.length === 0 ? <Text>No release is available yet.</Text> : title.releases.length === 1 ? (
            <ReleaseManifestPanel key={selectedRelease?.id} release={selectedRelease} autoDownload={downloadRelease === selectedRelease?.id} onDownloadStarted={() => setDownloadRelease(null)}
              playAction={playback?.status === 'playable' ? <Button component={TitleLink} to={`/titles/${title.id}/releases/${selectedRelease?.id}/play`} leftSection={<IconPlayerPlayFilled size={16} />}>Play</Button> : <Text size="sm" c="dimmed">{capability.isLoading ? 'Checking play availability…' : 'Browser play is unavailable for this release.'}</Text>} />
          ) : <Stack gap="md">
            <Text c="dimmed">Choose the version you want to play or download.</Text>
            <Accordion variant="separated" radius="md" value={selectedRelease?.id ?? null} onChange={id => { if (id) { setDownloadRelease(null); select('release', id); } }}>
              {title.releases.map(release => <Accordion.Item key={release.id} value={release.id}>
                <Accordion.Control><Stack gap="xs"><Group gap="sm" wrap="wrap"><Text fw={600}>{formatList(release.regions, release.name)}</Text>{release.id === title.defaultReleaseId && <Badge variant="light">Default</Badge>}{!release.isComplete && <Badge color="bronze">Missing files</Badge>}</Group><Text size="sm" c="dimmed">{[formatList(release.languages, 'Language unspecified'), release.revision, formatBytes(release.sizeBytes)].filter(Boolean).join(' · ')}</Text></Stack></Accordion.Control>
                <Accordion.Panel>{release.id === selectedRelease?.id && <ReleaseManifestPanel key={release.id} showSummary={false} release={release} autoDownload={downloadRelease === release.id} onDownloadStarted={() => setDownloadRelease(null)}
                  playAction={playback?.status === 'playable' ? <Button component={TitleLink} to={`/titles/${title.id}/releases/${release.id}/play`} leftSection={<IconPlayerPlayFilled size={16} />}>Play</Button> : <Text size="sm" c="dimmed">{capability.isLoading ? 'Checking play availability…' : 'Browser play is unavailable for this release.'}</Text>} />}</Accordion.Panel>
              </Accordion.Item>)}
            </Accordion>
          </Stack>}
        </div>
      </Tabs.Panel>
      <Tabs.Panel value="media" className={classes.panel}>
        {media.length ? <SimpleGrid cols={{ base: 2, sm: 3, lg: 4 }}>{media.map((item, index) => <AspectRatio key={item.id} ratio={4 / 3}><button className={classes.media} type="button" onClick={() => setActiveMedia(item)} aria-label={`View ${item.type} ${index + 1}`}><img src={item.url} alt={`${title.name} — ${item.type}`} loading="lazy" /></button></AspectRatio>)}</SimpleGrid> : <Text>No media is available yet.</Text>}
      </Tabs.Panel>
    </Tabs>
    <Modal opened={activeMedia !== null} onClose={() => setActiveMedia(null)} size="xl" title={activeMedia?.type ?? 'Media'} centered>{activeMedia && <Image src={activeMedia.url} alt={`${title.name} — ${activeMedia.type}`} fit="contain" mah="75vh" />}</Modal>
  </div>;
}

function getCapabilityGatedPreflight(
  title: Pick<ConsumerTitleDetailDto, 'system'>,
  release: ConsumerReleaseDto,
  capabilityResult: PlayerCapabilityResult | undefined,
  isLoading: boolean,
): BrowserPlaybackPreflight {
  const verificationUnavailableReason = getDownloadVerificationUnavailableReason();
  if (verificationUnavailableReason) {
    return {
      status: 'download-only',
      reason: verificationUnavailableReason,
    };
  }
  if (isLoading || !capabilityResult) {
    return {
      status: 'download-only',
      reason: 'Checking browser player availability.',
    };
  }
  if (capabilityResult.status === 'unavailable') {
    return {
      status: 'download-only',
      reason: capabilityResult.reason,
    };
  }
  return getBrowserPlaybackPreflight(title, release, capabilityResult.capability.cores);
}

interface HeroActionsProps {
  titleId: string;
  releases: ConsumerReleaseDto[];
  defaultReleaseId: string | null;
  selectedRelease: ConsumerReleaseDto | null;
  playback: ReturnType<typeof getBrowserPlaybackPreflight> | null;
  checking: boolean;
  onSelectRelease: (releaseId: string) => void;
  onDownload: () => void;
}

function HeroActions({
  titleId,
  releases,
  defaultReleaseId,
  selectedRelease,
  playback,
  checking,
  onSelectRelease,
  onDownload,
}: HeroActionsProps) {
  if (!selectedRelease) {
    return (
      <Text
        size="sm"
        c="dimmed"
      >
        No owned version is available.
      </Text>
    );
  }

  const playable = playback?.status === 'playable';

  return (
    <Stack gap={6}>
      <Group gap="sm">
        {checking ? <Button loading disabled>Checking play availability</Button> : playable ? (
          <Button
            size="md"
            component={TitleLink}
            to={`/titles/${titleId}/releases/${selectedRelease.id}/play`}
            color="mint"
            leftSection={<IconPlayerPlayFilled size={18} />}
          >
            Play Now
          </Button>
        ) : (
          <Button
            size="md"
            component="a"
            href="#release-download"
            onClick={onDownload}
            color="mint"
            leftSection={<IconDownload size={18} />}
          >
            Download
          </Button>
        )}



        {playable && (
          <Button
            size="md"
            component="a"
            href="#release-download"
            onClick={onDownload}
            variant="default"
            leftSection={<IconDownload size={18} />}
          >
            Download
          </Button>
        )}

        {releases.length > 1 && <ReleaseVersionMenu releases={releases} selectedRelease={selectedRelease} defaultReleaseId={defaultReleaseId} onSelectRelease={onSelectRelease} />}

      </Group>
      {!checking && !playable && playback?.status === 'download-only' && (
        <Text
          size="xs"
          c="dimmed"
        >
          {playback.reason}
        </Text>
      )}
    </Stack>
  );
}

interface InfoCellProps {
  label: string;
  value: string;
  mono?: boolean;
}

function InfoCell({ label, value, mono }: InfoCellProps) {
  return (
    <Stack gap={2}>
      <Text className="romd-eyebrow">{label}</Text>
      <Text
        fz={mono ? 12 : 'sm'}
        fw={600}
        ff={mono ? 'monospace' : undefined}
        style={{ wordBreak: 'break-word' }}
      >
        {value}
      </Text>
    </Stack>
  );
}
