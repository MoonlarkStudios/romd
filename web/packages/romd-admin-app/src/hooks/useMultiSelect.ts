import { type MouseEvent, useCallback, useMemo, useState } from 'react';

export interface UseMultiSelectOptions<T> {
  /** All items available for selection */
  items: T[];
  /** Function to get unique key from item */
  getItemKey: (item: T) => string;
  /** Initial selected keys */
  initialSelected?: Set<string>;
  /** Callback when selection changes */
  onSelectionChange?: (selectedKeys: Set<string>, selectedItems: T[]) => void;
}

export interface UseMultiSelectReturn<T> {
  /** Set of selected item keys */
  selectedKeys: Set<string>;
  /** Array of selected items */
  selectedItems: T[];
  /** Number of selected items */
  selectedCount: number;
  /** Check if a specific item is selected */
  isSelected: (key: string) => boolean;
  /** Toggle selection of a single item */
  toggle: (key: string) => void;
  /** Select a single item (clears other selections) */
  select: (key: string) => void;
  /** Handle click with shift-click range selection support */
  handleClick: (key: string, event: MouseEvent) => void;
  /** Select all items */
  selectAll: () => void;
  /** Clear all selections */
  clearSelection: () => void;
  /** Select items by keys */
  selectKeys: (keys: string[]) => void;
  /** Invert selection */
  invertSelection: () => void;
  /** Last clicked key (for shift-click range) */
  lastClickedKey: string | null;
}

/**
 * Hook for managing multi-selection with shift-click range selection support.
 *
 * @example
 * ```tsx
 * const {
 *   selectedKeys,
 *   selectedCount,
 *   isSelected,
 *   handleClick,
 *   selectAll,
 *   clearSelection,
 * } = useMultiSelect({
 *   items: roms,
 *   getItemKey: (rom) => rom.id,
 *   onSelectionChange: (keys, items) => console.log('Selected:', items),
 * });
 *
 * // In render:
 * <Checkbox
 *   checked={isSelected(rom.id)}
 *   onClick={(e) => handleClick(rom.id, e)}
 * />
 * ```
 */
export function useMultiSelect<T>({
  items,
  getItemKey,
  initialSelected = new Set(),
  onSelectionChange,
}: UseMultiSelectOptions<T>): UseMultiSelectReturn<T> {
  const [selectedKeys, setSelectedKeys] = useState<Set<string>>(initialSelected);
  const [lastClickedKey, setLastClickedKey] = useState<string | null>(null);

  // Build a map of key -> index for efficient lookups
  const keyToIndex = useMemo(() => {
    const map = new Map<string, number>();
    items.forEach((item, index) => {
      map.set(getItemKey(item), index);
    });
    return map;
  }, [items, getItemKey]);

  // Get selected items
  const selectedItems = useMemo(() => {
    return items.filter((item) => selectedKeys.has(getItemKey(item)));
  }, [items, selectedKeys, getItemKey]);

  // Update selection and notify
  const updateSelection = useCallback(
    (newKeys: Set<string>) => {
      setSelectedKeys(newKeys);
      if (onSelectionChange) {
        const selected = items.filter((item) => newKeys.has(getItemKey(item)));
        onSelectionChange(newKeys, selected);
      }
    },
    [items, getItemKey, onSelectionChange]
  );

  // Check if item is selected
  const isSelected = useCallback(
    (key: string) => selectedKeys.has(key),
    [selectedKeys]
  );

  // Toggle single item
  const toggle = useCallback(
    (key: string) => {
      const newKeys = new Set(selectedKeys);
      if (newKeys.has(key)) {
        newKeys.delete(key);
      } else {
        newKeys.add(key);
      }
      setLastClickedKey(key);
      updateSelection(newKeys);
    },
    [selectedKeys, updateSelection]
  );

  // Select single item (clears others)
  const select = useCallback(
    (key: string) => {
      setLastClickedKey(key);
      updateSelection(new Set([key]));
    },
    [updateSelection]
  );

  // Handle click with shift-click range selection
  const handleClick = useCallback(
    (key: string, event: MouseEvent) => {
      event.stopPropagation();

      // Shift+click for range selection
      if (event.shiftKey && lastClickedKey !== null) {
        const lastIndex = keyToIndex.get(lastClickedKey);
        const currentIndex = keyToIndex.get(key);

        if (lastIndex !== undefined && currentIndex !== undefined) {
          const startIndex = Math.min(lastIndex, currentIndex);
          const endIndex = Math.max(lastIndex, currentIndex);

          const newKeys = new Set(selectedKeys);
          for (let i = startIndex; i <= endIndex; i++) {
            const itemKey = getItemKey(items[i]);
            newKeys.add(itemKey);
          }
          updateSelection(newKeys);
          return;
        }
      }

      // Cmd/Ctrl+click for toggle
      if (event.metaKey || event.ctrlKey) {
        toggle(key);
        return;
      }

      // Regular click toggles
      toggle(key);
    },
    [lastClickedKey, keyToIndex, selectedKeys, items, getItemKey, toggle, updateSelection]
  );

  // Select all items
  const selectAll = useCallback(() => {
    const allKeys = new Set(items.map(getItemKey));
    updateSelection(allKeys);
  }, [items, getItemKey, updateSelection]);

  // Clear all selections
  const clearSelection = useCallback(() => {
    setLastClickedKey(null);
    updateSelection(new Set());
  }, [updateSelection]);

  // Select specific keys
  const selectKeys = useCallback(
    (keys: string[]) => {
      updateSelection(new Set(keys));
    },
    [updateSelection]
  );

  // Invert selection
  const invertSelection = useCallback(() => {
    const newKeys = new Set<string>();
    items.forEach((item) => {
      const key = getItemKey(item);
      if (!selectedKeys.has(key)) {
        newKeys.add(key);
      }
    });
    updateSelection(newKeys);
  }, [items, getItemKey, selectedKeys, updateSelection]);

  return {
    selectedKeys,
    selectedItems,
    selectedCount: selectedKeys.size,
    isSelected,
    toggle,
    select,
    handleClick,
    selectAll,
    clearSelection,
    selectKeys,
    invertSelection,
    lastClickedKey,
  };
}

export default useMultiSelect;
