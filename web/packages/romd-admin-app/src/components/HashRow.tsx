import { ActionIcon, Code, CopyButton, Group, Text, Tooltip } from '@mantine/core';
import { IconCheck, IconCopy } from '@tabler/icons-react';
import classes from './HashRow.module.css';

interface HashRowProps {
  label: string;
  value: string | null | undefined;
}

export function HashRow({ label, value }: HashRowProps) {
  return (
    <Group className={classes.row} wrap="nowrap" align="flex-start" gap="sm">
      <Text w={55} size="xs" c="dimmed" pt={7} style={{ flexShrink: 0 }}>{label}</Text>
      {value ? <Code className={classes.value}>
        <span className={classes.hash}>{value}</span>
        <CopyButton value={value} timeout={2000}>{({ copied, copy }) => (
        <Tooltip label={copied ? 'Copied' : `Copy ${label}`}>
          <ActionIcon className={classes.copy} data-copied={copied || undefined} aria-label={`Copy ${label}`} variant="subtle" color={copied ? 'teal' : 'gray'} onClick={copy}>
            {copied ? <IconCheck size={16} /> : <IconCopy size={16} />}
          </ActionIcon>
        </Tooltip>
        )}</CopyButton>
      </Code> : <Text size="sm" c="dimmed" pt={4}>Not recorded</Text>}
    </Group>
  );
}
