import { Anchor, Box, Stack, Text, Title } from '@mantine/core';
import type { ServerBuild } from '@romd/admin-api-client';
import { getServerInfo } from '@romd/admin-api-client';
import { useQuery } from '@tanstack/react-query';
import { Link } from 'react-router';
import classes from '../components/Workspace/Workspace.module.css';
import { ArtworkEnrichmentSettings } from './Settings/ArtworkEnrichmentSettings';
import { SignInMessageSettings } from './Settings/SignInMessageSettings';

// A stable release reuses its candidate's image, so the build can name an earlier tag than the release.
function buildLabel(build: ServerBuild) {
  return build.revision === build.tag
    ? `Build ${build.tag}`
    : `Build ${build.tag} (${build.revision.slice(0, 12)})`;
}

export function Settings() {
  const serverInfo = useQuery({
    queryKey: [
      'server',
      'info',
    ],
    queryFn: async ({ signal }) => {
      const { data } = await getServerInfo({
        signal,
        throwOnError: true,
      });
      return data;
    },
    staleTime: 0,
  });

  return (
    <Stack
      className={classes.page}
      gap="lg"
    >
      <div className={classes.hero}>
        <Title
          order={1}
          className={classes.heading}
        >
          Settings
        </Title>
      </div>
      <section className={classes.panel}>
        <Title
          order={2}
          className={classes.sectionHeading}
        >
          Server
        </Title>
        <Box role="status">
          {serverInfo.data ? (
            <>
              {serverInfo.data.release && (
                <Text
                  size="sm"
                  mt="xs"
                >
                  {`Version ${serverInfo.data.release}`}
                </Text>
              )}
              <Text
                size="sm"
                mt="xs"
                c="dimmed"
              >
                {buildLabel(serverInfo.data.build)}
              </Text>
            </>
          ) : (
            <Text
              size="sm"
              mt="xs"
            >
              {serverInfo.isError ? 'Version unavailable' : 'Version loading…'}
            </Text>
          )}
        </Box>
        <SignInMessageSettings />
      </section>
      <section className={classes.panel}>
        <Title
          order={2}
          className={classes.sectionHeading}
        >
          Automation
        </Title>
        <Text
          size="sm"
          c="dimmed"
          mt="xs"
        >
          Control artwork acquisition during enrichment across this installation. Existing artwork
          is preserved.
        </Text>
        <ArtworkEnrichmentSettings />
        <Anchor
          component={Link}
          to="/integrations"
          size="sm"
        >
          Manage provider connections
        </Anchor>
      </section>
    </Stack>
  );
}
