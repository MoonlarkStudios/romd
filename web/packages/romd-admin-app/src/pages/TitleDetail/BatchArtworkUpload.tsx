import { ActionIcon, Alert, Badge, Button, FileInput, Group, Image, NativeSelect, Progress, Stack, Text } from '@mantine/core';
import { IconTrash, IconUpload } from '@tabler/icons-react';
import { useEffect, useRef, useState } from 'react';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import { useUploadTitleMedia } from '../../hooks/api/useTitleActions';
import classes from './ArtworkWorkspace.module.css';

export const imageTypes = ['Cover', 'Screenshot', 'Background', 'Banner', 'Logo', 'Box3d', 'TitleScreen'];
export const typeLabel = (type: string) => ({ Box3d: '3D box', TitleScreen: 'Title screen' })[type] ?? type;
type UploadItem = { id: number; file: File; url: string; type: string; status: 'ready' | 'uploading' | 'saved' | 'failed'; progress?: number; error?: string };

export function BatchArtworkUpload({ titleId, onBusyChange }: { titleId: string; onBusyChange?: (busy: boolean) => void }) {
  const upload = useUploadTitleMedia();
  const [items, setItems] = useState<UploadItem[]>([]);
  const [defaultType, setDefaultType] = useState('Screenshot');
  const [busy, setBusy] = useState(false);
  const [attempt, setAttempt] = useState({ completed: 0, total: 0 });
  const saving = useRef(false);
  const [error, setError] = useState<string | null>(null);
  const urls = useRef(new Set<string>());
  const sequence = useRef(0);
  useEffect(() => () => { for (const url of urls.current) URL.revokeObjectURL(url); }, []);
  const add = (files: File[]) => {
    if (busy) return;
    const valid = files.filter(file => ['image/jpeg', 'image/png', 'image/webp'].includes(file.type) && file.size <= 32 * 1024 * 1024);
    setError(valid.length !== files.length ? 'Some files were skipped. Choose JPEG, PNG, or WebP images up to 32 MB each.' : null);
    setItems(previous => [...previous, ...valid.filter(file => !previous.some(item => item.file.name === file.name && item.file.size === file.size && item.file.lastModified === file.lastModified)).map(file => {
      const url = URL.createObjectURL(file); urls.current.add(url);
      return { id: sequence.current++, file, url, type: defaultType, status: 'ready' as const };
    })]);
  };
  const patch = (id: number, values: Partial<UploadItem>) => setItems(previous => previous.map(item => item.id === id ? { ...item, ...values } : item));
  const pending = items.filter(item => item.status === 'ready' || item.status === 'failed');
  const save = async () => {
    if (saving.current || !pending.length) return;
    saving.current = true;
    setAttempt({ completed: 0, total: pending.length });
    setBusy(true); onBusyChange?.(true);
    try {
      for (const item of pending) {
        patch(item.id, { status: 'uploading', progress: 0, error: undefined });
        try { await upload.mutateAsync({ titleId, file: item.file, type: item.type.toLowerCase(), onProgress: ({ loaded, total }) => patch(item.id, { progress: total > 0 ? Math.min(100, loaded / total * 100) : 0 }) }); patch(item.id, { status: 'saved' }); }
        catch { patch(item.id, { status: 'failed', error: 'Upload failed. Retry this image.' }); }
        finally { setAttempt(value => ({ ...value, completed: value.completed + 1 })); }
      }
    } finally { saving.current = false; setBusy(false); onBusyChange?.(false); }
  };
  return <Stack>
    <div className={classes.dropzone} onDragOver={event => event.preventDefault()} onDrop={event => { event.preventDefault(); add(Array.from(event.dataTransfer.files)); }}>
      <Stack gap="xs"><Text fw={600}>Drop your images here</Text><Text size="sm" c="dimmed">JPEG, PNG, or WebP · Up to 32 MB each. Images are added to the library; your poster, hero, and logo stay as selected.</Text>
        <Group align="end"><NativeSelect label="Type for new files" data={imageTypes.map(value => ({ value, label: typeLabel(value) }))} value={defaultType} disabled={busy} onChange={event => setDefaultType(event.currentTarget.value)} />
          <FileInput multiple label="Choose images" placeholder="Browse files" accept="image/jpeg,image/png,image/webp" value={[]} onChange={add} disabled={busy} style={{ flex: 1, minWidth: 180 }} /></Group>
      </Stack>
    </div>
    {error && <Alert color="orange">{error}</Alert>}
    {items.map(item => <Group key={item.id} wrap="nowrap" align="center">
      <Image src={item.url} alt="" w={64} h={56} fit="contain" />
      <Stack gap={2} style={{ flex: 1, minWidth: 0 }}><Text size="sm" truncate>{item.file.name}</Text>{item.error && <Text size="xs" c="red">{item.error}</Text>}
        {item.status === 'uploading' && <><Progress aria-label={`Transfer progress for ${item.file.name}`} value={item.progress ?? 0} /><Text size="xs" c="dimmed">{item.progress === 100 ? 'Saving image...' : `Uploading ${Math.floor(item.progress ?? 0)}%`}</Text></>}
      </Stack>
      <NativeSelect aria-label={`Type for ${item.file.name}`} data={imageTypes.map(value => ({ value, label: typeLabel(value) }))} value={item.type} disabled={busy || item.status === 'saved'} onChange={event => patch(item.id, { type: event.currentTarget.value })} w={130} />
      {item.status === 'saved' ? <Badge color="teal">Added</Badge> : <ActionIcon aria-label={`Remove ${item.file.name}`} variant="subtle" color="gray" disabled={busy} onClick={() => { URL.revokeObjectURL(item.url); urls.current.delete(item.url); setItems(previous => previous.filter(value => value.id !== item.id)); }}><IconTrash size={16} /></ActionIcon>}
    </Group>)}
    {!!items.length && <div className={classes.footer}><Stack gap="xs">
      {busy && <><Progress aria-label="Batch progress" value={attempt.total ? attempt.completed / attempt.total * 100 : 0} /><Text size="xs">{attempt.completed} of {attempt.total} processed this attempt</Text></>}
      <Group justify="space-between"><Text size="sm" role="status">{items.filter(item => item.status === 'saved').length} of {items.length} added</Text><Button {...workspaceActionProps} leftSection={<IconUpload size={16} />} loading={busy} disabled={!pending.length} onClick={() => void save()}>{items.some(item => item.status === 'failed') ? 'Retry remaining images' : `Add ${pending.length} ${pending.length === 1 ? 'image' : 'images'}`}</Button></Group>
    </Stack></div>}
  </Stack>;
}
