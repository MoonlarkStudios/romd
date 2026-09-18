import {
  ActionIcon,
  Badge,
  Breadcrumbs,
  Group,
  Skeleton,
  Stack,
  Tabs,
  Text,
  Title,
} from '@mantine/core';
import {
  IconArrowLeft,
  IconPhoto,
  IconShieldCheck,
  IconStack,
} from '@tabler/icons-react';
import { useNavigate, useParams, useSearchParams } from 'react-router';
import workspace from '../../components/Workspace/Workspace.module.css';
import { useTitleDetail } from '../../hooks/api/useTitleDetail';
import { ArtworkPanel } from './ArtworkPanel';
import { ContentRatingsPanel } from './ContentRatingsPanel';
import { MetadataPanel } from './MetadataPanel';
import { ReleasesPanel } from './ReleasesPanel';
import { TitleHero } from './TitleHero';

function TitleDetailSkeleton() {
  return (
    <Stack gap="xl">
      <Group align="flex-start" gap="xl" wrap="nowrap">
        <Skeleton w={200} h={267} radius="md" />
        <Stack gap="md" style={{ flex: 1 }}>
          <Skeleton h={40} w="60%" />
          <Group gap="xs">
            <Skeleton h={24} w={80} radius="xl" />
            <Skeleton h={24} w={60} radius="xl" />
          </Group>
          <Skeleton h={60} />
          <Group gap="xl">
            <Skeleton h={40} w={100} />
            <Skeleton h={40} w={100} />
            <Skeleton h={40} w={80} />
          </Group>
          <Skeleton h={40} />
        </Stack>
      </Group>
      <Stack gap="md">
        <Skeleton h={30} w={120} />
        <Skeleton h={60} />
        <Skeleton h={60} />
      </Stack>
    </Stack>
  );
}

/**
 * Title detail page - the "Convergence Point" showing metadata, releases, and files.
 */
export function TitleDetail() {
  const { titleId } = useParams<{ titleId: string }>();
  const navigate = useNavigate();
  const [params, setParams] = useSearchParams();
  const activeTab = params.get('tab') ?? 'metadata';
  const setActiveTab = (value: string | null) => { if (!value) return; const next = new URLSearchParams(params); next.set('tab', value); setParams(next); };

  const { data: title, isLoading, isError } = useTitleDetail(titleId);

  if (isLoading) {
    return (
      <Stack gap="lg">
        <Skeleton h={24} w={200} />
        <TitleDetailSkeleton />
      </Stack>
    );
  }

  if (isError || !title) {
    return (
      <Stack gap="lg">
        <Group>
          <ActionIcon variant="subtle" onClick={() => navigate(-1)}>
            <IconArrowLeft size={20} />
          </ActionIcon>
          <Title order={2}>Title Not Found</Title>
        </Group>
        <Text c="red">Failed to load title details. The title may not exist.</Text>
      </Stack>
    );
  }

  const releaseCount = title.releases?.length ?? 0;
  const ratingCount = title.contentRatings?.length ?? 0;

  return (
    <Stack gap="xl">
      {/* Breadcrumb Navigation */}
      <Breadcrumbs>
        <Text
          size="sm"
          c="dimmed"
          style={{ cursor: 'pointer' }}
          onClick={() => navigate('/catalog')}
        >
          Catalog
        </Text>
        <Text size="sm">{title.name}</Text>
      </Breadcrumbs>

      {/* Hero Section */}
      <TitleHero title={title} titleId={titleId!} />

      {/* Tabbed Content */}
      <Tabs value={activeTab} onChange={setActiveTab} classNames={{ list: workspace.tabsList, tab: workspace.tab }}>
        <Tabs.List>
          <Tabs.Tab value="metadata">Metadata</Tabs.Tab>
          <Tabs.Tab value="releases" leftSection={<IconStack size={16} />}>
            Releases
            <Badge size="sm" variant="light" ml="xs">
              {releaseCount}
            </Badge>
          </Tabs.Tab>
          <Tabs.Tab value="artwork" leftSection={<IconPhoto size={16} />}>Artwork</Tabs.Tab>
          <Tabs.Tab value="ratings" leftSection={<IconShieldCheck size={16} />}>
            Ratings
            <Badge size="sm" variant="light" ml="xs">
              {ratingCount}
            </Badge>
          </Tabs.Tab>
        </Tabs.List>

        <Tabs.Panel value="metadata" pt="md">
          <MetadataPanel title={title} titleId={titleId!} />
        </Tabs.Panel>

        <Tabs.Panel value="releases" pt="md">
          <ReleasesPanel key={titleId} titleId={titleId!} releases={title.releases ?? []} />
        </Tabs.Panel>

        <Tabs.Panel value="artwork" pt="md">
          {activeTab === 'artwork' && <ArtworkPanel key={titleId} title={title} titleId={titleId!} />}
        </Tabs.Panel>


        <Tabs.Panel value="ratings" pt="md">
          <ContentRatingsPanel title={title} titleId={titleId!} />
        </Tabs.Panel>
      </Tabs>
    </Stack>
  );
}
