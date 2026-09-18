import { Alert, Anchor, Badge, Button, Group, Stack, Text, Title } from '@mantine/core';
import { IconArrowLeft, IconChevronRight } from '@tabler/icons-react';
import { useQuery } from '@tanstack/react-query';
import { Link, useParams } from 'react-router';
import { IgdbProviderCard } from '../../components/Integrations/IgdbProviderCard';
import { igdbSettingsQuery, steamGridDbSettingsQuery } from '../../components/Integrations/providerQueries';
import { SteamGridDbProviderCard } from '../../components/Integrations/SteamGridDbProviderCard';
import classes from '../../components/Workspace/Workspace.module.css';

function ProviderStatus({ providerId }: { providerId: string }) {
  const igdb = useQuery({ ...igdbSettingsQuery, enabled: providerId === 'igdb' });
  const steam = useQuery({ ...steamGridDbSettingsQuery, enabled: providerId === 'steamgriddb' });
  const query = providerId === 'igdb' ? igdb : steam;
  if (query.isPending) return <Text size="xs" c="dimmed">Loading status…</Text>;
  if (query.isError) return <Alert color="red" py="xs">Status unavailable. <Button size="compact-xs" variant="subtle" onClick={() => void query.refetch()}>Retry</Button></Alert>;
  const settings = query.data;
  return <Group gap="xs"><Badge color={settings.enabled ? 'teal' : 'gray'} variant="light">{settings.enabled ? 'Enabled' : 'Disabled'}</Badge>
    {settings.managedByDeployment && <Badge variant="outline" color="gray">Deployment managed</Badge>}
    <Text size="xs" c={settings.lastTestedAt && !settings.lastTestSucceeded ? 'red' : 'dimmed'}>{settings.lastTestedAt ? `${settings.lastTestSucceeded ? 'Connection verified' : 'Connection failed'} · ${new Date(settings.lastTestedAt).toLocaleString()}` : 'Connection not tested'}</Text>
  </Group>;
}

export function Integrations() {
  const { providerId } = useParams();
  const provider = providerId === 'igdb' ? 'IGDB' : providerId === 'steamgriddb' ? 'SteamGridDB' : null;
  return <Stack className={classes.page} gap="lg">
    {providerId && <Button component={Link} to="/integrations" variant="subtle" color="gray" px={0} w="fit-content" leftSection={<IconArrowLeft size={15} />}>Integrations</Button>}
    <div className={classes.hero}><Title order={1} className={classes.heading}>{provider ?? 'Integrations'}</Title></div>
    {!providerId ? <>
      <Text c="dimmed" size="sm">Connect the services ROMD uses to enrich your catalog.</Text>
      {[{ id: 'igdb', name: 'IGDB', detail: 'Game metadata, identification, and artwork.' }, { id: 'steamgriddb', name: 'SteamGridDB', detail: 'Community artwork for posters and heroes.' }].map(item => <Group key={item.id} className={classes.panel} justify="space-between">
        <Stack gap={4}><Anchor component={Link} to={`/integrations/${item.id}`} fw={600}>{item.name}</Anchor><Text size="sm" c="dimmed">{item.detail}</Text><ProviderStatus providerId={item.id} /></Stack>
        <Button component={Link} to={`/integrations/${item.id}`} variant="subtle" rightSection={<IconChevronRight size={16} />}>Configure {item.name}</Button>
      </Group>)}
      <Anchor component={Link} to="/settings" size="sm">Configure automatic artwork acquisition</Anchor>
    </> : providerId === 'igdb' ? <IgdbProviderCard /> : providerId === 'steamgriddb' ? <SteamGridDbProviderCard /> : <Text>Integration not found.</Text>}
  </Stack>;
}
