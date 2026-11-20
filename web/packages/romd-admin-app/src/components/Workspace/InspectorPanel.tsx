import { ActionIcon, Group, Title } from '@mantine/core';
import { IconChevronDown, IconChevronUp, IconX } from '@tabler/icons-react';
import { type ReactNode, useId } from 'react';
import classes from './InspectorLayout.module.css';
import workspace from './Workspace.module.css';

interface InspectorPanelProps {
  id?: string;
  title: ReactNode;
  leading?: ReactNode;
  children: ReactNode;
  footer?: ReactNode;
  onClose: () => void;
  navigation?: {
    onPrevious?: () => void;
    onNext?: () => void;
  };
}

export function InspectorPanel({
  id,
  title,
  leading,
  children,
  footer,
  onClose,
  navigation,
}: InspectorPanelProps) {
  const headingId = useId();
  return (
    <aside
      id={id}
      tabIndex={-1}
      className={`${workspace.panel} ${classes.panel}`}
      aria-labelledby={headingId}
    >
      <header className={classes.header}>
        {leading}
        <Title
          order={2}
          className={`${workspace.sectionHeading} ${classes.title}`}
          id={headingId}
        >
          {title}
        </Title>
        <Group
          gap={2}
          wrap="nowrap"
        >
          {navigation && (
            <>
              <ActionIcon
                variant="subtle"
                color="gray"
                aria-label="Preview previous item"
                data-inspector-direction="previous"
                disabled={!navigation.onPrevious}
                onClick={navigation.onPrevious}
              >
                <IconChevronUp size={16} />
              </ActionIcon>
              <ActionIcon
                variant="subtle"
                color="gray"
                aria-label="Preview next item"
                data-inspector-direction="next"
                disabled={!navigation.onNext}
                onClick={navigation.onNext}
              >
                <IconChevronDown size={16} />
              </ActionIcon>
            </>
          )}
          <ActionIcon
            variant="subtle"
            color="gray"
            aria-label="Close inspector"
            onClick={onClose}
          >
            <IconX size={17} />
          </ActionIcon>
        </Group>
      </header>
      <div className={classes.body}>{children}</div>
      {footer && <footer className={classes.footer}>{footer}</footer>}
    </aside>
  );
}
