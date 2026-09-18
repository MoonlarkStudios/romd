import {
  Alert,
  Button,
  Group,
  Select,
  SimpleGrid,
  Skeleton,
  Stack,
  Text,
  Title,
} from '@mantine/core';
import { IconDeviceGamepad2, IconEye } from '@tabler/icons-react';
import { useState } from 'react';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import { useLibraryAttachments, useLibraryPreview } from '../../hooks/api/useLibraryExperience';
import classes from './Libraries.module.css';
import { LibraryDiagnostics } from './LibraryDiagnostics';

export function LibraryPreview({ libraryId, name }: { libraryId: string; name: string }) {
  const [collection, setCollection] = useState<string | null>(null);
  const attachments = useLibraryAttachments(libraryId);
  const preview = useLibraryPreview(libraryId, collection ?? undefined);
  const items = preview.data?.pages.flatMap((p) => p.items) ?? [];
  return (
    <Stack gap="lg">
      <Group justify="space-between">
        <div>
          <Title
            order={2}
            className={classes.sectionHeading}
          >
            Through their eyes
          </Title>
          <Text
            c="dimmed"
            mt="xs"
          >
            Owned games visible to this audience, using the latest saved library results.
          </Text>
        </div>
        <Select
          aria-label="Preview collection"
          placeholder="All owned games"
          clearable
          w={260}
          value={collection}
          onChange={setCollection}
          data={(attachments.data ?? []).map((a) => ({
            value: a.collectionId,
            label: a.name,
          }))}
        />
      </Group>
      <div className={classes.preview}>
        <Group
          justify="space-between"
          mb="xl"
        >
          <div>
            <Text className={classes.eyebrow}>Audience preview</Text>
            <Title
              order={2}
              mt="xs"
            >
              {name}
            </Title>
          </div>
          <IconEye size={24} />
        </Group>
        {preview.isError ? (
          <Alert color="red">
            Could not load audience preview.{' '}
            <Button
              variant="subtle"
              onClick={() => preview.refetch()}
            >
              Retry
            </Button>
          </Alert>
        ) : preview.isPending ? (
          <Skeleton h={260} />
        ) : items.length === 0 ? (
          <div className={classes.empty}>
            <Title order={3}>No owned games visible yet</Title>
            <Text
              c="dimmed"
              mt="sm"
            >
              Check the game selection and content ratings in Games. For a collection, also check
              that it contains games you own.
            </Text>
          </div>
        ) : (
          <SimpleGrid
            cols={{
              base: 2,
              sm: 3,
              md: 4,
              lg: 6,
            }}
            spacing="lg"
          >
            {items.map((game) => (
              <div key={game.id}>
                {game.coverUrl ? (
                  <img
                    loading="lazy"
                    className={classes.gameArt}
                    src={game.coverUrl}
                    alt=""
                  />
                ) : (
                  <div className={classes.gameArt}>
                    <IconDeviceGamepad2 size={38} />
                  </div>
                )}
                <Text
                  fw={600}
                  size="sm"
                  mt="sm"
                  lineClamp={2}
                >
                  {game.name}
                </Text>
                <Text
                  c="dimmed"
                  size="xs"
                  mt={4}
                >
                  {game.platformName}
                </Text>
              </div>
            ))}
          </SimpleGrid>
        )}
        {preview.hasNextPage && (
          <Button
            variant="light"
            {...workspaceActionProps}
            mt="xl"
            loading={preview.isFetchingNextPage}
            onClick={() => preview.fetchNextPage()}
          >
            Show more games
          </Button>
        )}
      </div>
      <Text
        c="dimmed"
        size="xs"
      >
        This previews audience eligibility, not device compatibility. Collections retain their
        curated game order.
      </Text>
      <LibraryDiagnostics libraryId={libraryId} />
    </Stack>
  );
}
