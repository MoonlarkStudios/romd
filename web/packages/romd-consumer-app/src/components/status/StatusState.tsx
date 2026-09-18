import { Button, Loader, Stack, Text } from '@mantine/core';
import { IconAlertCircle, IconRefresh } from '@tabler/icons-react';
import type { ReactNode } from 'react';

export type StatusStateProps =
  | { kind: 'loading'; label?: string }
  | { kind: 'error'; message: string; onRetry?: () => void; retrying?: boolean }
  | { kind: 'empty'; icon?: ReactNode; title: string; message?: string; action?: ReactNode };

/**
 * The consumer app's single vocabulary for async states — mirrors the
 * console's ConsoleStatusState (icon or spinner, title, muted line, action).
 * Use this instead of bare Loaders or ad-hoc Alerts.
 */
export function StatusState(props: StatusStateProps) {
  if (props.kind === 'loading') {
    return (
      <Stack
        align="center"
        gap="sm"
        py={80}
        role="status"
      >
        <Loader color="mint" />
        {props.label && <Text className="romd-metadata">{props.label}</Text>}
      </Stack>
    );
  }

  if (props.kind === 'error') {
    return (
      <Stack
        align="center"
        gap="sm"
        py={80}
        role="alert"
      >
        <IconAlertCircle
          size={28}
          color="var(--romd-warning)"
          aria-hidden
        />
        <Text
          fw={700}
          ta="center"
        >
          Something went wrong
        </Text>
        <Text
          size="sm"
          c="dimmed"
          ta="center"
          maw={420}
        >
          {props.message}
        </Text>
        {props.onRetry && (
          <Button
            variant="light"
            color="mint"
            leftSection={<IconRefresh size={16} />}
            onClick={props.onRetry}
            loading={props.retrying}
            mt="xs"
          >
            Try again
          </Button>
        )}
      </Stack>
    );
  }

  return (
    <Stack
      align="center"
      gap="sm"
      py={80}
    >
      {props.icon && (
        <Text
          span
          c="dimmed"
          style={{ display: 'grid', placeItems: 'center' }}
        >
          {props.icon}
        </Text>
      )}
      <Text
        className="romd-card-title"
        ta="center"
      >
        {props.title}
      </Text>
      {props.message && (
        <Text
          size="sm"
          c="dimmed"
          ta="center"
          maw={420}
        >
          {props.message}
        </Text>
      )}
      {props.action}
    </Stack>
  );
}
