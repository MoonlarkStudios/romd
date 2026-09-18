import { ActionIcon, Alert, Badge, Box, Group, Modal, Stack, Text, Title, Tooltip, UnstyledButton } from '@mantine/core';
import type { TitleDetail } from '@romd/admin-api-client';
import { IconBookmark, IconBookmarkOff, IconDownload, IconEdit, IconPhoto, IconRefresh } from '@tabler/icons-react';
import { useState } from 'react';
import { ArtworkFrame } from '../../components/ArtworkFrame';
import { EnrichmentStatusBadge } from '../../components/EnrichmentStatusBadge';
import { usePlatform } from '../../hooks/api/usePlatforms';
import { useSetTitleTracking, useTriggerEnrichment } from '../../hooks/api/useTitleActions';
import { useTitleSourceReferences } from '../../hooks/api/useTitleDetail';
import { usePermissions } from '../../hooks/usePermissions';
import { TitleActionMenu } from './TitleActionMenu';
import classes from './TitleDetail.module.css';
import { TitleNameEditor } from './TitleNameEditor';

export function TitleHero({ title, titleId, previewOnly = false, previewMobile = false }: { title: TitleDetail; titleId: string; previewOnly?: boolean; previewMobile?: boolean }) {
  const [preview, setPreview] = useState(false);
  const [editingName, setEditingName] = useState(false);
  const [savingName, setSavingName] = useState(false);
  const { canManageTitles } = usePermissions();
  const poster = title.artwork?.find((artwork) => artwork.role === 'Poster');
  const hero = title.artwork?.find((artwork) => artwork.role === 'Hero');
  const cover = title.media?.find((media) => media.type?.toLowerCase() === 'cover' && media.isPrimary)
    ?? title.media?.find((media) => media.type?.toLowerCase() === 'cover');
  const sources = useTitleSourceReferences(previewOnly ? undefined : titleId);
  const { data: platform } = usePlatform(title.systemKey);
  const enrich = useTriggerEnrichment();
  const tracking = useSetTitleTracking();
  const owned = title.releases?.filter((release) => release.isComplete).length ?? 0;

  return (
    <Stack gap="sm">
      <Box className={classes.hero} data-testid="title-header" data-preview-only={previewOnly || undefined} data-preview-mobile={previewMobile || undefined}>
        {hero?.url && <img className={classes.backdrop} src={hero.url} alt="" style={{ objectPosition: `${hero.focalX ?? 50}% ${hero.focalY ?? 50}%` }} />}
        <div className={classes.heroShade} />
        <div className={classes.identity}>
          <div className={classes.poster}>
            <ArtworkFrame role="Poster" src={poster?.url ?? cover?.url} title={title.name} fit={poster?.fit === 'Cover' ? 'Cover' : 'Contain'} />
          </div>
          <Stack gap="sm" className={classes.identityText}>
            {platform && <Text size="xs" fw={600}>{platform.name}</Text>}
            <Group gap="xs" wrap="nowrap" align="flex-start">
              <Title order={1} className={classes.title}>{title.name}</Title>
              {canManageTitles && <Tooltip label="Edit title name"><ActionIcon aria-label="Edit title name" variant="subtle" color="gray" style={{ flexShrink: 0 }} onClick={() => setEditingName(true)}><IconEdit size={16} /></ActionIcon></Tooltip>}
            </Group>
            <Group gap="xs">
              {title.releaseDate && <Text size="sm">{title.releaseDate.slice(0, 4)}</Text>}
              {title.genre && <Badge variant="light" color="blue">{title.genre}</Badge>}
              <EnrichmentStatusBadge status={title.enrichmentStatus} />
            </Group>
            {!previewOnly && <Group gap="xs">
              <Tooltip label={title.isTracked ? 'Untrack title' : 'Track title'}>
                <ActionIcon aria-label={title.isTracked ? 'Untrack title' : 'Track title'} color={title.isTracked ? 'yellow' : 'gray'} variant="light" size="lg" loading={tracking.isPending} onClick={() => tracking.mutate({ titleId, tracked: !title.isTracked })}>
                  {title.isTracked ? <IconBookmark size={18} /> : <IconBookmarkOff size={18} />}
                </ActionIcon>
              </Tooltip>
              <Tooltip label="Refresh provider data">
                <ActionIcon aria-label="Refresh provider data" variant="light" color="gray" size="lg" loading={enrich.isPending} disabled={title.enrichmentStatus === 'Pending'} onClick={() => enrich.mutate(titleId)}><IconRefresh size={18} /></ActionIcon>
              </Tooltip>
              {owned > 0 && <Tooltip label="Download best available version"><ActionIcon aria-label="Download best available version" component="a" href={`/api/titles/${titleId}/download`} variant="light" color="green" size="lg"><IconDownload size={18} /></ActionIcon></Tooltip>}
              <TitleActionMenu title={title} titleId={titleId} />
            </Group>}
          </Stack>
        </div>
        {hero?.url && <Tooltip label="Preview hero artwork"><UnstyledButton className={classes.preview} aria-label="Preview hero artwork" onClick={() => setPreview(true)}><IconPhoto size={18} /></UnstyledButton></Tooltip>}
      </Box>
      {!previewOnly && sources.data && !sources.data.some((source) => source.hasActiveDefinition) && <Alert color="gray" title="No active catalog definition">Your title and personal state remain saved. Add or re-enable a catalog source to restore definitions.</Alert>}
      <Modal opened={preview} onClose={() => setPreview(false)} title={`${title.name} artwork`} size="xl">
        <img src={hero?.url ?? undefined} alt={`${title.name} hero`} style={{ display: 'block', width: '100%', maxHeight: '75vh', objectFit: 'contain' }} />
      </Modal>
      <Modal opened={editingName} onClose={() => !savingName && setEditingName(false)} title="Edit title name" closeButtonProps={{ disabled: savingName }}>
        <TitleNameEditor key={`${titleId}-${editingName}`} titleId={titleId} name={title.name} onClose={() => setEditingName(false)} onSavingChange={setSavingName} />
      </Modal>
    </Stack>
  );
}
