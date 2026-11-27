import { Anchor, Button, Group, Stack, Text, Title } from '@mantine/core';
import type { Library } from '@romd/admin-api-client';
import { Link } from 'react-router';
import workspace from '../../components/Workspace/Workspace.module.css';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import type { HealthStats } from '../../hooks/api/useHealthStats';

export function FirstLibrary({ library, health }: { library: Library; health?: HealthStats }) {
  const hasMatchedFiles = (health?.identifiedCount ?? 0) > 0;
  const hasFiles = (health?.totalRoms ?? 0) > 0;
  const libraryPath = `/libraries/${library.id}?tab=games`;
  return (
    <Stack
      className={workspace.panel}
      gap="sm"
    >
      <Title
        order={2}
        className={workspace.sectionHeading}
      >
        Set up your first library
      </Title>
      <Text size="sm">
        {hasMatchedFiles
          ? `Review the games in ${library.name}, then choose Publish library. After it publishes, sign in to the consumer portal with this account.`
          : `${library.name} is assigned to your account. Import games or check your import results, then review and publish the library.`}
      </Text>
      <Group gap="sm">
        {hasMatchedFiles ? (
          <Button
            component={Link}
            to={libraryPath}
            {...workspaceActionProps}
          >
            Review {library.name}
          </Button>
        ) : (
          <>
            <Button
              component={Link}
              to="/roms/import"
              {...workspaceActionProps}
            >
              Import files
            </Button>
            {!hasFiles && (
              <Anchor
                component={Link}
                to="/systems"
                size="sm"
              >
                Set up a system
              </Anchor>
            )}
            <Anchor
              component={Link}
              to={libraryPath}
              size="sm"
            >
              Open {library.name} draft
            </Anchor>
          </>
        )}
        <Anchor
          component={Link}
          to="/jobs?type=upload"
          size="sm"
        >
          Review import results
        </Anchor>
      </Group>
    </Stack>
  );
}
