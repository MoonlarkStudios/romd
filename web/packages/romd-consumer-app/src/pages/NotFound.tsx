import { Button } from '@mantine/core';
import { IconDeviceGamepad2 } from '@tabler/icons-react';
import { Link } from 'react-router';
import { StatusState } from '../components/status/StatusState';

export function NotFound() {
  return (
    <StatusState
      kind="empty"
      icon={
        <IconDeviceGamepad2
          size={32}
          stroke={1.5}
        />
      }
      title="This page isn't in the collection"
      message="The link may be stale, or the page may have moved."
      action={
        <Button
          component={Link}
          to="/"
          variant="light"
          color="mint"
        >
          Back to Shelves
        </Button>
      }
    />
  );
}
