import { Button, Group, Modal, Stack, Text } from '@mantine/core';
import { useCallback, useContext } from 'react';
import { UNSAFE_DataRouterContext, useBeforeUnload, useBlocker } from 'react-router';

export function UnsavedChanges({ dirty }: { dirty: boolean }) {
  const router = useContext(UNSAFE_DataRouterContext);
  return router ? <RouterUnsavedChanges dirty={dirty} /> : null;
}
function RouterUnsavedChanges({ dirty }: { dirty: boolean }) {
  const blocker = useBlocker(dirty);
  useBeforeUnload(useCallback((event: BeforeUnloadEvent) => {
    if (dirty) { event.preventDefault(); event.returnValue = ''; }
  }, [dirty]));
  return <Modal opened={blocker.state === 'blocked'} onClose={() => blocker.reset?.()} title="Discard unsaved changes?" centered>
    <Stack><Text>Your saved settings will stay as they are.</Text><Group justify="flex-end">
      <Button variant="default" onClick={() => blocker.reset?.()}>Keep editing</Button>
      <Button color="red" onClick={() => blocker.proceed?.()}>Discard changes</Button>
    </Group></Stack>
  </Modal>;
}
