import { Alert, Button, Group, Text } from '@mantine/core';
import { useState } from 'react';

/**
 * A save found the title changed since the draft started and wrote nothing. The draft stays; Reload latest discards
 * it only after the curator confirms, then `onReload` refreshes the title and restarts the editor clean.
 */
export function TitleConflictAlert({
  draftLabel,
  onReload,
}: {
  draftLabel: string;
  onReload: () => Promise<void>;
}) {
  const [confirming, setConfirming] = useState(false);
  const [reloading, setReloading] = useState(false);
  const reload = async () => {
    setReloading(true);
    try {
      await onReload();
    } finally {
      setReloading(false);
    }
  };
  return (
    <Alert
      color="orange"
      mt="md"
      title="This title changed since you started editing."
    >
      {confirming ? (
        <>
          <Text size="sm">Reloading discards your unsaved {draftLabel}.</Text>
          <Group
            gap="xs"
            mt="xs"
          >
            <Button
              size="compact-sm"
              variant="default"
              disabled={reloading}
              onClick={() => setConfirming(false)}
            >
              Keep editing
            </Button>
            <Button
              size="compact-sm"
              color="red"
              loading={reloading}
              onClick={() => void reload()}
            >
              Reload latest
            </Button>
          </Group>
        </>
      ) : (
        <>
          <Text size="sm">
            Your draft is still here. Reload the latest values before saving again.
          </Text>
          <Button
            size="compact-sm"
            variant="light"
            color="orange"
            mt="xs"
            onClick={() => setConfirming(true)}
          >
            Reload latest
          </Button>
        </>
      )}
    </Alert>
  );
}
