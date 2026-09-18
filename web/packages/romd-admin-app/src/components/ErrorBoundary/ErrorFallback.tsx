import { Alert, Button, Code, Collapse, Group, Stack, Text } from '@mantine/core';
import { useDisclosure } from '@mantine/hooks';
import { IconAlertTriangle, IconRefresh } from '@tabler/icons-react';
import type { FallbackProps } from './ErrorBoundary';

interface ErrorFallbackProps extends FallbackProps {
  /** Custom title text */
  title?: string;
  /** Custom description text */
  description?: string;
  /** Whether to show error details toggle */
  showDetails?: boolean;
}

/**
 * Default error fallback component for ErrorBoundary.
 * Displays an error alert with a retry button and optional error details.
 *
 * @example
 * ```tsx
 * <ErrorBoundary FallbackComponent={ErrorFallback}>
 *   <MyComponent />
 * </ErrorBoundary>
 *
 * // With custom props
 * <ErrorBoundary
 *   FallbackComponent={(props) => (
 *     <ErrorFallback
 *       {...props}
 *       title="Failed to load data"
 *       description="Please check your connection and try again."
 *     />
 *   )}
 * >
 *   <MyComponent />
 * </ErrorBoundary>
 * ```
 */
export function ErrorFallback({
  error,
  resetErrorBoundary,
  title = 'Something went wrong',
  description = 'An unexpected error occurred. Please try again.',
  showDetails = true,
}: ErrorFallbackProps) {
  const [detailsOpened, { toggle }] = useDisclosure(false);

  return (
    <Alert icon={<IconAlertTriangle size={20} />} title={title} color="red" variant="light">
      <Stack gap="sm">
        <Text size="sm">{description}</Text>

        <Group gap="xs">
          <Button size="xs" variant="light" leftSection={<IconRefresh size={14} />} onClick={resetErrorBoundary}>
            Try again
          </Button>

          {showDetails && (
            <Button size="xs" variant="subtle" color="gray" onClick={toggle}>
              {detailsOpened ? 'Hide' : 'Show'} details
            </Button>
          )}
        </Group>

        {showDetails && (
          <Collapse in={detailsOpened}>
            <Code block mt="xs" style={{ whiteSpace: 'pre-wrap' }}>
              {error.message}
              {error.stack && `\n\n${error.stack}`}
            </Code>
          </Collapse>
        )}
      </Stack>
    </Alert>
  );
}
