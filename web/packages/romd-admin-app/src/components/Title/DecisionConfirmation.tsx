import { Alert, Button, Group, Modal, Stack, Text } from '@mantine/core';
import { useState } from 'react';
import { workspaceActionProps } from '../Workspace/workspaceActions';
import type { DecisionConfirmationCopy } from './enrichmentPresentation';

/**
 * Confirms an immediate curation decision; decisions are mutations, never drafts. Cancelling returns focus to
 * the control that opened it; after success it does too, unless the host moves focus itself.
 */
export function DecisionConfirmation({
  copy,
  onCancel,
  onConfirm,
  onConfirmed,
  returnFocusAfterConfirm = true,
}: {
  copy: DecisionConfirmationCopy | undefined;
  onCancel: () => void;
  onConfirm: () => Promise<unknown>;
  onConfirmed: () => void;
  returnFocusAfterConfirm?: boolean;
}) {
  const [pending, setPending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [confirmed, setConfirmed] = useState(false);
  // Callers clear the copy to close; keep showing the last copy through the exit transition.
  const [shown, setShown] = useState(copy);
  if (
    copy &&
    (copy.title !== shown?.title ||
      copy.message !== shown?.message ||
      copy.confirmLabel !== shown?.confirmLabel)
  )
    setShown(copy);
  const content = copy ?? shown;
  const opened = copy !== undefined;
  if (opened && confirmed) setConfirmed(false);
  const cancel = () => {
    if (pending) return;
    setError(null);
    onCancel();
  };
  const confirm = async () => {
    setPending(true);
    setError(null);
    try {
      await onConfirm();
      setConfirmed(true);
      onConfirmed();
    } catch (reason) {
      setError(
        reason instanceof Error ? reason.message : 'Could not save this decision. Try again.',
      );
    } finally {
      setPending(false);
    }
  };
  return (
    <Modal
      opened={opened}
      onClose={cancel}
      title={content?.title}
      centered
      returnFocus={returnFocusAfterConfirm || !confirmed}
      closeOnEscape={!pending}
      closeOnClickOutside={!pending}
      withCloseButton={!pending}
    >
      <Stack>
        <Text>{content?.message}</Text>
        {error && <Alert color="red">{error}</Alert>}
        <Group justify="flex-end">
          <Button
            variant="default"
            disabled={pending}
            onClick={cancel}
          >
            Cancel
          </Button>
          <Button
            {...workspaceActionProps}
            loading={pending}
            onClick={() => void confirm()}
          >
            {content?.confirmLabel}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
