import { Badge, Box, Button, Group, Menu, Stack, Text } from '@mantine/core';
import type { ConsumerReleaseDto } from '@romd/consumer-api-client';
import { IconCheck, IconChevronDown } from '@tabler/icons-react';
import { formatList } from '../../utils/library';
import classes from './ReleaseVersionMenu.module.css';

interface ReleaseVersionMenuProps {
  releases: ConsumerReleaseDto[];
  selectedRelease: ConsumerReleaseDto;
  defaultReleaseId: string | null;
  onSelectRelease: (id: string) => void;
}

function versionName(release: ConsumerReleaseDto) {
  return [formatList(release.regions, release.name), release.revision].filter(Boolean).join(' · ');
}

export function ReleaseVersionMenu({ releases, selectedRelease, defaultReleaseId, onSelectRelease }: ReleaseVersionMenuProps) {
  return <Menu position="bottom-start" width={320}>
    <Menu.Target>
      <Button variant="default" className={classes.trigger} classNames={{ label: classes.label }} rightSection={<IconChevronDown size={16} />}>
        <Stack component="span" gap={2}>
          <span>Version: {versionName(selectedRelease)}</span>
          <Text component="span" size="xs" c="dimmed">{formatList(selectedRelease.languages, 'Language unspecified')}</Text>
        </Stack>
      </Button>
    </Menu.Target>
    <Menu.Dropdown className={classes.dropdown}>
      <Menu.Label>Choose version</Menu.Label>
      {releases.map(release => <Menu.Item key={release.id}
        leftSection={release.id === selectedRelease.id ? <IconCheck size={16} color="var(--mantine-color-mint-4)" /> : <Box w={16} />}
        onClick={() => onSelectRelease(release.id)}>
        <Stack component="span" gap={4}>
          <Group component="span" gap="xs"><Text component="span" size="sm" fw={600}>{versionName(release)}</Text>{release.id === defaultReleaseId && <Badge component="span" size="xs" variant="light">Default</Badge>}</Group>
          <Text component="span" size="xs" c="dimmed">{formatList(release.languages, 'Language unspecified')}</Text>
        </Stack>
      </Menu.Item>)}
    </Menu.Dropdown>
  </Menu>;
}
