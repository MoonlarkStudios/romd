import { Badge, Button, Popover } from '@mantine/core';
import { useReducedMotion } from '@mantine/hooks';
import { romdMotion } from '@romd/foundation/tokens';
import { IconChevronDown } from '@tabler/icons-react';
import { type ReactNode, useState } from 'react';

interface SourceMarkerProps {
  label: string;
  value: string;
  children?: ReactNode | ((close: () => void) => ReactNode);
  disabled?: boolean;
}

export function SourceMarker({ label, value, children, disabled }: SourceMarkerProps) {
  const [opened, setOpened] = useState(false);
  const reducedMotion = useReducedMotion();
  const close = () => setOpened(false);
  if (!children) {
    return (
      <Badge
        variant="default"
        radius="sm"
        size="sm"
      >
        {value}
      </Badge>
    );
  }

  return (
    <Popover
      position="bottom-end"
      opened={opened}
      onChange={setOpened}
      trapFocus
      returnFocus
      withArrow
      width={300}
      transitionProps={{
        duration: reducedMotion ? 0 : romdMotion.durationMs.focus,
        timingFunction: romdMotion.easing.standard,
      }}
    >
      <Popover.Target>
        <Button
          aria-label={`${label} source: ${value}`}
          disabled={disabled}
          onClick={() => setOpened((current) => !current)}
          variant="default"
          size="compact-xs"
          radius="sm"
          rightSection={<IconChevronDown size={12} />}
        >
          {value}
        </Button>
      </Popover.Target>
      <Popover.Dropdown aria-label={`${label} source`}>
        {typeof children === 'function' ? children(close) : children}
      </Popover.Dropdown>
    </Popover>
  );
}
