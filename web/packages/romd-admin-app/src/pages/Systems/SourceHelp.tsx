import { ActionIcon, Popover, Stack, Text } from '@mantine/core';
import { IconHelpCircle } from '@tabler/icons-react';
import { useState } from 'react';

export function SourceHelp() {
  const [opened, setOpened] = useState(false);
  return <Popover opened={opened} onChange={setOpened} width={300} withArrow position="bottom-start">
    <Popover.Target><ActionIcon aria-label="About sources" title="About sources" variant="subtle" color="gray" onClick={() => setOpened((value) => !value)}><IconHelpCircle size={19} /></ActionIcon></Popover.Target>
    <Popover.Dropdown><Stack gap="xs">
      <Text fw={600}>Sources</Text>
      <Text size="sm">A source describes releases and their expected files. Its installed DAT is a version of that description, not a collection of ROM files.</Text>
      <Text size="sm">An entry can describe a game, disc, or BIOS set. Several entries can belong to one title, and sources can overlap.</Text>
      <Text size="sm" c="dimmed">Subscriptions check for new DAT versions. Updates require review before replacing the installed version.</Text>
    </Stack></Popover.Dropdown>
  </Popover>;
}
