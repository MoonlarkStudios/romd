import { useElementSize } from '@mantine/hooks';
import { type KeyboardEvent, useEffect, useLayoutEffect, useRef, useState } from 'react';
import { useLocation, useSearchParams } from 'react-router';

export function useInspectorSize(minWidth = 880) {
  const { ref, width } = useElementSize<HTMLDivElement>();
  return {
    ref,
    width,
    wide: width >= minWidth,
    measured: width > 0,
  };
}

function rowTrigger(list: ParentNode, id: string) {
  return Array.from(list.querySelectorAll<HTMLElement>('[data-inspector-row]'))
    .find((row) => row.dataset.inspectorRow === id)
    ?.querySelector<HTMLElement>('[data-inspector-trigger]');
}

interface InspectorSelectionOptions {
  param: string;
  ids: readonly string[];
  wide?: boolean;
  defaultFirst?: boolean;
  allowUnlisted?: boolean;
  onNarrowOpen?: (id: string) => void;
  canChange?: () => boolean;
  beforeChange?: (action: () => void) => void;
  panelId?: string;
  clearParams?: readonly string[];
}

export function useInspectorSelection({
  param,
  ids,
  wide = true,
  defaultFirst = false,
  allowUnlisted = false,
  onNarrowOpen,
  canChange,
  beforeChange,
  panelId,
  clearParams,
}: InspectorSelectionOptions) {
  const [params, setParams] = useSearchParams();
  const { state } = useLocation();
  const [dismissed, setDismissed] = useState(false);
  const triggerRef = useRef<HTMLElement>(null);
  const revealRef = useRef<HTMLElement>(null);
  const requested = params.get(param);
  const fallback = defaultFirst && !dismissed ? ids[0] : undefined;
  const selectedId = wide
    ? requested && (allowUnlisted || ids.includes(requested))
      ? requested
      : fallback
    : undefined;

  const update = (id: string | null) => {
    const next = new URLSearchParams(params);
    for (const key of clearParams ?? []) next.delete(key);
    if (id) next.set(param, id);
    else next.delete(param);
    setParams(next, {
      replace: true,
      state,
      preventScrollReset: true,
    });
  };

  const change = (action: () => void) => {
    if (canChange?.() === false) return;
    if (beforeChange) beforeChange(action);
    else action();
  };

  const close = () => {
    if (!selectedId) return;
    change(() => {
      const trigger = rowTrigger(document, selectedId) ?? triggerRef.current;
      setDismissed(true);
      if (requested) update(null);
      const stacked =
        panelId && document.getElementById(panelId)?.closest('[data-inspector-stacked]');
      trigger?.focus({
        preventScroll: !stacked,
      });
      triggerRef.current = null;
    });
  };

  const select = (id: string, trigger?: HTMLElement) => {
    if (id === selectedId) return;
    change(() => {
      if (!wide) {
        onNarrowOpen?.(id);
        return;
      }
      triggerRef.current = trigger ?? null;
      revealRef.current = trigger ?? null;
      update(id);
    });
  };

  const move = (direction: -1 | 1) => {
    const index = selectedId ? ids.indexOf(selectedId) : -1;
    const next = ids[index < 0 ? (direction === 1 ? 0 : ids.length - 1) : index + direction];
    if (!next) return;
    const active = document.activeElement;
    const control =
      active instanceof HTMLElement &&
      active.hasAttribute('data-inspector-direction') &&
      panelId &&
      document.getElementById(panelId)?.contains(active)
        ? active
        : undefined;
    select(next, control);
  };

  const onListKeyDown = (event: KeyboardEvent<HTMLElement>) => {
    if (event.defaultPrevented || !wide || event.altKey || event.ctrlKey || event.metaKey) return;
    if (!(event.target instanceof HTMLElement) || !event.target.matches('[data-inspector-trigger]'))
      return;
    const id = event.target.closest<HTMLElement>('[data-inspector-row]')?.dataset.inspectorRow;
    if (!id || !ids.includes(id)) return;
    const direction = event.key === 'ArrowDown' ? 1 : event.key === 'ArrowUp' ? -1 : 0;
    if (!direction) return;
    event.preventDefault();
    const next = ids[ids.indexOf(id) + direction];
    if (!next) return;
    const list = event.currentTarget;
    change(() => {
      update(next);
      rowTrigger(list, next)?.focus();
    });
  };

  useLayoutEffect(() => {
    const trigger = revealRef.current;
    if (!selectedId || !trigger) return;
    revealRef.current = null;
    const panel = panelId ? document.getElementById(panelId) : null;
    const direction = trigger.dataset.inspectorDirection;
    if (direction === 'previous' || direction === 'next') {
      const sameControl = panel?.querySelector<HTMLElement>(
        `[data-inspector-direction="${direction}"]:not(:disabled)`,
      );
      (sameControl ?? rowTrigger(document, selectedId))?.focus();
    } else if (panel?.closest('[data-inspector-stacked]')) {
      panel.focus({
        preventScroll: true,
      });
      panel.scrollIntoView?.({
        block: 'start',
      });
    }
  }, [
    panelId,
    selectedId,
  ]);

  useEffect(() => {
    if (!selectedId) return;
    const onEscape = (event: globalThis.KeyboardEvent) => {
      if (event.key !== 'Escape' || event.defaultPrevented || event.repeat) return;
      // Menus, comboboxes and modal dialogs handle their own Escape first.
      if (
        event.target instanceof Element &&
        event.target.closest(
          '[role="dialog"], [role="menu"], [role="listbox"], [aria-expanded="true"][aria-haspopup]',
        )
      )
        return;
      event.preventDefault();
      close();
    };
    document.addEventListener('keydown', onEscape);
    return () => document.removeEventListener('keydown', onEscape);
  });

  return {
    selectedId,
    close,
    select,
    move,
    onListKeyDown,
    rowProps: (id: string) => ({
      'aria-current': id === selectedId ? (true as const) : undefined,
      'data-inspector-row': id,
    }),
  };
}
