import { Center, Loader, type MantineSize } from '@mantine/core';

interface LoadingStateProps {
  /** Minimum height of the loading container */
  minHeight?: number | string;
  /** Loader size */
  size?: MantineSize;
}

/**
 * Centered loading indicator for async content.
 *
 * @example
 * ```tsx
 * <LoadingState />
 * <LoadingState minHeight={400} size="lg" />
 * ```
 */
export function LoadingState({ minHeight = 200, size = 'md' }: LoadingStateProps) {
  return (
    <Center mih={minHeight}>
      <Loader size={size} />
    </Center>
  );
}
