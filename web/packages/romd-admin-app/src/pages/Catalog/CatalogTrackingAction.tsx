import { ActionIcon, Alert, Button, Group, Menu, Modal, Stack, Text } from '@mantine/core';
import type { CatalogTitle } from '@romd/admin-api-client';
import { IconBookmark, IconBookmarkOff, IconDotsVertical } from '@tabler/icons-react';
import { useState } from 'react';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import { useSetTitleTracking } from '../../hooks/api/useTitleActions';

export function UntrackConfirmation({ opened, count, busy, error, onClose, onConfirm }: {
  opened: boolean; count: number; busy: boolean; error?: string | null; onClose: () => void; onConfirm: () => void;
}) {
  return <Modal opened={opened} onClose={() => !busy && onClose()} title={count === 1 ? 'Untrack title?' : `Untrack ${count} titles?`} closeButtonProps={{ disabled: busy }}>
    <Stack>
      <Text size="sm">Remove {count === 1 ? 'this title' : 'these titles'} from your tracked catalog and the default enrichment scope? Files, saved metadata, and artwork are kept.</Text>
      <Text size="sm" c="dimmed">Existing library membership is not changed by untracking yet.</Text>
      {error && <Alert color="red">{error}</Alert>}
      <Group justify="flex-end"><Button variant="default" disabled={busy} onClick={onClose}>Cancel</Button><Button color="red" loading={busy} onClick={onConfirm}>Untrack</Button></Group>
    </Stack>
  </Modal>;
}

export function CatalogTrackingAction({ title }: { title: CatalogTitle }) {
  const mutation = useSetTitleTracking();
  const [confirm, setConfirm] = useState(false);
  const [menuOpened, setMenuOpened] = useState(false);
  const change = (tracked: boolean) => mutation.mutate({ titleId: title.id, tracked }, { onSuccess: () => setConfirm(false) });
  return <>
    {title.isTracked ? <Menu withinPortal opened={menuOpened && !confirm} onChange={setMenuOpened}>
      <Menu.Target><ActionIcon title="Title actions" aria-label={`Actions for ${title.name}`} variant="subtle" color="gray"><IconDotsVertical size={17} /></ActionIcon></Menu.Target>
      <Menu.Dropdown><Menu.Item leftSection={<IconBookmarkOff size={16} />} onClick={() => { setMenuOpened(false); setConfirm(true); }}>Untrack {title.name}</Menu.Item></Menu.Dropdown>
    </Menu> : <Button {...workspaceActionProps} size="compact-xs" variant="subtle" leftSection={<IconBookmark size={14} />} aria-label={`Track ${title.name}`} loading={mutation.isPending} onClick={() => change(true)}>Track</Button>}
    {mutation.isError && !confirm && <Text size="xs" c="red" role="alert">Could not track title. Try again.</Text>}
    <UntrackConfirmation opened={confirm} count={1} busy={mutation.isPending} error={mutation.isError ? 'Could not untrack title. Try again.' : null} onClose={() => { setConfirm(false); mutation.reset(); }} onConfirm={() => change(false)} />
  </>;
}
