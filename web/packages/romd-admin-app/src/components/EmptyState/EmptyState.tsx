import { Box, Button, type MantineColor, type MantineSize, Stack, Text, ThemeIcon } from '@mantine/core';
import { IconInbox } from '@tabler/icons-react';
import type { ReactNode } from 'react';

interface EmptyStateAction {
  /** Button label */
  label: string;
  /** Click handler */
  onClick: () => void;
  /** Optional icon */
  icon?: ReactNode;
}

interface EmptyStateProps {
  /** Main title text */
  title?: string;
  /** Description text below title */
  description?: string;
  /** Custom icon element (defaults to IconInbox) */
  icon?: ReactNode;
  /** Size of the icon */
  iconSize?: number;
  /** Icon theme color */
  iconColor?: MantineColor;
  /** Primary action button */
  action?: EmptyStateAction;
  /** Secondary action button (subtle variant) */
  secondaryAction?: EmptyStateAction;
  /** Component size variant */
  size?: MantineSize;
  /** Additional content below description */
  children?: ReactNode;
}

const sizeStyles = {
  xs: { iconWrapper: 40, icon: 20, titleSize: 'sm' as const, descSize: 'xs' as const, py: 'md' as const },
  sm: { iconWrapper: 48, icon: 24, titleSize: 'md' as const, descSize: 'sm' as const, py: 'lg' as const },
  md: { iconWrapper: 64, icon: 32, titleSize: 'lg' as const, descSize: 'sm' as const, py: 'xl' as const },
  lg: { iconWrapper: 80, icon: 40, titleSize: 'xl' as const, descSize: 'md' as const, py: 'xl' as const },
  xl: { iconWrapper: 96, icon: 48, titleSize: 'xl' as const, descSize: 'md' as const, py: 'xl' as const },
};

/**
 * Displays a centered empty state with icon, title, description, and optional actions.
 * Use when a list, table, or section has no data to display.
 *
 * @example
 * ```tsx
 * // Basic usage
 * <EmptyState title="No items" description="Get started by adding your first item." />
 *
 * // With action
 * <EmptyState
 *   title="No ROMs uploaded"
 *   description="Upload ROM files to build your library."
 *   action={{ label: 'Upload Files', onClick: handleUpload }}
 * />
 *
 * // Custom icon and color
 * <EmptyState
 *   icon={<IconSearch size={32} />}
 *   iconColor="blue"
 *   title="No results found"
 *   description="Try adjusting your search terms."
 * />
 * ```
 */
export function EmptyState({
  title = 'No data',
  description,
  icon,
  iconSize,
  iconColor = 'gray',
  action,
  secondaryAction,
  size = 'md',
  children,
}: EmptyStateProps) {
  const styles = sizeStyles[size];
  const finalIconSize = iconSize ?? styles.icon;

  return (
    <Box py={styles.py}>
      <Stack align="center" gap="md">
        <ThemeIcon size={styles.iconWrapper} radius="xl" variant="light" color={iconColor}>
          {icon ?? <IconInbox size={finalIconSize} stroke={1.5} />}
        </ThemeIcon>

        <Stack align="center" gap={4}>
          <Text size={styles.titleSize} fw={500} c="dimmed">
            {title}
          </Text>
          {description && (
            <Text size={styles.descSize} c="dimmed" ta="center" maw={400}>
              {description}
            </Text>
          )}
        </Stack>

        {children}

        {(action || secondaryAction) && (
          <Stack gap="xs" align="center">
            {action && (
              <Button
                size={size === 'xs' || size === 'sm' ? 'xs' : 'sm'}
                leftSection={action.icon}
                onClick={action.onClick}
              >
                {action.label}
              </Button>
            )}
            {secondaryAction && (
              <Button
                size={size === 'xs' || size === 'sm' ? 'xs' : 'sm'}
                variant="subtle"
                leftSection={secondaryAction.icon}
                onClick={secondaryAction.onClick}
              >
                {secondaryAction.label}
              </Button>
            )}
          </Stack>
        )}
      </Stack>
    </Box>
  );
}
