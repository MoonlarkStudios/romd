import { Anchor, Stack, Text, Title } from '@mantine/core';
import { getServerInfo } from '@romd/admin-api-client';
import { useQuery } from '@tanstack/react-query';
import { Link } from 'react-router';
import classes from '../components/Workspace/Workspace.module.css';
import { ArtworkEnrichmentSettings } from './Settings/ArtworkEnrichmentSettings';
import { SignInMessageSettings } from './Settings/SignInMessageSettings';

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
        <Text
          size="sm"
          mt="xs"
          role="status"
        >
          Version {serverInfo.isError ? 'unavailable' : (serverInfo.data?.version ?? 'loading…')}
        </Text>
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
