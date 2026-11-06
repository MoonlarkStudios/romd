import { useCallback, useEffect, useState } from 'react';

export interface KeyboardShortcut {
  /** Key to trigger the action (e.g., 'j', 'k', 'Enter', 'ArrowUp', '?') */
  key: string;
  /** Whether Cmd/Ctrl modifier is required */
  meta?: boolean;
  /**
   * Whether Shift modifier is required. When omitted, Shift must be up, except for single non-letter keys such as
   * '?' whose character already reflects Shift.
   */
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
  /** Listen on window (default), or only for events inside the element that receives the returned ref. */
  scope?: 'window' | 'container';
}

/** Open overlays own the keyboard: Mantine Modal, Drawer, and Popover dropdowns, menus, and native dialogs. */
const overlaySelector = '[role="dialog"], [role="alertdialog"], [role="menu"], dialog[open]';

const isTypingTarget = (target: EventTarget | null) =>
  target instanceof HTMLElement &&
  (target.isContentEditable || target.closest('input, textarea, select') !== null);

const isDisplayed = (element: Element | null): boolean =>
  element === null ||
  (!element.hasAttribute('hidden') &&
    getComputedStyle(element).display !== 'none' &&
    isDisplayed(element.parentElement));

/** An overlay counts unless it contains the listening root, so a workspace inside a dialog keeps its shortcuts. */
const hasOpenOverlay = (root: Element | null) =>
  Array.from(document.querySelectorAll(overlaySelector)).some(
    (overlay) => !(root && overlay.contains(root)) && isDisplayed(overlay),
  );

const isSymbolKey = (key: string) => key.length === 1 && key.toLowerCase() === key.toUpperCase();

const matches = (shortcut: KeyboardShortcut, event: KeyboardEvent) =>
  event.key.toLowerCase() === shortcut.key.toLowerCase() &&
  (shortcut.meta ? event.metaKey || event.ctrlKey : !event.metaKey && !event.ctrlKey) &&
  ((shortcut.shift === undefined && isSymbolKey(shortcut.key)) ||
    Boolean(shortcut.shift) === event.shiftKey) &&
  Boolean(shortcut.alt) === event.altKey;

/**
 * Hook for managing scoped keyboard shortcuts. Suppression rules:
 * - While typing (input, textarea, select, or editable content), only Escape shortcuts run.
 * - While a dialog, drawer, menu, or popover is open, shortcuts without Cmd/Ctrl or Alt do not run; chorded
 *   shortcuts such as Cmd+J stay available as app commands.
 * - Shortcuts that do not declare Cmd/Ctrl or Alt never run while those modifiers are held.
 * - With `scope: 'container'`, only events whose target is inside the element given the returned callback ref run.
 *   The listener follows the element, so conditionally rendered containers bind when they mount.
 *
 * @example
 * ```tsx
 * const containerRef = useKeyboardShortcuts([
 *   { key: 'j', action: () => selectNext() },
 *   { key: 'k', action: () => selectPrev() },
 *   { key: 'Enter', action: () => openDetail() },
 *   { key: 'd', meta: true, action: () => deleteSelected() },
 * ], { scope: 'container' });
 *
 * return <div ref={containerRef} tabIndex={0}>...</div>;
 * ```
 */
export function useKeyboardShortcuts(
  shortcuts: KeyboardShortcut[],
  options: UseKeyboardShortcutsOptions = {},
) {
  const { enabled = true, scope = 'window' } = options;
  const [container, setContainer] = useState<HTMLElement | null>(null);

  const handleKeyDown = useCallback(
    (event: KeyboardEvent) => {
      if (!enabled) return;
      const root = scope === 'container' ? container : null;
      if (
        scope === 'container' &&
        !(root && event.target instanceof Node && root.contains(event.target))
      )
        return;
      const typing = isTypingTarget(event.target);
      const shortcut = shortcuts.find(
        (candidate) =>
          matches(candidate, event) &&
          (!typing || candidate.key === 'Escape') &&
          (candidate.meta || candidate.alt || !hasOpenOverlay(root)),
      );
      if (!shortcut) return;
      if (shortcut.preventDefault !== false) {
        event.preventDefault();
      }
      shortcut.action();
    },
    [
      enabled,
      scope,
      container,
      shortcuts,
    ],
  );

  useEffect(() => {
    if (!enabled) return;

    if (scope === 'window') {
      window.addEventListener('keydown', handleKeyDown);
      return () => window.removeEventListener('keydown', handleKeyDown);
    }

    if (container) {
      container.addEventListener('keydown', handleKeyDown);
      return () => container.removeEventListener('keydown', handleKeyDown);
    }
  }, [
    enabled,
    scope,
    container,
    handleKeyDown,
  ]);

  return setContainer;
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
  onFilterChange: (filter: T) => void,
): KeyboardShortcut[] {
  return filters.map((filter, index) => ({
    key: String(index + 1),
    meta: true,
    description: `Filter: ${filter}`,
    action: () => onFilterChange(filter),
  }));
}

export default useKeyboardShortcuts;
