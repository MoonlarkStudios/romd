import { Button, Group } from '@mantine/core';
import { IconDeviceFloppy, IconTrash } from '@tabler/icons-react';
import { workspaceActionProps } from './workspaceActions';

interface EditorFooterProps {
  dirty: boolean;
  saving?: boolean;
  disabled?: boolean;
  standalone?: boolean;
  onSave: () => void;
  onDiscard: () => void;
  onDelete?: () => void;
  deleteLabel?: string;
  saveLabel?: string;
}

export function EditorFooter({
  dirty,
  saving = false,
  disabled = false,
  standalone = true,
  onSave,
  onDiscard,
  onDelete,
  deleteLabel = 'Delete',
  saveLabel = 'Save changes',
}: EditorFooterProps) {
  return (
    <Group
      justify={onDelete ? 'space-between' : 'flex-end'}
      gap="sm"
      pt={standalone ? 'md' : undefined}
      style={
        standalone
          ? {
              borderTop: '1px solid var(--mantine-color-default-border)',
            }
          : undefined
      }
    >
      {onDelete && (
        <Button
          {...workspaceActionProps}
          variant="subtle"
          color="red"
          leftSection={<IconTrash size={16} />}
          onClick={onDelete}
          disabled={saving}
        >
          {deleteLabel}
        </Button>
      )}
      <Group
        gap="xs"
        ms="auto"
      >
        <Button
          {...workspaceActionProps}
          variant="default"
          onClick={onDiscard}
          disabled={!dirty || saving}
        >
          Discard
        </Button>
        <Button
          {...workspaceActionProps}
          leftSection={<IconDeviceFloppy size={16} />}
          loading={saving}
          disabled={!dirty || disabled}
          onClick={onSave}
        >
          {saveLabel}
        </Button>
      </Group>
    </Group>
  );
}
