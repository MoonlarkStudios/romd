import { useCallback, useEffect, useRef } from 'react';

export interface KeyboardShortcut {
  /** Key to trigger the action (e.g., 'j', 'k', 'Enter', 'ArrowUp') */
  key: string;
  /** Whether Cmd/Ctrl modifier is required */
  meta?: boolean;
  /** Whether Shift modifier is required */
  shift?: boolean;
  /** Whether Alt modifier is required */
  alt?: boolean;
  /** Action to perform */
  action: () => void;
  /** Description for help display */
  description?: string;
  /** Prevent default browser behavior */
  preventDefault?: boolean;
}

export interface UseKeyboardShortcutsOptions {
  /** Whether shortcuts are enabled */
  enabled?: boolean;
  /** Element to attach listener to (defaults to window) */
  scope?: 'window' | 'container';
}

/**
 * Hook for managing scoped keyboard shortcuts.
 * Supports modifier keys and provides a clean API for navigation and actions.
 *
 * @example
 * ```tsx
 * const containerRef = useKeyboardShortcuts([
 *   { key: 'j', action: () => selectNext() },
 *   { key: 'k', action: () => selectPrev() },
 *   { key: 'Enter', action: () => openDetail() },
 *   { key: 'd', meta: true, action: () => deleteSelected() },
 * ]);
 *
 * return <div ref={containerRef} tabIndex={0}>...</div>;
 * ```
 */
export function useKeyboardShortcuts(
  shortcuts: KeyboardShortcut[],
  options: UseKeyboardShortcutsOptions = {}
) {
  const { enabled = true, scope = 'window' } = options;
  const containerRef = useRef<HTMLDivElement>(null);

  const handleKeyDown = useCallback(
    (event: KeyboardEvent) => {
      if (!enabled) return;

      const target = event.target as HTMLElement;
      const isTyping =
        target.tagName === 'INPUT' ||
        target.tagName === 'TEXTAREA' ||
        target.isContentEditable;

      for (const shortcut of shortcuts) {
        // Special handling: "/" should only work when NOT typing
        if (shortcut.key === '/' && isTyping) {
          continue;
        }

        // Allow Escape to work even when typing (for clearing search)
        // Skip other shortcuts when typing
        if (isTyping && shortcut.key !== 'Escape') {
          continue;
        }
        const keyMatches =
          event.key.toLowerCase() === shortcut.key.toLowerCase() ||
          event.key === shortcut.key;

        const metaMatches = shortcut.meta
          ? event.metaKey || event.ctrlKey
          : !event.metaKey && !event.ctrlKey;

        const shiftMatches = shortcut.shift ? event.shiftKey : !event.shiftKey;
        const altMatches = shortcut.alt ? event.altKey : !event.altKey;

        if (keyMatches && metaMatches && shiftMatches && altMatches) {
          if (shortcut.preventDefault !== false) {
            event.preventDefault();
          }
          shortcut.action();
          return;
        }
      }
    },
    [enabled, shortcuts]
  );

  useEffect(() => {
    if (!enabled) return;

    if (scope === 'window') {
      window.addEventListener('keydown', handleKeyDown);
      return () => window.removeEventListener('keydown', handleKeyDown);
    }

    const container = containerRef.current;
    if (container) {
      container.addEventListener('keydown', handleKeyDown);
      return () => container.removeEventListener('keydown', handleKeyDown);
    }
  }, [enabled, scope, handleKeyDown]);

  return containerRef;
}

/**
 * Creates a standard set of navigation shortcuts for lists.
 */
export function createListNavigationShortcuts<T>(options: {
  items: T[];
  selectedIndex: number;
  onSelect: (index: number) => void;
  onOpen?: (item: T) => void;
  onToggleSelect?: (index: number) => void;
}): KeyboardShortcut[] {
  const { items, selectedIndex, onSelect, onOpen, onToggleSelect } = options;

  const shortcuts: KeyboardShortcut[] = [
    // Move down
    {
      key: 'j',
      description: 'Move down',
      action: () => {
        const nextIndex = Math.min(selectedIndex + 1, items.length - 1);
        onSelect(nextIndex);
      },
    },
    {
      key: 'ArrowDown',
      description: 'Move down',
      action: () => {
        const nextIndex = Math.min(selectedIndex + 1, items.length - 1);
        onSelect(nextIndex);
      },
    },
    // Move up
    {
      key: 'k',
      description: 'Move up',
      action: () => {
        const prevIndex = Math.max(selectedIndex - 1, 0);
        onSelect(prevIndex);
      },
    },
    {
      key: 'ArrowUp',
      description: 'Move up',
      action: () => {
        const prevIndex = Math.max(selectedIndex - 1, 0);
        onSelect(prevIndex);
      },
    },
    // Go to top
    {
      key: 'g',
      description: 'Go to top',
      action: () => onSelect(0),
    },
    // Go to bottom
    {
      key: 'G',
      shift: true,
      description: 'Go to bottom',
      action: () => onSelect(items.length - 1),
    },
  ];

  // Open item
  if (onOpen && selectedIndex >= 0 && selectedIndex < items.length) {
    shortcuts.push({
      key: 'Enter',
      description: 'Open selected',
      action: () => onOpen(items[selectedIndex]),
    });
  }

  // Toggle selection
  if (onToggleSelect) {
    shortcuts.push({
      key: ' ',
      description: 'Toggle selection',
      action: () => onToggleSelect(selectedIndex),
    });
  }

  return shortcuts;
}

/**
 * Creates filter shortcut keys (Cmd+1, Cmd+2, etc.)
 */
export function createFilterShortcuts<T extends string>(
  filters: T[],
  onFilterChange: (filter: T) => void
): KeyboardShortcut[] {
  return filters.map((filter, index) => ({
    key: String(index + 1),
    meta: true,
    description: `Filter: ${filter}`,
    action: () => onFilterChange(filter),
  }));
}

export default useKeyboardShortcuts;
