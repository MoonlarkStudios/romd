import { ActionIcon, Popover, Stack, Text, Tooltip } from '@mantine/core';
import { IconHelpCircle } from '@tabler/icons-react';
import { useState } from 'react';

export function CurationHelp({ concept }: { concept: 'Collections' | 'Libraries' }) {
  const [opened, setOpened] = useState(false);
  return (
    <Popover opened={opened} onChange={setOpened} width={300} position="bottom-start" withArrow shadow="md">
      <Popover.Target>
        <Tooltip label={`About ${concept.toLowerCase()}`} disabled={opened}>
          <ActionIcon variant="subtle" color="gray" aria-label={`About ${concept.toLowerCase()}`} onClick={() => setOpened((value) => !value)}>
            <IconHelpCircle size={19} />
          </ActionIcon>
        </Tooltip>
      </Popover.Target>
      <Popover.Dropdown>
        <Stack gap="xs">
          <Text fw={600}>{concept}</Text>
          {concept === 'Libraries' ? <>
            <Text size="sm">A library defines the titles and collections available to its audience.</Text>
            <Text size="sm">Choose its titles, set access rules, and assign the people who use it.</Text>
            <Text size="sm" c="dimmed">Attach collections to organize its titles into ordered groups. The library's access rules still apply.</Text>
          </> : <>
            <Text size="sm">A collection is an ordered selection of titles, reusable across libraries.</Text>
            <Text size="sm">Choose its titles and their order, then attach it to the libraries where it belongs.</Text>
            <Text size="sm" c="dimmed">Creating a collection does not attach it to any library. Each library's access rules still apply.</Text>
          </>}
        </Stack>
      </Popover.Dropdown>
    </Popover>
  );
}
