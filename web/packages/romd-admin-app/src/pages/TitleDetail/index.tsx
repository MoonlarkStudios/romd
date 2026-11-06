import {
  ActionIcon,
  Anchor,
  Badge,
  Breadcrumbs,
  Group,
  Skeleton,
  Stack,
  Text,
  Title,
} from '@mantine/core';
import { IconArrowLeft, IconShieldCheck, IconStack } from '@tabler/icons-react';
import { Link, Navigate, useLocation, useNavigate, useParams, useSearchParams } from 'react-router';
import { TitleHero } from '../../components/Title/TitleHero';
import { TitleWorkbench } from '../../components/Title/TitleWorkbench';
import { normalizeLegacyTitleDetailParams } from '../../components/Title/useTitleEditorParams';
import workspace from '../../components/Workspace/Workspace.module.css';
import { useTitleDetail } from '../../hooks/api/useTitleDetail';
import { ContentRatingsPanel } from './ContentRatingsPanel';
import { DiscSetsPanel } from './DiscSetsPanel';
import { ReleasesPanel } from './ReleasesPanel';

function TitleDetailSkeleton() {
  return (
    <Stack
      gap="md"
      className={workspace.page}
    >
      <Skeleton
        h={20}
        w={200}
      />
      <Skeleton h={240} />
      <Skeleton h={38} />
      <Skeleton h={180} />
    </Stack>
  );
}

/**
 * Title detail page - the "Convergence Point" showing metadata, releases, and files.
 */
export function TitleDetail() {
  const { titleId } = useParams<{
    titleId: string;
  }>();
  const navigate = useNavigate();
  const location = useLocation();
  const returnTo: unknown = location.state?.catalogReturnTo;
  const catalogReturnTo =
    typeof returnTo === 'string' && /^\/catalog(?:\?|$)/.test(returnTo) ? returnTo : '/catalog';
  const [params] = useSearchParams();
  const legacyParams = normalizeLegacyTitleDetailParams(params);

  const { data: title, isLoading, isError } = useTitleDetail(titleId);

  if (legacyParams)
    return (
      <Navigate
        replace
        to={{
          search: legacyParams.toString(),
        }}
        state={location.state}
      />
    );

  if (isLoading) return <TitleDetailSkeleton />;

  if (isError || !title || !titleId) {
    return (
      <Stack gap="lg">
        <Group>
          <ActionIcon
            variant="subtle"
            onClick={() => navigate(-1)}
          >
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
    <Stack
      gap="md"
      className={workspace.page}
    >
      {/* Breadcrumb Navigation */}
      <Breadcrumbs>
        <Anchor
          component={Link}
          to={catalogReturnTo}
          size="sm"
          c="dimmed"
        >
          Catalog
        </Anchor>
        <Text size="sm">{title.name}</Text>
      </Breadcrumbs>

      {/* Hero Section */}
      <TitleHero
        title={title}
        titleId={titleId}
      />

      <TitleWorkbench
        titleId={titleId}
        frame="page"
        density="comfortable"
        extraSections={[
          {
            value: 'releases',
            icon: <IconStack size={16} />,
            label: (
              <>
                Releases
                <Badge
                  size="sm"
                  variant="light"
                  ml="xs"
                >
                  {releaseCount}
                </Badge>
              </>
            ),
            content: (
              <ReleasesPanel
                key={titleId}
                titleId={titleId}
                preference={title}
                releases={title.releases ?? []}
              />
            ),
          },
          {
            value: 'discs',
            label: 'Discs',
            content: <DiscSetsPanel titleId={titleId} />,
          },
          {
            value: 'ratings',
            icon: <IconShieldCheck size={16} />,
            label: (
              <>
                Ratings
                <Badge
                  size="sm"
                  variant="light"
                  ml="xs"
                >
                  {ratingCount}
                </Badge>
              </>
            ),
            content: (
              <ContentRatingsPanel
                title={title}
                titleId={titleId}
              />
            ),
          },
        ]}
      />
    </Stack>
  );
}
