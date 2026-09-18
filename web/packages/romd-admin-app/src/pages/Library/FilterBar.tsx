import { Button, Group, Kbd, Text } from '@mantine/core';
import {
  IconCheck,
  IconCircleDashed,
  IconQuestionMark,
  IconStack2,
} from '@tabler/icons-react';
import type { ReactNode } from 'react';

export interface FilterOption {
  value: string;
  label: string;
  count?: number;
  color?: string;
  icon?: ReactNode;
  shortcut?: string;
}

export interface FilterBarProps {
  /** Current active filter value */
  value: string;
  /** Callback when filter changes */
  onChange: (value: string) => void;
  /** Filter options to display */
  options: FilterOption[];
  /** Whether to show keyboard shortcuts */
  showShortcuts?: boolean;
}

function getDefaultIcon(value: string): ReactNode {
  switch (value.toLowerCase()) {
    case 'all':
      return <IconStack2 size={14} />;
    case 'cataloged':
      return <IconCheck size={14} />;
    case 'unrouted':
      return <IconCircleDashed size={14} />;
    case 'unidentified':
      return <IconQuestionMark size={14} />;
    default:
      return null;
  }
}

function getDefaultColor(value: string): string {
  switch (value.toLowerCase()) {
    case 'cataloged':
      return 'green';
    case 'unrouted':
      return 'orange';
    case 'unidentified':
      return 'red';
    default:
      return 'gray';
  }
}

/**
 * Filter bar component with keyboard shortcut hints.
 * Displays filter buttons with counts and optional keyboard shortcuts.
 *
 * @example
 * ```tsx
 * <FilterBar
 *   value={status}
 *   onChange={setStatus}
 *   options={[
 *     { value: 'all', label: 'All', count: 1000, shortcut: '1' },
 *     { value: 'cataloged', label: 'Cataloged', count: 800, shortcut: '2' },
 *   ]}
 *   showShortcuts
 * />
 * ```
 */
export function FilterBar({
  value,
  onChange,
  options,
  showShortcuts = true,
}: FilterBarProps) {
  return (
    <Button.Group>
      {options.map((option) => {
        const isActive = value === option.value;
        const color = option.color || getDefaultColor(option.value);
        const icon = option.icon || getDefaultIcon(option.value);

        return (
          <Button
            key={option.value}
            variant={isActive ? 'filled' : 'default'}
            color={isActive ? color : 'gray'}
            onClick={() => onChange(option.value)}
            leftSection={icon}
            styles={{
              root: {
                fontWeight: isActive ? 600 : 400,
              },
              section: {
                marginRight: 6,
              },
            }}
          >
            <Group gap={6} wrap="nowrap">
              <span>{option.label}</span>
              {option.count !== undefined && (
                <Text
                  component="span"
                  size="xs"
                  c={isActive ? 'inherit' : 'dimmed'}
                  opacity={isActive ? 0.8 : 1}
                >
                  ({option.count.toLocaleString()})
                </Text>
              )}
              {showShortcuts && option.shortcut && (
                <Kbd size="xs" ml={4}>
                  {option.shortcut}
                </Kbd>
              )}
            </Group>
          </Button>
        );
      })}
    </Button.Group>
  );
}

export default FilterBar;
