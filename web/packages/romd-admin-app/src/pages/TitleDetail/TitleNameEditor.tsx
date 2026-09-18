import { Alert, Button, Group, Stack, TextInput } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useState } from 'react';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import { useUpdateTitleMetadata } from '../../hooks/api/useTitleActions';
import { useTitleEnrichment } from '../../hooks/api/useTitleEnrichment';

export function TitleNameEditor({ titleId, name, onClose, onSavingChange }: {
  titleId: string; name: string; onClose: () => void; onSavingChange: (saving: boolean) => void;
}) {
  const [draft, setDraft] = useState(name);
  const [error, setError] = useState<string | null>(null);
  const enrichment = useTitleEnrichment(titleId);
  const update = useUpdateTitleMetadata();
  const save = async () => {
    if (!enrichment.data || !draft.trim() || update.isPending) return;
    const user = enrichment.data.layers.find((layer) => layer.sourceId.toLowerCase() === 'user');
    onSavingChange(true);
    setError(null);
    try {
      // This endpoint replaces the user layer, so preserve the other custom values.
      await update.mutateAsync({ titleId, metadata: {
        name: draft.trim(), description: user?.description ?? null,
        genre: user?.genre ?? null, releaseDate: user?.releaseDate ?? null,
        publisher: user?.publisher ?? null, developer: user?.developer ?? null,
        players: user?.players ?? null, rating: user?.rating ?? null,
      } });
      notifications.show({ title: 'Title renamed', message: draft.trim(), color: 'green' });
      onClose();
    } catch {
      setError('Could not rename this title. Please try again.');
    } finally { onSavingChange(false); }
  };
  return (
    <form onSubmit={(event) => { event.preventDefault(); void save(); }}>
      <Stack>
        <TextInput label="Title name" value={draft} onChange={(event) => setDraft(event.currentTarget.value)} disabled={update.isPending} data-autofocus />
        {(error || enrichment.isError) && <Alert color="red">{error ?? 'Could not load existing metadata. Reopen this editor to retry.'}</Alert>}
        <Group justify="flex-end">
          <Button variant="subtle" color="gray" onClick={onClose} disabled={update.isPending}>Cancel</Button>
          <Button {...workspaceActionProps} type="submit" loading={update.isPending} disabled={!enrichment.data || enrichment.isError || !draft.trim() || draft.trim() === name}>Save changes</Button>
        </Group>
      </Stack>
    </form>
  );
}
