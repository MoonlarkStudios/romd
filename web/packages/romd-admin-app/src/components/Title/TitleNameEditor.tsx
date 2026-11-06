import { Alert, Button, Group, Stack, TextInput } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { invalidateTitle } from '../../hooks/api/titleCache';
import { TitleConflictError } from '../../hooks/api/titleConflict';
import { useUpdateTitleMetadata } from '../../hooks/api/useTitleActions';
import type { TitleDetailView as TitleDetail } from '../../hooks/api/useTitleDetail';
import { titleDetailKeys, useTitleDetail } from '../../hooks/api/useTitleDetail';
import { workspaceActionProps } from '../Workspace/workspaceActions';
import { TitleConflictAlert } from './TitleConflictAlert';

export function TitleNameEditor({
  titleId,
  name,
  onClose,
  onSavingChange,
}: {
  titleId: string;
  name: string;
  onClose: () => void;
  onSavingChange: (saving: boolean) => void;
}) {
  const cache = useQueryClient();
  const [draft, setDraft] = useState(name);
  const [error, setError] = useState<string | null>(null);
  const [conflicted, setConflicted] = useState(false);
  const { data: title } = useTitleDetail(titleId);
  // The rename is checked against the title revision it started from; until the first edit it follows refreshes.
  const [baseRevision, setBaseRevision] = useState(title?.revision);
  if (draft === name && baseRevision !== title?.revision) setBaseRevision(title?.revision);
  const update = useUpdateTitleMetadata();
  const save = async () => {
    if (!baseRevision || !draft.trim() || update.isPending) return;
    onSavingChange(true);
    setError(null);
    try {
      await update.mutateAsync({
        titleId,
        revision: baseRevision,
        metadata: {
          name: draft.trim(),
        },
      });
      notifications.show({
        title: 'Title renamed',
        message: draft.trim(),
        color: 'green',
      });
      onClose();
    } catch (reason) {
      if (reason instanceof TitleConflictError) setConflicted(true);
      else setError('Could not rename this title. Please try again.');
    } finally {
      onSavingChange(false);
    }
  };
  const reloadLatest = async () => {
    await invalidateTitle(cache, titleId);
    setDraft(cache.getQueryData<TitleDetail>(titleDetailKeys.detail(titleId))?.name ?? name);
    setConflicted(false);
  };
  return (
    <form
      onSubmit={(event) => {
        event.preventDefault();
        void save();
      }}
    >
      <Stack>
        <TextInput
          label="Title name"
          value={draft}
          onChange={(event) => setDraft(event.currentTarget.value)}
          disabled={update.isPending}
          data-autofocus
        />
        {error && <Alert color="red">{error}</Alert>}
        {conflicted && (
          <TitleConflictAlert
            draftLabel="title name"
            onReload={reloadLatest}
          />
        )}
        <Group justify="flex-end">
          <Button
            variant="subtle"
            color="gray"
            onClick={onClose}
            disabled={update.isPending}
          >
            Cancel
          </Button>
          <Button
            {...workspaceActionProps}
            type="submit"
            loading={update.isPending}
            disabled={!baseRevision || !draft.trim() || draft.trim() === name || conflicted}
          >
            Save changes
          </Button>
        </Group>
      </Stack>
    </form>
  );
}
