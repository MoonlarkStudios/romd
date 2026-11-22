import { Button } from '@mantine/core';
import { IconRefresh } from '@tabler/icons-react';
import { StatusState } from './StatusState';

export function LibraryUnavailable({ retry }: { retry: () => void }) {
  return (
    <StatusState
      kind="empty"
      title="No library available yet"
      message="Ask your administrator to check your library access."
      action={
        <Button
          variant="light"
          color="mint"
          leftSection={<IconRefresh size={16} />}
          onClick={retry}
        >
          Refresh library
        </Button>
      }
    />
  );
}
