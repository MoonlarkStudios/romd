import { Badge } from '@mantine/core';
import { IconChevronRight } from '@tabler/icons-react';
import type { MouseEventHandler, ReactNode } from 'react';
import { Link, type To } from 'react-router';
import classes from './RequirementList.module.css';

interface LinkedCount {
  label: string;
  to: To;
  count: number;
  icon: ReactNode;
}

export function LinkedCountList({
  items,
  state,
  onOpen,
}: {
  items: LinkedCount[];
  state?: unknown;
  onOpen?: MouseEventHandler<HTMLAnchorElement>;
}) {
  return (
    <nav aria-label="Title sections">
      <ul className={classes.list}>
        {items.map(({ label, to, count, icon }) => (
          <li key={label}>
            <Link
              to={to}
              state={state}
              onClick={onOpen}
              className={classes.sectionLink}
            >
              {icon}
              <span>{label}</span>
              <Badge
                variant="light"
                color="gray"
                size="sm"
              >
                {count.toLocaleString()}
              </Badge>
              <IconChevronRight
                size={15}
                aria-hidden="true"
              />
            </Link>
          </li>
        ))}
      </ul>
    </nav>
  );
}
