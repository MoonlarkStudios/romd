import { Box, Skeleton, Stack, Text } from '@mantine/core';
import { useVirtualizer } from '@tanstack/react-virtual';
import { type ReactNode, useCallback, useEffect, useRef } from 'react';

/** Default row height in pixels (48px for comfortable touch targets) */
export const DEFAULT_ROW_HEIGHT = 48;

export interface VirtualListProps<T> {
  /** Items to render */
  items: T[];
  /** Height of each row in pixels (default: 48) */
  rowHeight?: number;
  /** Render function for each item */
  renderItem: (item: T, index: number, isSelected: boolean) => ReactNode;
  /** Key extractor for items */
  getItemKey: (item: T, index: number) => string;
  /** Currently selected item key (for keyboard navigation highlighting) */
  selectedKey?: string | null;
  /** Loading state */
  isLoading?: boolean;
  /** Empty state content */
  emptyContent?: ReactNode;
  /** Number of items to over-scan (render outside visible area for smoother scroll) */
  overscan?: number;
  /** Container height (default: 100%) */
  height?: string | number;
  /** Callback when an item is clicked */
  onItemClick?: (item: T, index: number) => void;
  /** Callback when an item is double-clicked */
  onItemDoubleClick?: (item: T, index: number) => void;
  /** Class name for the container */
  className?: string;
  /** Whether to show alternating row backgrounds */
  striped?: boolean;
  /** Whether to highlight on hover */
  highlightOnHover?: boolean;
  /** Callback when scroll position changes (for infinite loading) */
  onScrollEnd?: () => void;
  /** Threshold from end to trigger onScrollEnd (in pixels) */
  scrollEndThreshold?: number;
}

/**
 * High-performance virtual list component using TanStack Virtual.
 * Renders only visible items for efficient handling of large datasets (10,000+ items).
 *
 * @example
 * ```tsx
 * <VirtualList
 *   items={roms}
 *   getItemKey={(rom) => rom.id}
 *   renderItem={(rom, index, isSelected) => (
 *     <RomRow rom={rom} isSelected={isSelected} />
 *   )}
 *   selectedKey={selectedRomId}
 *   height="calc(100vh - 300px)"
 *   striped
 *   highlightOnHover
 * />
 * ```
 */
export function VirtualList<T>({
  items,
  rowHeight = DEFAULT_ROW_HEIGHT,
  renderItem,
  getItemKey,
  selectedKey,
  isLoading = false,
  emptyContent,
  overscan = 5,
  height = '100%',
  onItemClick,
  onItemDoubleClick,
  className,
  striped = false,
  highlightOnHover = false,
  onScrollEnd,
  scrollEndThreshold = 200,
}: VirtualListProps<T>) {
  const parentRef = useRef<HTMLDivElement>(null);

  const virtualizer = useVirtualizer({
    count: items.length,
    getScrollElement: () => parentRef.current,
    estimateSize: () => rowHeight,
    overscan,
    getItemKey: (index) => getItemKey(items[index], index),
  });

  const virtualItems = virtualizer.getVirtualItems();

  // Handle scroll end detection for infinite loading
  const handleScroll = useCallback(() => {
    if (!parentRef.current || !onScrollEnd) return;

    const { scrollTop, scrollHeight, clientHeight } = parentRef.current;
    const distanceFromEnd = scrollHeight - scrollTop - clientHeight;

    if (distanceFromEnd < scrollEndThreshold) {
      onScrollEnd();
    }
  }, [onScrollEnd, scrollEndThreshold]);

  useEffect(() => {
    const element = parentRef.current;
    if (!element || !onScrollEnd) return;

    element.addEventListener('scroll', handleScroll);
    return () => element.removeEventListener('scroll', handleScroll);
  }, [handleScroll, onScrollEnd]);

  // Scroll to selected item when it changes
  useEffect(() => {
    if (!selectedKey) return;

    const selectedIndex = items.findIndex(
      (item, index) => getItemKey(item, index) === selectedKey
    );

    if (selectedIndex >= 0) {
      virtualizer.scrollToIndex(selectedIndex, { align: 'auto' });
    }
  }, [selectedKey, items, getItemKey, virtualizer]);

  // Loading state
  if (isLoading && items.length === 0) {
    return (
      <Stack gap={0}>
        {Array.from({ length: 10 }).map((_, i) => (
          <Box key={i} p="xs" h={rowHeight}>
            <Skeleton height={rowHeight - 16} />
          </Box>
        ))}
      </Stack>
    );
  }

  // Empty state
  if (items.length === 0) {
    return (
      emptyContent ?? (
        <Box p="xl" ta="center">
          <Text c="dimmed">No items to display</Text>
        </Box>
      )
    );
  }

  return (
    <Box
      ref={parentRef}
      className={className}
      style={{
        height,
        overflow: 'auto',
        contain: 'strict',
      }}
    >
      <Box
        style={{
          height: `${virtualizer.getTotalSize()}px`,
          width: '100%',
          position: 'relative',
        }}
      >
        {virtualItems.map((virtualRow) => {
          const item = items[virtualRow.index];
          const key = getItemKey(item, virtualRow.index);
          const isSelected = selectedKey === key;
          const isOdd = virtualRow.index % 2 === 1;

          return (
            <Box
              key={virtualRow.key}
              data-index={virtualRow.index}
              ref={virtualizer.measureElement}
              onClick={() => onItemClick?.(item, virtualRow.index)}
              onDoubleClick={() => onItemDoubleClick?.(item, virtualRow.index)}
              style={{
                position: 'absolute',
                top: 0,
                left: 0,
                width: '100%',
                height: `${rowHeight}px`,
                transform: `translateY(${virtualRow.start}px)`,
                cursor: onItemClick ? 'pointer' : undefined,
                backgroundColor: isSelected
                  ? 'var(--mantine-color-blue-light)'
                  : striped && isOdd
                    ? 'var(--mantine-color-gray-light)'
                    : undefined,
                transition: 'background-color 150ms ease',
              }}
              onMouseEnter={(e) => {
                if (highlightOnHover && !isSelected) {
                  e.currentTarget.style.backgroundColor =
                    'var(--mantine-color-gray-light-hover)';
                }
              }}
              onMouseLeave={(e) => {
                if (highlightOnHover && !isSelected) {
                  e.currentTarget.style.backgroundColor =
                    striped && isOdd
                      ? 'var(--mantine-color-gray-light)'
                      : '';
                }
              }}
            >
              {renderItem(item, virtualRow.index, isSelected)}
            </Box>
          );
        })}
      </Box>
    </Box>
  );
}

export default VirtualList;
