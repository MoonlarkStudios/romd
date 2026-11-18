import { ActionIcon, Popover, Stack, Text } from '@mantine/core';
import { IconHelpCircle } from '@tabler/icons-react';
import { useState } from 'react';

export function SystemHelp() {
  const [opened, setOpened] = useState(false);
  return (
    <Popover
      opened={opened}
      onChange={setOpened}
      width={300}
      withArrow
      position="bottom-start"
    >
      <Popover.Target>
        <ActionIcon
          variant="subtle"
          color="gray"
          aria-label="About systems"
          title="About systems"
          onClick={() => setOpened((value) => !value)}
        >
          <IconHelpCircle size={19} />
        </ActionIcon>
      </Popover.Target>
      <Popover.Dropdown>
        <Stack gap="xs">
          <Text fw={600}>Systems</Text>
          <Text size="sm">
            A system is a gaming platform, such as PlayStation. Adding one puts it in your Systems
            list.
          </Text>
          <Text size="sm">
            A catalog source, such as Redump, describes releases and expected files for a system. A
            DAT is the document installed for that source. A subscription checks for new DAT
            versions for you to review.
          </Text>
          <Text
            size="sm"
            c="dimmed"
          >
            Tracking chooses the titles you care about. Titles with files may still have incomplete
            releases.
          </Text>
        </Stack>
      </Popover.Dropdown>
    </Popover>
  );
}
