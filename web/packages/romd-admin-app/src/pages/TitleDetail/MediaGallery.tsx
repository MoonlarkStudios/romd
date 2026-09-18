import { ActionIcon, Alert, Anchor, Badge, Button, Card, FileInput, Group, Image, Menu, Modal, NativeSelect, SimpleGrid, Stack, Text, Tooltip, UnstyledButton } from '@mantine/core';
import type { ResolvedArtworkDto, SavedArtworkDto, TitleMediaRef } from '@romd/admin-api-client';
import { IconCheck, IconDotsVertical, IconLayoutNavbar, IconPhoto, IconTrash, IconUpload } from '@tabler/icons-react';
import { useState } from 'react';
import { ArtworkSurface } from '../../components/ArtworkSurface';
import { type ArtworkRole, artworkPresentation, artworkRoleLabel, artworkRoles, artworkSourceLabel, isArtworkRole } from '../../components/artworkPresentation';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import { useDeleteTitleMedia, useUploadTitleMedia } from '../../hooks/api/useTitleActions';
import { usePermissions } from '../../hooks/usePermissions';
import { formatBytes } from '../../utils/format';
import { typeLabel } from './BatchArtworkUpload';

export type { ArtworkRole } from '../../components/artworkPresentation';
export type SavedArtworkTarget = { assetId: string; mediaId?: never } | { mediaId: string; assetId?: never };
const mediaTypes = ['Cover', 'Screenshot', 'Background', 'Banner', 'Logo'];

export function MediaUploadForm({ titleId, onUploaded }: { titleId: string; onUploaded: (media: TitleMediaRef) => void | Promise<void> }) {
  const upload = useUploadTitleMedia();
  const [file, setFile] = useState<File | null>(null);
  const [type, setType] = useState('Cover');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  return <form onSubmit={async (event) => {
    event.preventDefault(); if (!file || busy) return;
    setError(null); setBusy(true);
    try { const media = await upload.mutateAsync({ titleId, file, type: type.toLowerCase() }); setFile(null); await onUploaded(media); }
    catch { setError('Could not upload image. Try again.'); }
    finally { setBusy(false); }
  }}><Stack>
    <FileInput label="Image" placeholder="Select image" accept="image/jpeg,image/png,image/webp" value={file} onChange={setFile} leftSection={<IconPhoto size={16} />} disabled={busy} />
    <NativeSelect label="Image type" data={mediaTypes} value={type} onChange={(event) => setType(event.currentTarget.value)} disabled={busy} />
    {error && <Alert color="red">{error}</Alert>}
    <Group justify="flex-end"><Button type="submit" leftSection={<IconUpload size={16} />} disabled={!file} loading={busy}>Upload image</Button></Group>
  </Stack></form>;
}

interface MediaGalleryProps {
  media: TitleMediaRef[];
  titleId: string;
  saved?: SavedArtworkDto[];
  artwork?: ResolvedArtworkDto[];
  role?: ArtworkRole;
  onAssign?: (role: ArtworkRole, target: SavedArtworkTarget, url?: string) => Promise<boolean>;
  busy?: boolean;
  onAdd?: () => void;
}

export function MediaGallery({ media, titleId, saved = [], artwork = [], role, onAssign, busy = false, onAdd }: MediaGalleryProps) {
  const { canManageTitles } = usePermissions();
  const remove = useDeleteTitleMedia();
  const [type, setType] = useState('');
  const [source, setSource] = useState('');
  const [uploadOpen, setUploadOpen] = useState(false);
  const [deleting, setDeleting] = useState<TitleMediaRef | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [preview, setPreview] = useState<{ url: string; name: string } | null>(null);
  const entries = [
    ...media.map((item) => ({ key: `media-${item.id}`, name: item.type, source: item.sourceId.replace('gallery:', ''), url: item.url,
      target: { mediaId: item.id } as SavedArtworkTarget, media: item, asset: undefined as SavedArtworkDto | undefined,
      roles: artworkRoles.filter((itemRole) => artwork.some((art) => art.role === itemRole &&
        (art.url === item.url || saved.some((asset) => asset.id === art.assetId && asset.mediaIds.includes(item.id))))) })),
    ...saved.filter((asset) => !asset.mediaIds.some((id) => media.some((item) => item.id === id))).map((asset) => ({
      key: `asset-${asset.id}`, name: isArtworkRole(asset.role) ? artworkPresentation[asset.role].mediaType : asset.role, source: asset.sourceId.replace('gallery:', ''), url: asset.artwork.url ?? '',
      target: { assetId: asset.id } as SavedArtworkTarget, media: undefined as TitleMediaRef | undefined, asset,
      roles: artworkRoles.filter((itemRole) => artwork.some((art) => art.role === itemRole && art.assetId === asset.id)),
    })),
  ];
  const visible = entries.filter((entry) => (!type || entry.name === type) && (!source || entry.source === source)
    && (!role || !entry.asset || entry.asset.role === role));
  return <Stack gap="md">
    <Group justify="space-between" align="end">
      <Group><NativeSelect label="Type" value={type} onChange={(event) => setType(event.currentTarget.value)} data={[{ value: '', label: 'All types' }, ...Array.from(new Set(entries.map((entry) => entry.name))).sort()]} />
        <NativeSelect label="Source" value={source} onChange={(event) => setSource(event.currentTarget.value)} data={[{ value: '', label: 'All sources' }, ...Array.from(new Set(entries.map((entry) => entry.source))).sort().map(value => ({ value, label: artworkSourceLabel(value) }))]} /></Group>
      {canManageTitles && !role && !onAdd && <Button variant="light" leftSection={<IconUpload size={16} />} onClick={() => setUploadOpen(true)}>Upload</Button>}
    </Group>
    {error && <Alert color="red">{error}</Alert>}
    {visible.length === 0 ? <Text size="sm" c="dimmed" py="md">{entries.length ? 'No images match these filters.' : 'Your artwork library is empty. Add images from a provider or upload your own.'}</Text> :
      <SimpleGrid cols={{ base: 1, xs: 2, md: role ? 3 : 4 }} spacing="md">{visible.map((entry) => <Card key={entry.key} withBorder radius="sm" p="xs">
        <UnstyledButton aria-label={`Preview ${entry.name} from ${entry.source}`} onClick={() => setPreview({ url: entry.url, name: `${entry.name} · ${entry.source}` })}>
          <ArtworkSurface h={180} background={entry.name === 'Logo' ? 'checkerboard' : 'dark'}><Image src={entry.url} alt={`${entry.name} from ${entry.source}`} h="100%" fit="contain" /></ArtworkSurface>
        </UnstyledButton>
        <Group justify="space-between" wrap="nowrap" mt="xs">
          <Stack gap={2} style={{ minWidth: 0 }}><Text size="sm" fw={500}>{typeLabel(entry.name)}</Text><Text size="xs" c="dimmed" style={{ overflowWrap: 'anywhere' }}>{artworkSourceLabel(entry.source)}</Text></Stack>
          {canManageTitles && <Menu withinPortal><Menu.Target><Tooltip label="Image actions"><ActionIcon aria-label={`Actions for ${entry.name} ${entry.media?.id ?? entry.asset?.id}`} variant="subtle" color="gray"><IconDotsVertical size={16} /></ActionIcon></Tooltip></Menu.Target>
            <Menu.Dropdown>
              {onAssign && (role ? [role] : entry.asset ? (isArtworkRole(entry.asset.role) ? [entry.asset.role] : []) : artworkRoles).map((itemRole) => <Menu.Item key={itemRole} disabled={busy} leftSection={itemRole === 'Hero' ? <IconLayoutNavbar size={16} /> : <IconPhoto size={16} />} onClick={() => {
                const retained = entry.media && saved.find((asset) => asset.role === itemRole && asset.mediaIds.includes(entry.media!.id));
                void onAssign(itemRole, retained ? { assetId: retained.id } : entry.target, entry.url);
              }}>Use as {artworkRoleLabel(itemRole).toLowerCase()}</Menu.Item>)}
              {entry.media && !role && <><Menu.Divider /><Menu.Item color="red" leftSection={<IconTrash size={16} />} onClick={() => setDeleting(entry.media!)}>Delete image</Menu.Item></>}
            </Menu.Dropdown></Menu>}
        </Group>
        <Group gap={4} mt="xs">{entry.roles.map((selectedRole) => <Badge key={selectedRole} size="xs" color="teal" variant="light" leftSection={<IconCheck size={10} />}>Selected {artworkRoleLabel(selectedRole).toLowerCase()}</Badge>)}</Group>
        {role && onAssign && <Button {...workspaceActionProps} variant="light" size="xs" mt="xs" disabled={busy} onClick={() => {
          const retained = entry.media && saved.find(asset => asset.role === role && asset.mediaIds.includes(entry.media!.id));
          void onAssign(role, retained ? { assetId: retained.id } : entry.target, entry.url);
        }}>Select image</Button>}
        {entry.media?.file && <Text size="xs" c="dimmed">{formatBytes(entry.media.file.sizeOnDiskBytes)}</Text>}
        {(entry.asset?.attribution ?? entry.media?.attribution) && <Text size="xs" c="dimmed" mt="xs">By {entry.asset?.attribution ?? entry.media?.attribution}</Text>}
        {(entry.asset?.sourcePageUrl ?? entry.media?.sourcePageUrl) && <Anchor size="xs" href={entry.asset?.sourcePageUrl ?? entry.media?.sourcePageUrl ?? undefined} target="_blank" rel="noreferrer">Source</Anchor>}
      </Card>)}</SimpleGrid>}
    <Modal opened={uploadOpen} onClose={() => setUploadOpen(false)} title="Upload image" closeButtonProps={{ 'aria-label': 'Close upload' }}><MediaUploadForm titleId={titleId} onUploaded={() => setUploadOpen(false)} /></Modal>
    <Modal opened={preview !== null} onClose={() => setPreview(null)} title={preview?.name} size="xl" closeButtonProps={{ 'aria-label': 'Close image preview' }}><ArtworkSurface><Image src={preview?.url} alt={preview?.name ?? ''} fit="contain" mah="70vh" /></ArtworkSurface></Modal>
    <Modal opened={deleting !== null} onClose={() => !remove.isPending && setDeleting(null)} title="Delete image?" closeButtonProps={{ 'aria-label': 'Close delete confirmation' }}>
      <Stack><Text size="sm">Remove this image from the gallery? Retained poster, backdrop, banner, and logo copies are preserved.</Text>
        <Group justify="flex-end"><Button variant="default" disabled={remove.isPending} onClick={() => setDeleting(null)}>Cancel</Button><Button color="red" loading={remove.isPending} onClick={async () => {
          if (!deleting) return;
          try { await remove.mutateAsync({ titleId, mediaId: deleting.id }); setDeleting(null); setError(null); }
          catch { setError('Could not delete image. Try again.'); setDeleting(null); }
        }}>Delete image</Button></Group></Stack>
    </Modal>
  </Stack>;
}
