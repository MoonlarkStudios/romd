import { Badge } from '@mantine/core';
import { type ReactNode, useLayoutEffect, useRef } from 'react';
import { Link, type To } from 'react-router';
import classes from './SectionTabs.module.css';
import workspace from './Workspace.module.css';

interface SectionItem {
  id: string;
  label: string;
  to: To;
  icon?: ReactNode;
  count?: number;
}

export function SectionTabs({
  label,
  items,
  current,
  state,
}: {
  label: string;
  items: SectionItem[];
  current: string;
  state?: unknown;
}) {
  const list = useRef<HTMLElement>(null);
  useLayoutEffect(() => {
    const strip = list.current;
    if (!strip) return;
    const active = Array.from(strip.children).find(
      (link) => link.getAttribute('data-section') === current,
    );
    if (!active) return;
    const viewport = strip.getBoundingClientRect();
    const item = active.getBoundingClientRect();
    if (item.left < viewport.left) strip.scrollLeft += item.left - viewport.left;
    else if (item.right > viewport.right) strip.scrollLeft += item.right - viewport.right;
  }, [
    current,
  ]);

  return (
    <nav
      ref={list}
      className={`${workspace.tabsList} ${classes.tabs}`}
      aria-label={label}
    >
      {items.map(({ id, label: text, to, icon, count }) => (
        <Link
          key={id}
          to={to}
          state={state}
          aria-current={id === current ? 'page' : undefined}
          data-current={id === current || undefined}
          data-section={id}
          className={`${workspace.tab} ${classes.link}`}
          preventScrollReset
        >
          {icon}
          <span>{text}</span>
          {count != null && (
            <Badge
              size="sm"
              variant="light"
              color="gray"
            >
              {count}
            </Badge>
          )}
        </Link>
      ))}
    </nav>
  );
}
