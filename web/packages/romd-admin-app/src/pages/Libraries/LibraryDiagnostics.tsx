import { Accordion, Alert, Badge, Group, Skeleton, Stack, Text } from '@mantine/core';
import { useState } from 'react';
import { TitleSearchSelect } from '../../components/TitleSearchSelect';
import { useLibraryTitleReleaseDiagnostics } from '../../hooks/api/useLibraryManagement';

export function LibraryDiagnostics({ libraryId }: { libraryId: string }) {
  const [titleId, setTitleId] = useState<string | null>(null);
  const query = useLibraryTitleReleaseDiagnostics(libraryId, titleId);
  return (
    <Accordion
      variant="separated"
      radius="md"
    >
      <Accordion.Item value="diagnostics">
        <Accordion.Control>Missing a game? Check its visibility</Accordion.Control>
        <Accordion.Panel>
          <Stack>
            <Text
              size="sm"
              c="dimmed"
            >
              Search for a game to inspect its releases against the saved library rules. Unsaved changes are not reflected here.
            </Text>
            <TitleSearchSelect
              label="Inspect a title"
              value={titleId}
              onChange={(id) => setTitleId(id)}
            />
            {titleId && query.isPending && <Skeleton h={70} />}
            {query.isError && <Alert color="red">Could not load release visibility.</Alert>}
            {titleId && query.isSuccess && query.data.length === 0 && (
              <Text
                size="sm"
                c="dimmed"
              >
                No release results for this title. It may be outside this library’s game selection
                or blocked by an audience rule.
              </Text>
            )}
            {query.data?.map((row) => (
              <Group
                key={row.id}
                justify="space-between"
              >
                <Stack gap={2}>
                  <Text
                    size="sm"
                    fw={550}
                  >
                    {row.name}
                  </Text>
                  <Text
                    size="xs"
                    c="dimmed"
                  >
                    {row.blockReason ?? row.exposureReason}
                  </Text>
                </Stack>
                <Badge color={row.isExposed ? 'teal' : 'gray'}>
                  {row.isExposed ? 'Visible' : 'Hidden'}
                </Badge>
              </Group>
            ))}
          </Stack>
        </Accordion.Panel>
      </Accordion.Item>
    </Accordion>
  );
}
