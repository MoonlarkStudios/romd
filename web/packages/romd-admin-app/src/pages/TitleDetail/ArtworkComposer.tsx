import { ActionIcon, Alert, Badge, Box, Button, Group, Modal, SegmentedControl, SimpleGrid, Slider, Stack, Text, Tooltip, UnstyledButton } from '@mantine/core';
import type { ArtworkCandidateDto, TitleDetail } from '@romd/admin-api-client';
import { IconDeviceDesktop, IconDeviceMobile, IconFocus2, IconRestore } from '@tabler/icons-react';
import { useState } from 'react';
import { ArtworkFrame } from '../../components/ArtworkFrame';
import { artworkRoleLabel } from '../../components/artworkPresentation';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import { BackdropPreview } from './BackdropPreview';
import type { ArtworkRole, SavedArtworkTarget } from './MediaGallery';
import classes from './TitleDetail.module.css';
import { TitleHero } from './TitleHero';

export interface ArtworkDraft {
  role: ArtworkRole;
  target?: SavedArtworkTarget;
  candidate?: ArtworkCandidateDto;
  file?: File;
  url: string;
  revision: string;
  focalX: number;
  focalY: number;
}

export function ArtworkComposer({ title, titleId, draft, busy, error, onCancel, onSave }: {
  title: TitleDetail; titleId: string; draft: ArtworkDraft; busy: boolean; error: string | null;
  onCancel: () => void; onSave: (draft: ArtworkDraft) => void;
}) {
  const [focalX, setFocalX] = useState(draft.focalX);
  const [focalY, setFocalY] = useState(draft.focalY);
  const [viewport, setViewport] = useState(() => window.matchMedia('(max-width: 48em)').matches ? 'mobile' : 'desktop');
  const [comparison, setComparison] = useState('preview');
  const current = title.artwork?.find((item) => item.role === draft.role);
  const previewTitle: TitleDetail = comparison === 'current' ? title : { ...title, artwork: [
    ...(title.artwork ?? []).filter((item) => item.role !== draft.role),
    { role: draft.role, assetId: null, contentVersion: null, width: null, height: null,
      originalWidth: null, originalHeight: null, url: draft.url, focalX, focalY, fit: 'Cover', fallbackReason: 'None', variants: [] },
  ] };
  return <Modal opened onClose={() => !busy && onCancel()} title={`Compose ${artworkRoleLabel(draft.role).toLowerCase()}`} size={1000}
    closeButtonProps={{ 'aria-label': 'Close artwork preview', disabled: busy }}>
    <div className={classes.editor}>
      <div className={classes.editorBody}>
        <Stack gap="md">
          <Group justify="space-between">
            <SegmentedControl aria-label="Artwork comparison" value={comparison} onChange={setComparison} data={[{ value: 'current', label: 'Current' }, { value: 'preview', label: 'Preview' }]} />
            {draft.role !== 'Logo' && <Group gap={4}>
              <Tooltip label="Desktop preview"><ActionIcon aria-label="Desktop preview" aria-pressed={viewport === 'desktop'} variant={viewport === 'desktop' ? 'light' : 'subtle'} onClick={() => setViewport('desktop')}><IconDeviceDesktop size={18} /></ActionIcon></Tooltip>
              <Tooltip label="Mobile preview"><ActionIcon aria-label="Mobile preview" aria-pressed={viewport === 'mobile'} variant={viewport === 'mobile' ? 'light' : 'subtle'} onClick={() => setViewport('mobile')}><IconDeviceMobile size={18} /></ActionIcon></Tooltip>
            </Group>}
          </Group>
          {draft.role === 'Backdrop' && draft.candidate && <Text size="sm" c="dimmed">{draft.candidate.width} × {draft.candidate.height}{draft.candidate.width < 1920 || draft.candidate.height < 1080 ? ' · Below full HD; may look soft on a large display.' : ''}</Text>}
          {draft.role === 'Backdrop' && <BackdropPreview title={previewTitle} mobile={viewport === 'mobile'} />}
          {draft.role !== 'Logo' && draft.role !== 'Backdrop' && <Box mx="auto" w="100%" maw={viewport === 'mobile' ? 360 : undefined} data-testid="artwork-composition-preview">
            <TitleHero title={previewTitle} titleId={titleId} previewOnly previewMobile={viewport === 'mobile'} />
          </Box>}
          {draft.role === 'Poster' && <Box w={140} mx="auto">
            <ArtworkFrame role="Poster" src={comparison === 'current' ? current?.url : draft.url} title={title.name} fit={comparison === 'current' && current?.fit === 'Contain' ? 'Contain' : 'Cover'} />
            <Text size="sm" fw={500} mt="xs" style={{ overflowWrap: 'anywhere' }}>{title.name}</Text>
          </Box>}
          {draft.role === 'Logo' && <Stack>
            <Text size="sm">Inspect the complete logo on both backgrounds before saving.</Text>
            <SimpleGrid cols={{ base: 1, sm: 2 }}>
              {(['dark', 'light'] as const).map(background => <Stack key={background} gap="xs">
                <Text size="sm">{background === 'dark' ? 'Dark background' : 'Light background'}</Text>
                <ArtworkFrame role="Logo" src={comparison === 'current' ? current?.url : draft.url} title={title.name} fit="Contain" background={background} />
              </Stack>)}
            </SimpleGrid>
            {draft.candidate && <Text size="sm" c="dimmed">{draft.candidate.width} × {draft.candidate.height}{draft.candidate.style ? ` · ${draft.candidate.style}` : ''}{draft.candidate.attribution ? ` · By ${draft.candidate.attribution}` : ''}</Text>}
          </Stack>}
          {(draft.role === 'Hero' || draft.role === 'Backdrop') && <Group align="flex-start" gap="lg">
            <Stack gap="xs" style={{ flex: '1 1 240px', minWidth: 0 }}>
              <Group justify="space-between"><Text size="sm" fw={600}>Focal point</Text><Tooltip label="Center focal point"><ActionIcon aria-label="Center focal point" variant="subtle" disabled={busy} onClick={() => { setFocalX(50); setFocalY(50); setComparison('preview'); }}><IconRestore size={17} /></ActionIcon></Tooltip></Group>
              <Box><Text size="xs" mb={6}>Horizontal</Text><Slider thumbLabel="Horizontal focal point" value={focalX} onChange={(value) => { setFocalX(value); setComparison('preview'); }} disabled={busy} label={(value) => `${value}%`} /></Box>
              <Box mt="xs"><Text size="xs" mb={6}>Vertical</Text><Slider thumbLabel="Vertical focal point" value={focalY} onChange={(value) => { setFocalY(value); setComparison('preview'); }} disabled={busy} label={(value) => `${value}%`} /></Box>
            </Stack>
            <UnstyledButton aria-label="Set focal point on image" disabled={busy} style={{ position: 'relative', display: 'inline-flex', maxWidth: '100%', margin: 'auto' }} onClick={(event) => {
              const bounds = event.currentTarget.getBoundingClientRect();
              setFocalX(event.detail ? Math.max(0, Math.min(100, Math.round((event.clientX - bounds.left) / bounds.width * 100))) : 50);
              setFocalY(event.detail ? Math.max(0, Math.min(100, Math.round((event.clientY - bounds.top) / bounds.height * 100))) : 50);
              setComparison('preview');
            }}>
              <img src={draft.url} alt="Artwork focal point" style={{ display: 'block', maxHeight: 140, maxWidth: '100%' }} />
              <IconFocus2 size={28} style={{ position: 'absolute', left: `${focalX}%`, top: `${focalY}%`, transform: 'translate(-50%, -50%)', color: 'white', filter: 'drop-shadow(0 1px 2px black)', pointerEvents: 'none' }} />
            </UnstyledButton>
          </Group>}
        </Stack>
      </div>
      <div className={classes.editorFooter}>
        {error && <Alert color="red" mb="sm">{error}</Alert>}
        <Group justify="space-between"><Badge variant="light" color="yellow">Unsaved preview</Badge><Group gap="xs">
          <Button variant="default" disabled={busy} onClick={onCancel}>Cancel</Button>
          <Button {...workspaceActionProps} loading={busy} onClick={() => onSave({ ...draft, focalX, focalY })}>Save changes</Button>
        </Group></Group>
      </div>
    </div>
  </Modal>;
}
